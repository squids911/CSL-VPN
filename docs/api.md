# Control Plane API reference

Base URL: `https://v.csl.by` (via NPM). Raw tokens and codes are shown exactly
once; the database stores only SHA-256 hashes. All errors are JSON:
`{"error": "<code>"}`.

## Public / device endpoints

### `POST /enroll`
Enrolls a device with a one-time enrollment code. Transactional: consumes the
code, creates (or re-validates) the device, assigns an address, issues a
credential.

Request:
```json
{ "code": "<one-time code>", "displayName": "laptop-01", "deviceId": "optional-preserved-guid" }
```

Response `200`:
```json
{ "deviceId": "…", "displayName": "…", "address": "10.255.0.1", "credential": "<bearer token, shown once>" }
```

Errors: `invalid_code`, `code_consumed`, `code_expired`, `device_revoked` (403),
`invalid_display_name`, `invalid_device_id`, `pool_exhausted` (503).

If `deviceId` matches an existing active device, enrollment is a re-issue:
the device keeps its address, previous credentials and sessions are revoked,
and a fresh credential is returned. This is how a reinstall keeps its VPN
address when the device identity was preserved.

### `POST /session/heartbeat`
Bearer-authenticated. Refreshes (or starts) the device's single active session
and updates `last_seen_at`. Response: `sessionId`, `startedAt`,
`lastHeartbeat`, `address`, `status`.

### `POST /session/disconnect`
Bearer-authenticated. Ends the active session. Response: `{"status":"disconnected"}`.

### `GET /device/status`
Bearer-authenticated. Returns `deviceId`, `displayName`, `enrolledAt`,
`lastSeenAt`, `address`, `sessionActive`.

### `GET /health/live`, `GET /health/ready`
Liveness and liveness+PostgreSQL readiness checks.

## Admin endpoints (header `X-Admin-Key: <ADMIN_API_KEY>`)

### `POST /admin/enrollment-codes?expiresInMinutes=60`
Creates a one-time enrollment code. `expiresInMinutes` is optional
(default 60, max 10080). Response contains the raw `code` once.

### `GET /admin/devices`
Lists up to 500 devices with address, session state, enrollment/last-seen/
revocation timestamps.

### `POST /admin/devices/{deviceId}/revoke`
Revokes a device: kills credentials and sessions and returns its address to
the pool. Idempotent (`already_revoked` on repeat).

## Background cleanup

`CleanupService` sweeps hourly: devices with no activity for 14 days are
revoked and their addresses return to the pool.
