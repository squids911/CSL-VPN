# CSL VPN

CSL VPN is a centrally managed Ethernet VPN project for Windows clients and remote sites.

> Status: early scaffold. The repository now has a starter Control Plane API, PostgreSQL schema, Ubuntu deployment files, Windows service/tray shells, and CI tests. Device enrollment, IP allocation, authentication/session control, the admin UI, and the VPN data plane are not implemented yet. Do not deploy this as a production VPN.

## Initial architecture

- Central server: Ubuntu Server 26.04.
- Public management/API endpoint: `v.csl.by`.
- Intended VPN transport: `28457/UDP` and `28457/TCP` (not implemented).
- Intended client address pool: `10.255.0.0/22` (allocation policy not implemented).
- Windows clients: Windows 10/11 and Windows Server x64.
- Server API: ASP.NET Core on .NET 10; PostgreSQL.
- Deployment: Docker Compose; Nginx Proxy Manager is the planned HTTPS edge.
- Client components: Windows service, tray UI, and a future virtual Ethernet adapter integration.

## What exists now

- `src/CSL.Vpn.ControlPlane`: API health endpoints (`/health/live`, `/health/ready`).
- `db/migrations`: first PostgreSQL schema draft for devices, credentials, enrollment codes, sessions, and the address pool.
- `deploy`: Compose configuration and an environment template; PostgreSQL is not published on a host port.
- `src/CSL.Vpn.WindowsService` and `src/CSL.Vpn.WindowsTray`: non-functional UI/service shells. They do not establish a tunnel.
- `src/CSL.Vpn.Shared` and tests: persistent device-ID helper and unit tests.
- `.github/workflows/ci.yml`: build/test workflow.

## Network design constraints

The intended client VPN is Ethernet/L2. UDP is the preferred transport and TCP is a fallback; clients should be able to select automatic, UDP-only, or TCP-only mode. The L2 transport and adapter choice still need a prototype and security review.

Physical site networks may use overlapping address ranges. They must remain isolated rather than being blindly merged into one shared Layer-2 broadcast domain. Site-aware routing/translation is a separate design task and is not implemented.

## Security requirements

- Per-device identity and credentials; never distribute the server's TLS private key to clients.
- Persist a device identifier across reinstalls when the device identity is preserved.
- Store only hashes of bearer/enrollment tokens in the database; reveal issued secrets once.
- Prevent simultaneous active sessions for one device registration.
- Heartbeat active registrations and expire devices after 14 days offline, returning released addresses to the pool.
- Protect management and transport control channels with authenticated encryption.
- Keep PostgreSQL and the API off the public Internet; expose the API only to the trusted proxy/LAN.

These requirements are not all enforced by the current scaffold. See the implementation roadmap.

## Deploying the current scaffold

See Ubuntu Server 26.04 deployment notes. The API is intended to listen on `10.254.100.252:8088` behind NPM at `10.254.100.251`.

Port 443 warning: SoftEther currently uses `10.254.100.252:443`. Do not change the router's 443 forwarding or enable NPM Force SSL until the public HTTPS path and SoftEther access have been planned together. Let's Encrypt HTTP-01 validation uses inbound TCP 80; serving HTTPS from NPM also requires public TCP 443 to reach NPM.

## Repository workflow

- `main`: stable, reviewed milestones.
- `develop`: active integration branch.
- Use focused commits for independently reviewable components.

## Planned implementation areas

1. Architecture and protocol decisions.
2. Management API and database schema.
3. Device enrollment, authentication, and IP address management.
4. Windows service and client UI.
5. Ethernet transport/data plane.
6. Site isolation and site-to-site access.
7. Administration UI, packaging, updates, tests, and operations documentation.

The exact L2 transport and overlapping-site access mechanism must be validated with prototypes and security reviews before they are treated as supported features.
