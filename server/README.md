# CSL VPN Control Plane

Go-based control plane for CSL VPN. This service is deliberately separate from the VPN data planes.

## Current milestone

- Go 1.27.3.
- PostgreSQL connection and readiness check.
- HTTP health/version endpoints.
- Initial schema for sites, devices, exclusive sessions, and audit events.
- Device enrollment is available at `POST /api/v1/enroll` using a single-use 64-character hex enrollment code. The client generates and persists its credential before enrollment; the server stores only its SHA-256 hash. Repeating enrollment with the same device fingerprint and credential returns the original VPN IP.
- Session leases are available at `POST /api/v1/sessions`; a device can hold only one unexpired lease. `POST /api/v1/heartbeat` renews a lease.
- A background cleanup job expires registrations after 14 days without a heartbeat and releases their IP addresses.
- Migrations `004_device_provisioning_outbox.sql` and `005_provisioning_job_leases.sql` add stable per-device VPN MAC addresses and a transactional provisioning outbox. Enrollment queues DHCP reservation and SoftEther account jobs in the same database transaction as device registration.
- `cmd/cslvpn-vpn-agent` is the first privileged VPN-host worker milestone. It claims only `dhcp_reservation` jobs over HTTPS, validates device/IP/MAC payloads, updates a dedicated dnsmasq include file atomically, runs `dnsmasq --test`, restarts dnsmasq, and restores the previous reservation file if validation or restart fails. It never executes shell strings and never claims SoftEther account jobs.
- SoftEther account provisioning and secure per-device credential delivery are not implemented. A device remains `pending` until both DHCP and SoftEther jobs are completed; do not enable VPN connections before `provisioning_status=ready`.

## Configuration

- `CSLVPN_LISTEN`: listen address (default `127.0.0.1:8088`).
- `CSLVPN_DATABASE_URL`: PostgreSQL connection URL (required).
- `CSLVPN_ADMIN_TOKEN`: administrator API secret, at least 32 characters (required).
- `CSLVPN_PROVISIONER_TOKEN`: separate VPN-host worker API secret, at least 32 characters (required). Store it only on the control-plane host and the dedicated VPN-host agent.
- VPN-host agent: `CSLVPN_SERVER_URL` (HTTPS API base URL), `CSLVPN_PROVISIONER_TOKEN`, and `CSLVPN_WORKER_ID` are required. Optional `CSLVPN_DNSMASQ_CONFIG` defaults to `/etc/dnsmasq.conf`; `CSLVPN_DHCP_RESERVATIONS_FILE` defaults to `/etc/dnsmasq.d/cslvpn-reservations.conf`. The dnsmasq main configuration must include the reservation file's directory using `conf-dir=`.

Do not expose the service directly to the Internet. Put it behind a TLS-terminating reverse proxy and restrict access to management routes.

## Run

```sh
cd server
go mod tidy
CSLVPN_DATABASE_URL='postgres://cslvpn:change-me@127.0.0.1:5432/cslvpn?sslmode=disable' CSLVPN_ADMIN_TOKEN='replace-with-a-random-secret-of-at-least-32-characters' CSLVPN_PROVISIONER_TOKEN='use-a-different-random-secret-of-at-least-32-characters' go run ./cmd/cslvpn-server
```

Apply migrations in numeric order using the reviewed SQL files in `migrations/`, including migrations 004 and 005 before deploying this version.

## API

- `GET /health/live`
- `GET /health/ready`
- `GET /api/v1/version`
- `GET /api/v1/device`: authenticated device profile, assigned VPN address/MAC, and provisioning state
- `POST /api/v1/admin/enrollment-tokens`: admin Bearer token; creates a one-time enrollment code bound to a device name. The plaintext token is returned only once.
- `POST /api/v1/enroll`: one-time-token enrollment; expects a 64-character hex `bootstrap_token`, `device_name`, a 32-byte hex `device_fingerprint`, and a client-generated 32-byte hex `device_credential`.
- `POST /api/v1/sessions`: device Bearer credential; expects `protocol` (`softether` or `wireguard`) and `transport` (`udp` or `tcp`). Returns a 90-second session token.
- `POST /api/v1/heartbeat`: device Bearer credential plus `session_token` in JSON; renews the session lease.
- `POST /api/v1/sessions/disconnect`: device Bearer credential plus `session_token` in JSON; closes that device's session lease.
- `POST /api/v1/agent/provisioning/claim`: dedicated provisioner Bearer token plus `worker_id` and optional `job_type` (`dhcp_reservation` or `softether_account`); leases one matching provisioning job for 60 seconds, or returns 204 when the queue is empty.
- `POST /api/v1/agent/provisioning/jobs/{id}/complete`: dedicated provisioner Bearer token plus `worker_id`, `success`, and optional `error`; acknowledges the lease and updates device provisioning state.

The API listens on loopback by default. Keep it behind a TLS reverse proxy and do not expose the service directly to the public Internet. Enrollment codes are single-use, expire after the configured period, and are bound to the intended device name. Never embed an enrollment code or administrator token in a distributable client installer.

## VPN-host DHCP worker

Build the worker on Linux x64 with:

```sh
cd server
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -o cslvpn-vpn-agent ./cmd/cslvpn-vpn-agent
```

Run it as a dedicated, root-owned systemd service because it must atomically write under `/etc/dnsmasq.d` and restart dnsmasq. Keep the unit non-interactive, restrict writable paths to the dedicated reservation file where practical, protect its environment file (`0600`), and do not expose a local command interface. The worker invokes fixed absolute paths for `dnsmasq` and `systemctl`; it does not invoke a shell. Configure the API URL as HTTPS and use a unique worker ID. The agent refuses to start if the dnsmasq main config does not include the reservation directory. The default reservation file is `/etc/dnsmasq.d/cslvpn-reservations.conf`.

The worker intentionally processes only DHCP reservation jobs. SoftEther jobs remain queued until the separate account-provisioning and encrypted credential-delivery flow is implemented. This means successful DHCP reservation alone never makes a device ready.
