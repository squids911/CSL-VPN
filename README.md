# CSL VPN

CSL VPN is a centrally managed Ethernet VPN project for Windows clients and remote sites.

> Status: architecture and implementation planning. No production-ready VPN data plane is claimed yet.

## Initial architecture

- Central server: Ubuntu Server 26.04.
- Public endpoint: `v.csl.by`.
- VPN transport port: `28457/UDP` and `28457/TCP`.
- VPN client address pool: `10.255.0.0/22`.
- Windows clients: Windows 10/11 and Windows Server x64.
- Client components: Windows service, tray UI, and virtual Ethernet adapter.
- Server components: management API, PostgreSQL, IP address management, authentication, transport/data plane, and administration UI.

## Network design constraints

The intended client VPN is Ethernet/L2. UDP is the preferred transport and TCP is a fallback; clients should also be able to select automatic, UDP-only, or TCP-only mode.

Physical site networks may use overlapping address ranges. They must therefore remain isolated from one another rather than being blindly merged into one shared Layer-2 broadcast domain. Access to remote site networks will require a site-aware routing and translation design that preserves the existing addressing at each site.

## Security requirements

- Per-device identity and credentials; do not distribute the server's TLS private key to clients.
- Device registration is bound to a persistent device identifier.
- A device keeps its assigned VPN address across reinstalls when its identity is preserved.
- Prevent simultaneous active sessions for one device registration.
- Heartbeat and expire registrations after 14 days offline, returning released addresses to the pool.
- Protect management and transport control channels with authenticated encryption.

## Repository workflow

- `main`: stable, reviewed milestones.
- `develop`: active integration branch.
- Use focused commits for independently reviewable components.

## Planned implementation areas

1. Architecture and protocol decisions.
2. Management API and database schema.
3. Device enrollment, authentication, and IPAM.
4. Windows service and client UI.
5. Ethernet transport/data plane.
6. Site isolation and site-to-site access.
7. Administration UI, packaging, updates, tests, and operations documentation.

## Important engineering note

The exact L2 transport implementation and the overlapping-site access mechanism must be validated with prototypes and security reviews before they are treated as supported features.
