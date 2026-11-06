# SoftEther deployment workspace

This directory contains deployment assets for the isolated CSL VPN SoftEther prototype. Do not run deployment files against a production host without reviewing the matching phase in `docs/PROTOTYPE-SOFTETHER.md`.

## Pinned upstream artifact

- Edition: Stable Edition
- Version: `4.44-9807-rtm`
- Release: https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases/tag/v4.44-9807-rtm
- Linux x86_64 VPN Server archive:
  https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases/download/v4.44-9807-rtm/softether-vpnserver-v4.44-9807-rtm-2025.04.16-linux-x64-64bit.tar.gz

The release metadata currently has no published SHA-256 digest for this asset. Any future installer must fail closed unless the downloaded artifact matches a checksum pinned in the repository after independent verification. Never calculate a checksum from the downloaded file and then treat that same value as proof of authenticity.

## Deployment rules

1. Do not overwrite an existing SoftEther installation or configuration automatically.
2. Before installation, check whether any of the candidate TCP/UDP ports are already occupied.
3. Back up any existing SoftEther configuration before changing it.
4. Install into a dedicated path and use a dedicated systemd unit.
5. Do not put passwords, tokens, or private keys in this repository, command-line arguments, or shell history.
6. Do not expose the SoftEther server administration port or Hub administration credentials to clients.
7. Do not enable SecureNAT, bridge the Virtual Hub to `ens33`, enable IP forwarding, or add NAT rules during the first prototype.
8. The only allowed initial host-side Layer-3 endpoint is a dedicated TAP interface attached exclusively to the `CSL` Virtual Hub.
9. Keep the external listener on a temporary test port until the actual TCP and UDP flows have been measured. Port `28457` is a target, not a verified fact.
10. All changes must have a documented rollback path.

## Build prerequisites

The upstream UNIX build guide lists CMake, GCC/G++, make, pkg-config, ncurses, OpenSSL, libsodium, readline, and zlib development packages. On Ubuntu, the documented package set is:

```bash
sudo apt install cmake gcc g++ make pkgconf libncurses5-dev libssl-dev libsodium-dev libreadline-dev zlib1g-dev
```

Upstream build guide: https://github.com/SoftEtherVPN/SoftEtherVPN/blob/master/src/BUILD_UNIX.md

The release archive and the source repository are not interchangeable without inspection. Before writing an automated installer, inspect the pinned archive's top-level contents and confirm the exact build procedure for that release. Do not assume an archive contains prebuilt executables.

## Planned assets

- `install.sh`: not yet created; must be idempotent, checksum-gated, and refuse to replace an existing installation.
- `vpnserver.service`: not yet created; must use explicit paths and least-privilege settings supported by the selected build.
- `configure-hub.vpncmd`: not yet created; commands must be checked against `vpncmd` help from the pinned release before use.
- `verify.sh`: not yet created; will verify service state, bound sockets, TAP isolation, and absence of physical-LAN bridge/forwarding changes.

## Required preflight evidence

Before applying configuration on the Ubuntu host, capture:

- `/etc/os-release`
- `uname -m` and `uname -r`
- free disk space
- current TCP and UDP listeners
- current IPv4 forwarding state
- current routes and interface addresses
- existing SoftEther process/service/configuration state

These are read-only checks. The installation phase must not modify the physical interface, default route, DHCP configuration, or production firewall policy.
