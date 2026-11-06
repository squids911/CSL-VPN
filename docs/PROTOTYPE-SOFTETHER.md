# SoftEther-based CSL VPN prototype

## Objective and scope

Validate SoftEther VPN Stable Edition as the Layer-2 Ethernet data plane for CSL VPN using one Ubuntu server and one Windows client. This is a lab prototype, not a production deployment.

The first test must remain isolated from every physical LAN. Do not create a bridge to `ens33`, do not enable SecureNAT, do not enable IP forwarding, and do not change the host's routes, DHCP, firewall, or DNS as part of the initial installation.

## Fixed environment

| Item | Value |
|---|---|
| Server OS | Ubuntu Server 26.04 LTS |
| Architecture | x86_64 |
| Kernel baseline | 7.0.0-43-generic |
| Physical interface | `ens33` |
| Physical address | `10.254.100.252/24` |
| Default gateway | `10.254.100.254` |
| Public DNS name | `v.csl.by` |
| Intended external port | `28457` |
| VPN pool | `10.255.0.0/22` |
| Reserved server-side address | `10.255.0.1/22` |
| Virtual Hub | `CSL` |
| First client | Windows 10/11 x64 |

The server's public endpoint is behind Nginx Proxy Manager for web services today. A normal HTTP reverse proxy cannot proxy the SoftEther VPN protocol. VPN traffic must reach the SoftEther listener using a supported TCP passthrough or direct port-forwarding design. Do not expose the SoftEther administration listener to the Internet.

## Pinned upstream baseline

- Product: SoftEther VPN Server, Stable Edition.
- Version: `4.44-9807-rtm`.
- Upstream release: https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases/tag/v4.44-9807-rtm
- x86_64 Linux server archive:
  https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases/download/v4.44-9807-rtm/softether-vpnserver-v4.44-9807-rtm-2025.04.16-linux-x64-64bit.tar.gz
- Upstream version history: https://www.softether.org/5-download/Version_History

The upstream release metadata does not publish an asset SHA-256 digest. Do not invent one or treat TLS download alone as a cryptographic checksum. Record a locally computed SHA-256 and archive provenance in the deployment log; prefer a signed/checksummed upstream artifact if one becomes available. Re-check the Stable Edition release page before each new build rather than silently tracking Developer Edition.

## Proposed isolated Layer-2 topology

1. SoftEther VPN Server runs as a dedicated systemd service.
2. Create one Virtual Hub named `CSL`.
3. Connect the Virtual Hub only to a dedicated Linux TAP device (for example `tap_csl`) if a host-side Layer-3 endpoint is required.
4. Assign `10.255.0.1/22` to that TAP interface. The TAP interface is a host endpoint for this isolated hub, not a bridge to `ens33`.
5. Do not attach `tap_csl` to a Linux bridge containing physical interfaces.
6. Do not enable SecureNAT for the first test.
7. Do not enable IPv4 forwarding or NAT for the first test.
8. Initially configure one test Windows client with a static address from the test pool (for example `10.255.0.10/22`) and gateway/DNS unset. This avoids introducing a DHCP service before the L2 path is proven.
9. Verify only client-to-server-TAP and client-to-client traffic. Explicitly verify that the physical LAN and Internet are not reachable through the VPN.

A SoftEther Virtual Hub is an Ethernet switch; it does not itself allocate the CSL VPN IPAM address. Address assignment belongs to CSL VPN Management API/IPAM. For the first L2 test, use a manually configured test address. A later DHCP design must be bound only to the isolated TAP segment and must not leak onto the physical LAN.

## Transport model and port validation

SoftEther's native SSL-VPN transport is not equivalent to independent native "UDP-only" and "TCP-only" VPN protocols. Its normal session uses TCP and can use UDP acceleration. UDP acceleration is an optimization of the SoftEther session, not a separate UDP-only mode.

The intended CSL transport choices are therefore prototype acceptance items:

- TCP-only: establish a session with UDP acceleration disabled and verify packet capture shows the expected TCP-only data path.
- TCP with UDP acceleration: establish a normal session and verify the UDP acceleration flow in packet capture.
- Automatic: client policy chooses between the two tested configurations.
- Manual: user can select the tested TCP-only or TCP+UDP-acceleration profile.

Do not label the second profile "UDP-only". Do not claim that TCP and UDP listeners both use external port `28457` until the exact server build's behavior has been measured. Record the actual bound sockets and packet-capture destination ports. Confirm the edge router and Nginx Proxy Manager topology can forward the required TCP and UDP flows; HTTP proxy-host configuration alone is insufficient.

## Prototype phases

### P0 — Release and host validation

- Confirm OS, architecture, kernel, disk space, and existing listener usage.
- Pin the official Stable Edition version and archive.
- Inspect archive contents before installation.
- Build/install into a dedicated path such as `/opt/softether`.
- Record the archive SHA-256 locally.
- Create a dedicated service account only if supported by the selected upstream build and file permissions; otherwise document the upstream runtime privilege requirement before changing it.
- Create a systemd unit with explicit start/stop/restart behavior and restrictive file permissions.

### P1 — Isolated server

- Install SoftEther VPN Server and verify its exact version.
- Set a unique strong server administration password.
- Create the `CSL` Virtual Hub and a separate hub administrator credential.
- Keep server administration bound to loopback or a management-only LAN path; do not expose it publicly.
- Disable unused VPN protocols and listeners.
- Create a dedicated TAP device and connect only the `CSL` hub to it if a server-side IP endpoint is needed.
- Assign `10.255.0.1/22` to the TAP interface.
- Keep SecureNAT, physical Local Bridge, host forwarding, and NAT disabled.
- Initially retain the upstream default listener only for local validation; move to `28457` after confirming the port is free and edge forwarding is understood.

### P2 — Windows client

- Use the official SoftEther VPN Client for the first interoperability test.
- Create a dedicated virtual network adapter and connect to `CSL`.
- Use one unique test account; do not share the hub administrator account.
- Set a static test address such as `10.255.0.10/22`.
- Verify ARP, ICMP to `10.255.0.1`, and bidirectional Ethernet traffic.
- Capture traffic on both server physical and TAP interfaces.
- Test reconnect, sleep/resume, and server restart.
- Verify that no default route, DNS route, or physical-LAN route is unintentionally installed on Windows.

### P3 — Transport matrix

Test and record separately:

| Profile | UDP acceleration | Expected observation |
|---|---:|---|
| TCP-only | Disabled | VPN session/data carried over TCP |
| TCP + UDP acceleration | Enabled | TCP session plus UDP acceleration packets |
| Automatic | Policy-selected | Client chooses only a previously validated profile |

For each test, record the client/server version, configured listener, actual socket, packet capture, throughput, latency, packet loss, and reconnect behavior. Do not infer UDP support from a successful TCP connection.

### P4 — Identity, IPAM, and session integration

- PostgreSQL remains the source of truth for device identity, assigned VPN address, site membership, and lifecycle.
- One registered device maps to one SoftEther authentication identity and one permanent IP.
- Use a unique per-device credential; never embed the server administrator password in the client.
- Prototype SoftEther user lifecycle through `vpncmd` and the documented management API.
- Reject or fence concurrent sessions for one device at the management layer.
- Preserve device identity and assigned IP across transport changes and reconnects.
- Reinstall must preserve the protected device identity file to retain registration.
- Heartbeat and 14-day inactive-device cleanup remain CSL VPN API responsibilities.

### P5 — Site connectivity

- Never connect overlapping physical LAN prefixes into one shared L2 broadcast domain.
- Keep each site isolated and use an explicitly designed routed/NAT-translated model.
- Do not bridge any production LAN until site isolation, ACLs, routing, and rollback have been reviewed and tested.

## Security requirements

- Separate server administration, hub administration, and per-device VPN authentication.
- No shared client credential across devices.
- No public management listener.
- No public HTTP admin console unless separately secured and explicitly required.
- Firewall policy must allow only the measured VPN data-plane ports.
- Do not copy the Let's Encrypt private key to clients.
- Keep the SoftEther server configuration and secrets readable only by the service administrator.
- Log authentication, connection lifecycle, configuration changes, and management actions without logging plaintext credentials.
- Keep production routing and physical LAN untouched during P0–P3.

## Acceptance criteria

1. One Windows client connects to the isolated `CSL` hub.
2. The server TAP endpoint responds at `10.255.0.1`.
3. The client uses a test address from `10.255.0.0/22`.
4. Bidirectional Ethernet traffic works.
5. Packet captures prove the actual TCP-only and TCP+UDP-acceleration behaviors.
6. Reconnect retains the same device identity and test IP.
7. A duplicate session for one device is rejected or safely fenced.
8. No VPN path reaches the physical LAN or changes the host default route.
9. Server administration is not exposed to the public Internet.
10. All listener ports and edge forwarding requirements are documented before moving to `28457`.

## Explicit non-goals

- No production deployment or production LAN bridge.
- No custom VPN protocol or cryptography.
- No custom Windows GUI in the first interoperability test.
- No claim of native UDP-only SoftEther mode.
- No automatic DHCP/IPAM integration before the static-address L2 test passes.
- No claim that port `28457` works for both transports before packet-level verification.
