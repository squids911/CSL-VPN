# Architecture notes

## Goals

CSL VPN is intended to provide centrally managed connectivity for Windows endpoints and remote sites while preserving existing physical LAN addressing.

## Logical components

### Control plane
- Management API: device enrollment, authentication, session leases, configuration delivery, and administrative operations.
- PostgreSQL: persistent device records, site metadata, address assignments, certificates/credential metadata, and audit events.
- IPAM: unique allocation from the VPN client pool and deterministic reassignment for a returning device identity.
- Administration UI: manage devices, sites, access policy, and operational state.

### Client
- Windows service starts independently of interactive logon and owns the VPN connection lifecycle.
- Tray application displays state and exposes transport preference: automatic, UDP-only, or TCP-only.
- Virtual Ethernet adapter provides the client-side Layer-2 interface.
- Device identity must survive ordinary application upgrades and reinstalls when the identity store is preserved.

### Data plane
- Carries Ethernet frames between authorized VPN participants.
- UDP is preferred; TCP is a fallback. Both use port 28457 on their respective protocols.
- Transport changes must not change the client's assigned VPN address.
- A server-side lease prevents two simultaneous sessions from using one device registration.

### Site connectivity
- Each physical site is an isolated routing domain.
- Overlapping physical prefixes cannot be transparently merged into one common L2 domain.
- Remote access to a site with overlapping prefixes needs a site-qualified translated prefix, per-site NAT, or an equivalent explicit selection mechanism.
- The final mechanism must be prototyped and documented before implementation is considered complete.

## Enrollment and lifecycle

1. Client creates or loads a persistent device identity.
2. Client enrolls with the management API using an authenticated bootstrap flow.
3. Server validates the unique device name and binds the registration to the device identity.
4. IPAM assigns a stable VPN address.
5. Client obtains its connection configuration and establishes a data-plane session.
6. Heartbeats refresh the registration's last-seen timestamp.
7. A registration offline for 14 days becomes eligible for expiration and address reclamation, subject to a safe cleanup policy.

## Security principles

- Never distribute the server's Let's Encrypt private key to clients.
- Prefer unique per-device credentials/certificates over a shared network password alone.
- Encrypt and authenticate management and data-plane control traffic.
- Store secrets using platform-appropriate protected storage.
- Record enrollment, authentication, session conflicts, administrative changes, and expiration events.
- Apply authorization at device and site boundaries; network reachability must not imply unrestricted access.

## Decisions still requiring validation

- Viable, maintained Windows TAP-compatible adapter and driver strategy.
- L2 encapsulation and bridging implementation, including broadcast/multicast containment.
- UDP/TCP transport behavior, congestion handling, MTU, and reconnect semantics.
- Safe site access model for overlapping address spaces.
- Certificate issuance, rotation, revocation, and recovery workflow.
- Threat model and security review before production deployment.
