ALTER TABLE vpn_devices
    ADD COLUMN IF NOT EXISTS vpn_mac MACADDR,
    ADD COLUMN IF NOT EXISTS provisioning_status TEXT NOT NULL DEFAULT 'pending'
        CHECK (provisioning_status IN ('pending', 'processing', 'ready', 'failed'));

-- Existing registrations receive a stable locally administered unicast MAC derived
-- from their immutable device UUID. New enrollments use the same derivation in Go.
UPDATE vpn_devices
SET vpn_mac = (
    '02:' ||
    substr(md5(id::text), 1, 2) || ':' ||
    substr(md5(id::text), 3, 2) || ':' ||
    substr(md5(id::text), 5, 2) || ':' ||
    substr(md5(id::text), 7, 2) || ':' ||
    substr(md5(id::text), 9, 2)
)::macaddr
WHERE vpn_mac IS NULL;

ALTER TABLE vpn_devices
    ALTER COLUMN vpn_mac SET NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS vpn_devices_vpn_mac_uidx
    ON vpn_devices(vpn_mac);

CREATE TABLE IF NOT EXISTS vpn_provisioning_jobs (
    id BIGSERIAL PRIMARY KEY,
    device_id UUID NOT NULL REFERENCES vpn_devices(id) ON DELETE CASCADE,
    job_type TEXT NOT NULL CHECK (job_type IN ('softether_account', 'dhcp_reservation')),
    payload JSONB NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'processing', 'completed', 'failed')),
    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    last_error TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS vpn_provisioning_jobs_pending_idx
    ON vpn_provisioning_jobs(created_at, id)
    WHERE status IN ('pending', 'failed');

-- Backfill jobs for registrations created before provisioning was introduced.
INSERT INTO vpn_provisioning_jobs (device_id, job_type, payload)
SELECT d.id, 'dhcp_reservation',
       jsonb_build_object('device_id', d.id, 'vpn_ipv4', d.vpn_ipv4::text,
                          'vpn_mac', d.vpn_mac::text)
FROM vpn_devices d
WHERE d.status = 'active'
  AND NOT EXISTS (
      SELECT 1 FROM vpn_provisioning_jobs j
      WHERE j.device_id = d.id AND j.job_type = 'dhcp_reservation'
  );

INSERT INTO vpn_provisioning_jobs (device_id, job_type, payload)
SELECT d.id, 'softether_account',
       jsonb_build_object('device_id', d.id, 'device_name', d.device_name)
FROM vpn_devices d
WHERE d.status = 'active'
  AND NOT EXISTS (
      SELECT 1 FROM vpn_provisioning_jobs j
      WHERE j.device_id = d.id AND j.job_type = 'softether_account'
  );
