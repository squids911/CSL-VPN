CREATE UNIQUE INDEX IF NOT EXISTS vpn_devices_credential_hash_uidx
    ON vpn_devices(credential_hash)
    WHERE credential_hash IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS vpn_sessions_lease_token_hash_uidx
    ON vpn_sessions(lease_token_hash);

CREATE INDEX IF NOT EXISTS vpn_sessions_expiry_idx
    ON vpn_sessions(expires_at)
    WHERE disconnected_at IS NULL;

ALTER TABLE vpn_devices
    ADD CONSTRAINT vpn_devices_ipv4_pool_check
    CHECK (
        vpn_ipv4 > inet '10.255.0.1'
        AND vpn_ipv4 < inet '10.255.3.255'
        AND vpn_ipv4 <<= inet '10.255.0.0/22'
    );
