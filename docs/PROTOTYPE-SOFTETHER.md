# SoftEther-based prototype plan

## Prototype objective

Validate SoftEther as the Ethernet data plane for CSL VPN before implementing the custom management platform or Windows tray application.

The prototype is deliberately isolated from production LANs. It must not create a physical LAN bridge during the first stage.

## Proposed topology

- Existing Ubuntu host: `10.254.100.252/24`, gateway `10.254.100.254`.
- Public endpoint: `v.csl.by`.
- SoftEther VPN Server: isolated test Virtual Hub.
- Initial test client: one Windows 10/11 x64 machine.
- VPN test address range: reserve a small test subset from `10.255.0.0/22`; do not advertise or bridge it into the physical LAN.
- Management API and PostgreSQL are not required for the first connectivity test.

## Important transport clarification

SoftEther's native protocol carries Ethernet frames and uses a TCP/IP and UDP/IP hybrid transport with UDP acceleration. It is not equivalent to two independent native listener modes called "UDP-only" and "TCP-only". The default listeners documented by SoftEther are TCP listeners; UDP acceleration behavior must be tested and its configuration/port requirements verified for the selected version.

Therefore, the CSL VPN requirement for explicit automatic / UDP-only / TCP-only modes is an open compatibility item. Do not promise these three modes until a prototype demonstrates them. The configured external port `28457` must be validated for both the control/listener path and any UDP acceleration traffic; do not assume one TCP and one UDP socket on that same port without verification.

## Prototype phases

### P0 — Host and release validation

- Confirm OS release, architecture, kernel, and available resources.
- Select the official Stable Edition source/release, not an experimental Developer Edition build.
- Record exact version, source URL, checksum/signature where published, and build/install method.
- Review supported configuration and management interfaces.

### P1 — Isolated server

- Install SoftEther VPN Server.
- Bind only the required listener(s), initially using a temporary test port if needed.
- Set a strong server administration password.
- Create a dedicated test Virtual Hub.
- Disable unused VPN protocols and unused listener ports.
- Do not enable SecureNAT or Local Bridge unless a test specifically requires it.
- Keep the server management interface inaccessible from the public Internet.

### P2 — Windows client

- Install the official SoftEther VPN Client on one test Windows machine.
- Create a virtual network adapter and connect to the test Virtual Hub.
- Test certificate-based authentication and a unique test user.
- Verify Ethernet frame transport and reconnect behavior.
- Capture TCP and UDP flows to establish actual transport behavior.

### P3 — Address assignment and session controls

- Test DHCP/static addressing in the isolated hub.
- Determine whether SoftEther's built-in per-user controls are sufficient or whether the management API must coordinate sessions.
- Test duplicate simultaneous login behavior.
- Test whether client identity and address can be preserved across reconnects and reinstall scenarios.

### P4 — Management integration spike

- Use `vpncmd` and documented management interfaces to create, inspect, disable, and remove test users.
- Prototype an API adapter that manages SoftEther objects without exposing the SoftEther administrator password to clients.
- Decide whether device certificates can be mapped cleanly to SoftEther authentication identities.
- Keep PostgreSQL as the source of truth for device identity, assigned address, site membership, and lifecycle state.

### P5 — Site connectivity design

- Do not bridge overlapping physical LAN prefixes into one shared broadcast domain.
- Test each site as an isolated segment.
- Select and validate a site-qualified NAT/translation or routed access mechanism before connecting any production LAN.

## Acceptance criteria

1. One Windows client connects reliably to the isolated hub.
2. Ethernet traffic crosses the tunnel in both directions.
3. Actual TCP and UDP transport flows are documented from packet captures.
4. Reconnect does not unexpectedly change the client's assigned VPN address.
5. A duplicate session for one device can be rejected or safely fenced.
6. Server administration is not exposed to the public Internet.
7. The test does not alter routing, DHCP, or bridging on the production LAN.
8. Known limitations are recorded before proceeding to a custom client.

## Explicit non-goals for this prototype

- No production deployment.
- No connection to production LANs.
- No custom Windows GUI.
- No claim of independent UDP-only and TCP-only operation until proven.
- No custom VPN data-plane implementation.
