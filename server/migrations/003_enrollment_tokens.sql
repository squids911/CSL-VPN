CREATE TABLE IF NOT EXISTS vpn_enrollment_tokens (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    token_hash BYTEA NOT NULL UNIQUE,
    device_name_normalized TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    consumed_at TIMESTAMPTZ,
    created_by TEXT NOT NULL DEFAULT 'admin',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS vpn_enrollment_tokens_expiry_idx
    ON vpn_enrollment_tokens(expires_at)
    WHERE consumed_at IS NULL;
