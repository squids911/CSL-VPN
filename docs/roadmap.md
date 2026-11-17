# Implementation roadmap

This roadmap turns the four requested workstreams into reviewable increments. The current repository change is an initial scaffold, not a working VPN.

## Workstream 1 — Control Plane API and PostgreSQL

Scaffolded: ASP.NET Core health endpoints, PostgreSQL container, and first schema draft.

Next: one-time enrollment-code issuance/consumption, device registration, opaque per-device bearer credentials (hash at rest), IPAM transactions, heartbeat, a single-session lease, revocation, and 14-day cleanup. Add migration tooling before making schema changes beyond the first bootstrap migration.

## Workstream 2 — Ubuntu deployment and NPM

Scaffolded: Docker Compose binds the API to `10.254.100.252:8088`; PostgreSQL has no host-published port and persists in a named volume.

Next: provision on Ubuntu Server 26.04, verify firewall rules allow TCP 8088 only from NPM (`10.254.100.251`) and trusted management sources, configure NPM, and test backups/restores. Do not expose 8088 or 5432 to the public Internet.

## Workstream 3 — Windows client

Scaffolded: a service shell and a tray shell, both explicitly non-functional for VPN connectivity.

Next: select and prototype an L2-capable adapter/transport, define secure device enrollment and local identity persistence, then add connection lifecycle, status, upgrades, and recovery. A normal TUN adapter is not automatically equivalent to the required Ethernet/L2 VPN.

## Workstream 4 — Tests, CI, and operations documentation

Scaffolded: shared device-ID tests, a .NET CI workflow, and an Ubuntu deployment runbook.

Next: API/database integration tests, security tests, backup/restore checks, packaging/signing, and operator/admin workflows.

## Explicitly out of scope for this scaffold

- A production-ready Ethernet/L2 data plane.
- Site-to-site routing or address translation for overlapping physical LANs.
- An administration UI.
- A production enrollment/authentication workflow.
- Serving public HTTPS while the router still sends public TCP 443 to SoftEther.

These decisions should be designed and validated before treating the VPN as usable for real traffic.
