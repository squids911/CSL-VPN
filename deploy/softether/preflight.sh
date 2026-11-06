#!/usr/bin/env bash
set -Eeuo pipefail

# Read-only preflight for the isolated CSL VPN SoftEther prototype.
# This script intentionally does not install packages or modify networking.

section() {
  printf '\n===== %s =====\n' "$1"
}

section "Operating system"
if [[ -r /etc/os-release ]]; then
  cat /etc/os-release
else
  echo "/etc/os-release is unavailable"
fi

section "Kernel and architecture"
uname -a
printf 'architecture: '
uname -m

section "Disk space"
df -h / /opt 2>/dev/null || df -h /

section "Network interfaces"
ip -brief address 2>&1 || true

section "IPv4 routes"
ip -4 route show 2>&1 || true

section "IPv4 forwarding state"
if command -v sysctl >/dev/null 2>&1; then
  sysctl net.ipv4.ip_forward 2>&1 || true
else
  echo "sysctl is unavailable"
fi

section "Listening TCP sockets"
ss -H -lntp 2>&1 || true

section "Listening UDP sockets"
ss -H -lnup 2>&1 || true

section "Existing SoftEther processes"
if command -v pgrep >/dev/null 2>&1; then
  pgrep -a -f 'vpnserver|vpncmd|softether' || true
else
  echo "pgrep is unavailable"
fi

section "Known SoftEther service units"
for unit in softether-vpnserver.service vpnserver.service; do
  if systemctl list-unit-files "$unit" --no-legend 2>/dev/null | grep -q "$unit"; then
    systemctl status "$unit" --no-pager --full 2>&1 || true
  else
    printf '%s: not installed\n' "$unit"
  fi
done

section "Potential existing installation paths"
for path in /opt/softether /opt/vpnserver /usr/local/vpnserver /usr/local/bin/vpnserver; do
  if [[ -e "$path" ]]; then
    ls -ld "$path"
  else
    printf '%s: absent\n' "$path"
  fi
done

section "Preflight complete"
echo "No system configuration was changed."
