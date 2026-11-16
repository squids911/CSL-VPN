# Implementation status

Updated: 2026-11-16

## Committed to develop

### Server
- Go 1.27.3 module and Linux container build.
- HTTP server lifecycle, structured request logging, security response headers.
- PostgreSQL connection/readiness handling.
- Initial schema for sites, devices, unique device names/fingerprints, VPN addresses, exclusive live sessions, and audit events.
- Liveness, readiness, and API version endpoints.
- Device enrollment, persistent IP allocation, credential hashing, session leases, heartbeats, exclusive active-session enforcement, and 14-day inactive-device cleanup.
- Enrollment/session API validation tests and additional database indexes/constraints.
- Windows service performs HTTPS enrollment using its per-device one-time code.

### Windows client
- .NET 10 shared protocol/transport model.
- Windows Service host shell.
- System-tray application shell.
- HTTPS management API client for enrollment, device profile, session start, and heartbeat.
- DPAPI-protected persistent device identity and client-generated credential.
- Windows service performs idempotent first enrollment and retains the assigned address.
- Build workflow for Linux server and Windows client.

### Control-plane API now committed
- Admin-protected issuance of single-use, expiring enrollment codes bound to device names.
- Transactional enrollment-token consumption with SHA-256 fingerprint validation.
- Transactional VPN IP allocation from `10.255.0.2` through `10.255.3.254`.
- Client-generated persistent per-device credential; server stores only SHA-256 hash.
- Idempotent enrollment when the same device fingerprint and credential are presented again.
- Device-authenticated session lease creation and heartbeat.
- Exclusive unexpired session enforcement and stale lease cleanup.
- Hourly 14-day inactive-device expiry with audit event and IP reclamation.
- HTTP validation tests for enrollment and credential-gated session routes, deterministic MAC allocation, and provisioning-worker credential isolation.
- Transactional provisioning outbox for DHCP reservation and SoftEther account setup.
- Stable per-device locally administered VPN MAC allocation and authenticated device-profile delivery.
- Dedicated provisioner-worker credential, leased job-claim API with an explicit job-type allowlist, and completion/failure acknowledgement API with audit events.
- First VPN-host agent milestone: Linux worker polls the control plane over HTTPS and claims only DHCP reservation jobs.
- DHCP reservation worker validates UUID/IP/MAC payloads, limits addresses to the VPN /22 allocation pool, atomically maintains a dedicated dnsmasq include file, validates dnsmasq configuration, restarts dnsmasq, and restores the previous file if applying the new configuration fails.
- CI now builds and publishes a separate Linux x64 VPN-host agent artifact.
- Windows Service fails closed until server-side provisioning reports ready.

## Not implemented yet

- Device re-enrollment/recovery flow that safely preserves the existing credential and address.
- SoftEther account provisioning and secure per-device credential delivery (DHCP worker is implemented, but it deliberately leaves SoftEther jobs queued).
- SoftEther adapter and isolated Virtual Hub provisioning.
- Native WireGuard adapter and TCP encapsulation adapter.
- Restricted service/tray IPC.
- Connection orchestration, automatic transport selection, and reconnect.
- Admin UI, installer/updater, signing, packaging, and end-to-end tests.

## Transport implementation rules

- SoftEther Stable Edition is used as the L2 engine; UDP acceleration is not advertised as pure UDP-only because SoftEther retains TCP control/session traffic.
- WireGuard remains the native UDP engine. TCP mode is a separate adapter carrying already-encrypted WireGuard datagrams; no custom cryptography is introduced.
- Do not bridge the production LAN interface. The first SoftEther hub remains isolated.
- The configured public port 28457 is a target, not a verified shared listener configuration. Validate every listener with socket inspection and packet capture before firewall changes.

## Release baselines

- SoftEther VPN Stable: 4.44 Build 9807 RTM.
- Go: 1.27.3.
- Windows client target: .NET 10 LTS (10.0.14 servicing baseline; CI resolves the current 10.0.x SDK).
- WireGuard: official WireGuard for Windows / WireGuardNT and native Linux WireGuard tools.

## Build status

The GitHub Actions workflow builds the control-plane server, Linux DHCP provisioning agent, and Windows client. The latest run must complete successfully before calling the new agent build-verified. No production installer or live VPN connection is claimed at this stage.
