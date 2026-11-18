# Ubuntu Server 26.04 deployment notes (draft)

This runbook deploys only the current management-plane scaffold. It does not create a working VPN tunnel.

## Network assumptions

- Ubuntu/API host: `10.254.100.252`.
- Nginx Proxy Manager: `10.254.100.251`.
- NPM upstream: HTTP `10.254.100.252:8088`.
- PostgreSQL is private to the Docker network and has no host-published port.
- Do not forward TCP 8088 or 5432 from the router to the Internet.
- SoftEther currently uses `10.254.100.252:443`. Leave the public 443 forwarding and SoftEther unchanged while requesting an HTTP-01 certificate. To serve HTTPS from NPM later, public TCP 443 must reach NPM; resolve that port conflict as a separate change.

## Prerequisites

Install Docker Engine and the Docker Compose v2 plugin using the supported instructions for Ubuntu Server 26.04. Verify:

```bash
docker --version
docker compose version
```

Before changing UFW, confirm the current SSH allow rule so you do not lock yourself out.

## Configure and start

From the repository root:

```bash
umask 077
cp deploy/.env.example deploy/.env
openssl rand -hex 32
```

Edit `deploy/.env`: replace `POSTGRES_PASSWORD` and `ADMIN_API_KEY` with generated random hex values. Do not commit or send this file. The hex format avoids connection-string delimiter characters. `ADMIN_API_KEY` guards the `/admin` surface (enrollment codes, device revocation).

Validate the Compose configuration and start the services:

```bash
docker compose --env-file deploy/.env -f deploy/compose.yaml config
docker compose --env-file deploy/.env -f deploy/compose.yaml up -d --build
docker compose --env-file deploy/.env -f deploy/compose.yaml ps
```

The configured host binding is `10.254.100.252:8088`. If that address is not assigned to this Ubuntu host, stop and correct `API_BIND_ADDRESS` rather than binding the API publicly by accident.

## Restrict the API port

Allow NPM to reach the API on the LAN. Review the existing firewall policy first; do not enable UFW blindly if SSH is not already allowed.

```bash
sudo ufw allow from 10.254.100.251 to any port 8088 proto tcp
sudo ufw status numbered
```

Keep the database port unpublished. Keep router/WAN rules for 8088 and 5432 closed.

## Verify locally

Run from the Ubuntu host or another permitted LAN machine:

```bash
curl -i http://10.254.100.252:8088/health/live
curl -i http://10.254.100.252:8088/health/ready
```

The first endpoint checks that the API process is alive; the second also checks PostgreSQL. Other management features are not implemented yet.

## NPM and certificate notes

For the future API Proxy Host, use scheme `http`, forward host `10.254.100.252`, and port `8088`. Let's Encrypt HTTP-01 validation requires inbound public TCP 80 to reach NPM at `10.254.100.251`. A 404 for an arbitrary, nonexistent ACME challenge path is expected. A certificate alone does not solve the current public TCP 443 conflict with SoftEther. Do not enable Force SSL until the external 443 routing is deliberately changed and SoftEther access is preserved.

## Database initialization and backups

`0001_initial.sql` is mounted as a Postgres first-start initialization script. It runs only when the named volume is empty. Do not edit an already-applied migration and expect an existing volume to rerun it; add a new reviewed migration and a migration runner before evolving deployed schemas.

Example logical backup:

```bash
docker compose --env-file deploy/.env -f deploy/compose.yaml exec -T db \
  sh -c 'pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB"' > cslvpn-backup.sql
```

Store backups off-host and test restoration before relying on them.

## Backups (applied 2026-11-18)

`deploy/backup.sh` (host: `/opt/csl-vpn/deploy/`) writes a daily
custom-format pg_dump to `/var/backups/csl-vpn/` (0700), keeps 14 days,
maintains `latest.dump`, and refuses to rotate in a dump smaller than 1 KiB.
systemd: `csl-vpn-backup.service` + `csl-vpn-backup.timer` (03:30 UTC daily).

Restore drill (verified on first install):

```sh
docker exec deploy-db-1 psql -U cslvpn -d postgres -c "DROP DATABASE IF EXISTS cslvpn_restore_test"
docker exec deploy-db-1 psql -U cslvpn -d postgres -c "CREATE DATABASE cslvpn_restore_test"
docker exec -i deploy-db-1 pg_restore -U cslvpn -d cslvpn_restore_test < /var/backups/csl-vpn/latest.dump
# sanity: row counts match the source
docker exec deploy-db-1 psql -U cslvpn -d cslvpn_restore_test -tAc \
  "SELECT (SELECT count(*) FROM devices), (SELECT count(*) FROM address_pool), (SELECT count(*) FROM schema_migrations)"
docker exec deploy-db-1 psql -U cslvpn -d postgres -c "DROP DATABASE cslvpn_restore_test"
```

Copy `latest.dump` off-host (rsync/scp to a backup target) — the dump alone
is sufficient: SoftEther state is static config (backed up as
`/usr/vpnserver/vpn_server.config.bak-*`), everything else rebuilds from the
repository.

## Anti-spoofing (applied 2026-11-18)

A VPN client may source traffic only from the address the control plane has
currently reserved for it. `deploy/sync-antispoof.sh` (on the host,
`/opt/csl-vpn/deploy/`) maintains the ipset `cslvpn-active` from the database
and asserts drop rules at the head of FORWARD/INPUT for `tap_tap_csl`; IPv6
on the tap is dropped outright. systemd: `csl-vpn-antispoof.service`
(oneshot, after ufw/docker/vpnserver) and `csl-vpn-antispoof-sync.timer`
(every 30 s). The script takes an flock — concurrent timer/service runs are
safe. Verified: enrolling a device puts its address into the set, revoking
removes it.

## SoftEther data-plane wiring (applied 2026-11-18)

The hub "CSL" authenticates VPN logins against the control plane via RADIUS
(the API embeds a PAP facade, published on `127.0.0.1:1812/udp` only).
`vpncmd` is not installed on the host, so the hub was configured by editing
`/usr/vpnserver/vpn_server.config` while the service was stopped:

1. `systemctl stop vpnserver` and back up `vpn_server.config`.
2. In `declare CSL` → hub options, add:
   `string RadiusServerName 127.0.0.1` and
   `byte RadiusSecret <base64 of RADIUS_SHARED_SECRET>`.
   (`RadiusServerPort 1812` is already the default in the config.)
3. In the same hub's `declare UserList`, add the wildcard user (any unknown
   username falls through to RADIUS per SoftEther docs):

   ```
   declare *
   {
       uint AuthType 4
       uint64 CreatedTime <ms epoch>
       uint64 ExpireTime 0
       uint64 LastLoginTime 0
       string Note Control-plane devices (RADIUS)
       uint NumLogin 0
       string RealName $
       uint64 UpdatedTime <ms epoch>
       wstring AuthRadiusUsername $
   }
   ```

4. `systemctl start vpnserver`, then verify with a real device credential:
   `radtest <device-guid> <credential> 127.0.0.1 0 $RADIUS_SHARED_SECRET`
   must return Access-Accept, and a revoked device must get Access-Reject.

SecureNAT stays disabled: the control plane owns 10.255.0.0/22 and clients
assign their addresses statically. The host masquerades pool traffic towards
non-LAN destinations (persisted by `csl-vpn-nat.service`):

```
iptables -t nat -A POSTROUTING -s 10.255.0.0/22 ! -d 10.254.100.0/24 -j MASQUERADE
```

LAN hosts need a return route for `10.255.0.0/22 via 10.254.100.252` on the
site router if VPN clients must be reachable from the LAN by their real
addresses; otherwise LAN-bound traffic can be masqueraded too.

## Hardening (applied 2026-11-17)

- UFW enabled on the API host: default deny incoming, allow outgoing/routed
  (routed stays open because SoftEther SecureNAT handles VPN client traffic in
  user mode), explicit allows: 22/tcp (SSH), 443/tcp and 5555/tcp (SoftEther).
- The Docker-published API port bypasses UFW, so a DOCKER-USER rule restricts
  10.254.100.252:8088 to NPM (10.254.100.251) only:
  `iptables -I DOCKER-USER -p tcp -d 10.254.100.252 --dport 8088 ! -s 10.254.100.251 -j DROP`.
  Persisted via the `csl-api-guard.service` systemd unit (enabled).
- Recommended NPM hardening (manual, admin UI): in the v.csl.by Proxy Host →
  Advanced tab, restrict the admin surface to the LAN:

  ```nginx
  location /admin/ {
      allow 10.254.100.0/24;
      deny all;
      proxy_pass http://10.254.100.252:8088;
      proxy_set_header Host $host;
      proxy_set_header X-Real-IP $remote_addr;
      proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
      proxy_set_header X-Forwarded-Proto $scheme;
  }
  ```
