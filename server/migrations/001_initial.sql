CREATE TABLE IF NOT EXISTS vpn_sites (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL UNIQUE,
    description TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS vpn_devices (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    device_name TEXT NOT NULL,
    device_name_normalized TEXT NOT NULL UNIQUE,
    device_fingerprint BYTEA NOT NULL UNIQUE,
    vpn_ipv4 INET NOT NULL UNIQUE,
    site_id UUID REFERENCES vpn_sites(id) ON DELETE RESTRICT,
    credential_hash BYTEA,
    status TEXT NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'active', 'disabled', 'expired')),
    last_seen_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS vpn_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    device_id UUID NOT NULL REFERENCES vpn_devices(id) ON DELETE CASCADE,
    protocol TEXT NOT NULL CHECK (protocol IN ('softether', 'wireguard')),
    transport TEXT NOT NULL CHECK (transport IN ('udp', 'tcp')),
    lease_token_hash BYTEA NOT NULL,
    connected_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    heartbeat_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ NOT NULL,
    disconnected_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS vpn_sessions_one_live_per_device
    ON vpn_sessions(device_id)
    WHERE disconnected_at IS NULL;

CREATE INDEX IF NOT EXISTS vpn_devices_last_seen_idx
    ON vpn_devices(last_seen_at)
    WHERE status = 'active';

CREATE TABLE IF NOT EXISTS vpn_audit_events (
    id BIGSERIAL PRIMARY KEY,
    actor_type TEXT NOT NULL,
    actor_id TEXT NOT NULL DEFAULT '',
    event_type TEXT NOT NULL,
    details JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
