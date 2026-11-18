-- 0002_exclude_gateway.sql
-- The SoftEther SecureNAT gateway lives at 10.255.0.1 (tap_tap_csl), which is
-- inside the client pool range 10.255.0.0/22. That address must never be
-- handed out to a device. This migration releases any accidental lease of
-- the gateway address and removes it from the pool permanently.

BEGIN;

-- If a device currently holds the gateway address, release the lease; the
-- device receives a fresh address on its next enrollment.
UPDATE address_pool
SET reserved_by = NULL, reserved_at = NULL
WHERE address = '10.255.0.1';

DELETE FROM address_pool WHERE address = '10.255.0.1';

COMMIT;
