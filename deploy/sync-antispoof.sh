#!/bin/sh
# sync-antispoof.sh — keep the cslvpn-active ipset in sync with the control
# plane's address pool and (re)assert the anti-spoofing iptables rules.
#
# A VPN client may source traffic only from the address the control plane
# currently has reserved for its device. Anything else arriving from the tap
# (unassigned pool addresses, the gateway 10.255.0.1, LAN or public spoofs)
# is dropped. IPv6 on the tap is dropped entirely (the VPN is IPv4-only).
#
# Installed by hand on the CSL VPN host; systemd units:
#   csl-vpn-antispoof.service  (oneshot: create set + rules, idempotent)
#   csl-vpn-antispoof-sync.timer  (every 30s: refresh set from PostgreSQL)
set -eu

# The oneshot service and the sync timer may fire together; serialize.
exec 9>/run/csl-vpn-antispoof.lock
flock -n 9 || exit 0

SET=cslvpn-active
TAP=tap_tap_csl

ipset create "$SET" hash:ip -exist

# Anti-spoof rules (idempotent). Position 1 keeps them ahead of Docker/UFW.
for chain in FORWARD INPUT; do
    iptables  -C "$chain" -i "$TAP" -m set ! --match-set "$SET" src -j DROP 2>/dev/null \
        || iptables  -I "$chain" 1 -i "$TAP" -m set ! --match-set "$SET" src -j DROP
    ip6tables -C "$chain" -i "$TAP" -j DROP 2>/dev/null \
        || ip6tables -I "$chain" 1 -i "$TAP" -j DROP
done

# Refresh the set atomically from the database (reserved addresses only).
TMP=$(mktemp)
trap 'rm -f "$TMP"' EXIT
echo "create ${SET}-new hash:ip -exist" > "$TMP"
docker exec deploy-db-1 psql -U cslvpn -d cslvpn -tAc \
    "SELECT 'add ${SET}-new ' || host(address) FROM address_pool WHERE reserved_by IS NOT NULL" >> "$TMP"
ipset restore < "$TMP"
ipset swap "${SET}-new" "$SET"
ipset destroy "${SET}-new"
