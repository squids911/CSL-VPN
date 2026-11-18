# Architecture notes

## Control plane

The control plane is an ASP.NET Core API with PostgreSQL. The current API exposes liveness and database-readiness checks only. The schema draft records devices, address reservations, enrollment-code hashes, device-credential hashes, and single-session leases; application endpoints and transaction logic are still to be implemented.

Future credentials must be generated with a cryptographically secure random generator, stored only as hashes, transmitted only over authenticated TLS, and revocable. Enrollment should be one-time and operator-authorized. Device IP allocation must use a database transaction/locking strategy to avoid duplicate leases under concurrent enrollments.

## Network placement

NPM is the planned public TLS edge. It forwards requests to the API over the private LAN at `10.254.100.252:8088`. PostgreSQL stays inside the Compose network. The API's host port must be restricted to NPM and trusted administration sources by the host firewall.

The public 443 forwarding currently serves SoftEther. Certificate issuance via HTTP-01 on port 80 and serving the API over public HTTPS on port 443 are separate concerns. Resolve the 443 routing before enabling Force SSL or changing SoftEther.

## Data plane

The data plane is SoftEther VPN end to end: the existing server (hub "CSL",
TCP 5555) and the SoftEther VPN Client bundled with the Windows agent. The
SoftEther protocol gives Ethernet/L2 with built-in NAT traversal; the
management agent drives the bundled client (vpncmd) and owns the IP
configuration.

Address ownership: SecureNAT is disabled on the hub, so nothing hands out
DHCP leases. The control plane owns the address pool (10.255.0.0/22, gateway
10.255.0.1 excluded) and the client statically assigns its allocated address
to the virtual adapter. The hub is locally bridged to the host tap
(`tap_tap_csl`, 10.255.0.1/22); the host routes and masquerades pool traffic
towards non-LAN destinations.

Authentication: the hub has a wildcard user "*" with RADIUS auth. The API
embeds a minimal RADIUS PAP facade (RFC 2865, UDP 1812 published on
127.0.0.1 only) that accepts a login only when User-Name is a device GUID and
User-Password matches a non-revoked credential of that non-revoked device.
SoftEther hashes the password with MD5 for transport, but only over loopback;
the credential itself is a high-entropy token. Revocation takes effect on the
next (re)connect; existing sessions end when the device's control-plane
credential is forgotten (the agent disconnects on 401).

Known gaps, to close before production traffic claims:

- No source-address enforcement on the tap: a malicious client can use an
  address other than its assigned one. ebtables/iptables anti-spoofing rules
  generated from the address pool are the planned mitigation.
- MTU/framing, reconnect storms, and adapter lifecycle are validated only in
  unit tests; the client tunnel manager still needs a real-hardware soak.
- Overlapping physical site subnets must not be bridged blindly. Site-aware
  routing/translation and isolation require a separate design and
  threat-model review.
