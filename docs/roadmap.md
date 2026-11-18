# Implementation roadmap

This roadmap turns the four requested workstreams into reviewable increments. The current repository change is an initial scaffold, not a working VPN.

## Workstream 1 — Control Plane API and PostgreSQL

Done: ASP.NET Core health endpoints, PostgreSQL container, first schema draft, one-time enrollment codes, transactional IP allocation (FOR UPDATE SKIP LOCKED), opaque bearer credentials (SHA-256 at rest, shown once), single-session heartbeat, revocation, and a 14-day cleanup sweep.

Next: integration tests against a real database in CI, migration tooling for schema changes beyond the bootstrap migration, and rate limiting on `/enroll`.

## Workstream 2 — Ubuntu deployment and NPM

Scaffolded: Docker Compose binds the API to `10.254.100.252:8088`; PostgreSQL has no host-published port and persists in a named volume.

Next: provision on Ubuntu Server 26.04, verify firewall rules allow TCP 8088 only from NPM (`10.254.100.251`) and trusted management sources, configure NPM, and test backups/restores. Do not expose 8088 or 5432 to the public Internet.

## Workstream 3 — Windows client

Done (management plane): the service enrolls with a one-time code, persists the device identity under ProgramData, heartbeats the control plane, forgets rejected credentials, and mirrors status to a state file; the tray app displays it.

Prototype (data plane, behind `Tunnel:Enabled`): the service drives the
bundled SoftEther VPN Client — virtual NIC + one account per device, RADIUS
login with the device GUID and credential, static address from the control
plane. Not yet validated on real hardware.

Next: validate the data-plane prototype (SoftEther client orchestration,
RADIUS auth) on real hardware: MTU, reconnects, adapter lifecycle, address
reconciliation.

## Workstream 4 — Tests, CI, and operations documentation

Done: shared device-ID tests, token/crypto unit tests, client HTTP tests, API+PostgreSQL integration tests running against a real database in CI, and an Ubuntu deployment runbook.

Next: backup/restore checks, packaging/signing, load/rate-limit tests, and operator/admin workflows.

## Explicitly out of scope for this scaffold

- A production-ready Ethernet/L2 data plane.
- Site-to-site routing or address translation for overlapping physical LANs.
- An administration UI.
- A production enrollment/authentication workflow.
- Serving public HTTPS while the router still sends public TCP 443 to SoftEther.

These decisions should be designed and validated before treating the VPN as usable for real traffic.
