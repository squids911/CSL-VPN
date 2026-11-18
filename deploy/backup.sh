#!/bin/sh
# backup.sh — daily PostgreSQL backup of the control-plane database.
# Keeps 14 days of custom-format dumps in /var/backups/csl-vpn.
# systemd: csl-vpn-backup.service + csl-vpn-backup.timer (daily).
set -eu

DEST=/var/backups/csl-vpn
mkdir -p "$DEST"
chmod 700 "$DEST"

TS=$(date -u +%Y%m%dT%H%M%SZ)
TMP="$DEST/.cslvpn-$TS.dump.tmp"

docker exec deploy-db-1 pg_dump -U cslvpn -d cslvpn -Fc > "$TMP"

# Refuse to rotate in an empty/truncated dump.
SIZE=$(wc -c < "$TMP")
if [ "$SIZE" -lt 1024 ]; then
    echo "backup suspiciously small ($SIZE bytes), aborting" >&2
    rm -f "$TMP"
    exit 1
fi

mv "$TMP" "$DEST/cslvpn-$TS.dump"
ln -sf "cslvpn-$TS.dump" "$DEST/latest.dump"
find "$DEST" -name 'cslvpn-*.dump' -mtime +14 -delete
echo "backup ok: $DEST/cslvpn-$TS.dump ($SIZE bytes)"
