# Architecture notes

## Control plane

The control plane is an ASP.NET Core API with PostgreSQL. The current API exposes liveness and database-readiness checks only. The schema draft records devices, address reservations, enrollment-code hashes, device-credential hashes, and single-session leases; application endpoints and transaction logic are still to be implemented.

Future credentials must be generated with a cryptographically secure random generator, stored only as hashes, transmitted only over authenticated TLS, and revocable. Enrollment should be one-time and operator-authorized. Device IP allocation must use a database transaction/locking strategy to avoid duplicate leases under concurrent enrollments.

## Network placement

NPM is the planned public TLS edge. It forwards requests to the API over the private LAN at `10.254.100.252:8088`. PostgreSQL stays inside the Compose network. The API's host port must be restricted to NPM and trusted administration sources by the host firewall.

The public 443 forwarding currently serves SoftEther. Certificate issuance via HTTP-01 on port 80 and serving the API over public HTTPS on port 443 are separate concerns. Resolve the 443 routing before enabling Force SSL or changing SoftEther.

## Data plane

The stated requirement is Ethernet/L2 with UDP preferred and TCP fallback. This scaffold does not select a transport or adapter and does not carry VPN traffic. A prototype must validate MTU, framing, encryption/authentication, replay protection, key rotation, reconnects, and Windows adapter lifecycle before production claims.

Overlapping physical site subnets must not be bridged blindly. Site-aware routing/translation and isolation require a separate design and threat-model review.
