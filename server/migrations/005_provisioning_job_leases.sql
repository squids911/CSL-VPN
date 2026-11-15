ALTER TABLE vpn_provisioning_jobs
    ADD COLUMN IF NOT EXISTS locked_by TEXT,
    ADD COLUMN IF NOT EXISTS locked_until TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS vpn_provisioning_jobs_lease_idx
    ON vpn_provisioning_jobs(locked_until)
    WHERE status = 'processing';
