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

Edit `deploy/.env`: replace `POSTGRES_PASSWORD` with the generated random hex value. Do not commit or send this file. The hex format avoids connection-string delimiter characters.

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
