# CSL VPN Control Plane

Go-based control plane for CSL VPN. This service is deliberately separate from the VPN data planes.

## Current milestone

- Go 1.27.3.
- PostgreSQL connection and readiness check.
- HTTP health/version endpoints.
- Initial schema for sites, devices, exclusive sessions, and audit events.
- No public enrollment endpoint is enabled yet. Device enrollment will be added with one-time bootstrap authorization, hashed device credentials, and transactional IP allocation.

## Configuration

- `CSLVPN_LISTEN`: listen address (default `127.0.0.1:8088`).
- `CSLVPN_DATABASE_URL`: PostgreSQL connection URL (required).

Do not expose the service directly to the Internet. Put it behind a TLS-terminating reverse proxy and restrict access to management routes.

## Run

```sh
cd server
go mod tidy
CSLVPN_DATABASE_URL='postgres://cslvpn:change-me@127.0.0.1:5432/cslvpn?sslmode=disable' go run ./cmd/cslvpn-server
```

Apply migrations using the reviewed SQL files in `migrations/`.
