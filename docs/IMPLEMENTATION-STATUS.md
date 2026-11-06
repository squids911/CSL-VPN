# Implementation status

Updated: 2026-11-06

## Committed to develop

### Server
- Go 1.27.3 module and Linux container build.
- HTTP server lifecycle, structured request logging, security response headers.
- PostgreSQL connection/readiness handling.
- Initial schema for sites, devices, unique device names/fingerprints, VPN addresses, exclusive live sessions, and audit events.
- Liveness, readiness, and API version endpoints.
- Device enrollment, persistent IP allocation, credential hashing, session leases, heartbeats, exclusive active-session enforcement, and 14-day inactive-device cleanup.
- Enrollment/session API validation tests and additional database indexes/constraints.

### Windows client
- .NET 10 shared protocol/transport model.
- Windows Service host shell.
- System-tray application shell.
- HTTPS management API client for enrollment, device profile, session start, and heartbeat.
- DPAPI-protected persistent device identity and client-generated credential.
- Windows service performs idempotent first enrollment and retains the assigned address.
- Build workflow for Linux server and Windows client.

### Control-plane API now committed
- Bootstrap-token-protected device enrollment with SHA-256 fingerprint validation.
- Transactional VPN IP allocation from `10.255.0.2` through `10.255.3.254`.
- Client-generated persistent per-device credential; server stores only SHA-256 hash.
- Idempotent enrollment when the same device fingerprint and credential are presented again.
- Device-authenticated session lease creation and heartbeat.
- Exclusive unexpired session enforcement and stale lease cleanup.
- Hourly 14-day inactive-device expiry with audit event and IP reclamation.
- HTTP validation tests for enrollment and credential-gated session routes.

## Not implemented yet

- Device re-enrollment/recovery flow that safely preserves the existing credential and address.
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
- Windows client target: .NET 10.
- WireGuard: official WireGuard for Windows / WireGuardNT and native Linux WireGuard tools.

## Build status

The GitHub Actions workflow is committed. A successful workflow run is required before calling these scaffolds build-verified. No production installer or server binary is claimed at this stage.
