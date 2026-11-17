-- 0001_initial.sql
-- Initial CSL VPN control-plane schema draft.
--
-- This file is mounted into the PostgreSQL container's
-- /docker-entrypoint-initdb.d/ and runs ONLY on the first start with an
-- empty data volume. Never edit it after it has been applied to a real
-- deployment; add a new reviewed migration instead.
--
-- Security note: enrollment codes and device credentials are stored only
-- as hashes. Raw secrets must be shown to the operator exactly once at
-- issuance time and must never be written to the database or logs.

BEGIN;

-- One row per enrolled device installation identity.
CREATE TABLE devices (
    id              UUID PRIMARY KEY,
    display_name    TEXT NOT NULL,
    enrolled_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at    TIMESTAMPTZ,
    revoked_at      TIMESTAMPTZ
);

-- One-time, operator-authorized enrollment codes. Only the hash is kept.
CREATE TABLE enrollment_codes (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code_hash       BYTEA NOT NULL UNIQUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at      TIMESTAMPTZ NOT NULL,
    consumed_by     UUID REFERENCES devices (id),
    consumed_at     TIMESTAMPTZ,
    CONSTRAINT enrollment_code_consumed_together
        CHECK ((consumed_by IS NULL) = (consumed_at IS NULL))
);

-- Opaque per-device bearer credentials. Only the hash is kept.
CREATE TABLE device_credentials (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    device_id       UUID NOT NULL REFERENCES devices (id) ON DELETE CASCADE,
    secret_hash     BYTEA NOT NULL UNIQUE,
    issued_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    revoked_at      TIMESTAMPTZ
);

CREATE INDEX device_credentials_device_id ON device_credentials (device_id);

-- The client VPN address pool (10.255.0.0/22). Each address may be
-- reserved by at most one device; released addresses return to the pool.
CREATE TABLE address_pool (
    address         INET PRIMARY KEY,
    reserved_by     UUID REFERENCES devices (id),
    reserved_at     TIMESTAMPTZ
);

-- Populate 10.255.0.1 - 10.255.3.254 (network and broadcast excluded).
INSERT INTO address_pool (address)
SELECT '10.255.0.0'::inet + i
FROM generate_series(1, 1022) AS i;

-- Session leases. A device may hold at most one active session.
CREATE TABLE sessions (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    device_id       UUID NOT NULL REFERENCES devices (id) ON DELETE CASCADE,
    started_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_heartbeat  TIMESTAMPTZ NOT NULL DEFAULT now(),
    ended_at        TIMESTAMPTZ
);

CREATE UNIQUE INDEX one_active_session_per_device
    ON sessions (device_id)
    WHERE ended_at IS NULL;

COMMIT;
