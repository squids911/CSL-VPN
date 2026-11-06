# CSL VPN Control Plane

Go-based control plane for CSL VPN. This service is deliberately separate from the VPN data planes.

## Current milestone

- Go 1.27.3.
- PostgreSQL connection and readiness check.
- HTTP health/version endpoints.
- Initial schema for sites, devices, exclusive sessions, and audit events.
- Device enrollment is available at `POST /api/v1/enroll` using a bootstrap token of at least 32 characters. The device credential is returned once; only its SHA-256 hash is stored.
- Session leases are available at `POST /api/v1/sessions`; a device can hold only one unexpired lease. `POST /api/v1/heartbeat` renews a lease.
- A background cleanup job expires registrations after 14 days without a heartbeat and releases their IP addresses.

## Configuration

- `CSLVPN_LISTEN`: listen address (default `127.0.0.1:8088`).
- `CSLVPN_DATABASE_URL`: PostgreSQL connection URL (required).
- `CSLVPN_ENROLLMENT_TOKEN`: bootstrap secret, at least 32 characters (required).

Do not expose the service directly to the Internet. Put it behind a TLS-terminating reverse proxy and restrict access to management routes.

## Run

```sh
cd server
go mod tidy
CSLVPN_DATABASE_URL='postgres://cslvpn:change-me@127.0.0.1:5432/cslvpn?sslmode=disable' \\
CSLVPN_ENROLLMENT_TOKEN='replace-with-a-random-secret-of-at-least-32-characters' \\
go run ./cmd/cslvpn-server
```

Apply migrations using the reviewed SQL files in `migrations/`.

## API

- `GET /health/live`
- `GET /health/ready`
- `GET /api/v1/version`
- `POST /api/v1/enroll`: bootstrap-token enrollment; expects `device_name` and a 32-byte hex `device_fingerprint`.
- `POST /api/v1/sessions`: device Bearer credential; expects `protocol` (`softether` or `wireguard`) and `transport` (`udp` or `tcp`). Returns a 90-second session token.
- `POST /api/v1/heartbeat`: device Bearer credential plus `session_token` in JSON; renews the session lease.

The API listens on loopback by default. Keep it behind a TLS reverse proxy and do not expose the service directly to the public Internet. Never embed the shared enrollment bootstrap secret in a distributable client installer; the current bootstrap flow is an operator-controlled development mechanism pending per-device one-time enrollment codes.
