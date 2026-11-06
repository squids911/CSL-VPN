# Upstream release baselines

Verified for the current development cycle: 2026-11-06.

| Component | Baseline | Selection |
|---|---|---|
| SoftEther VPN Server/Client | 4.44 Build 9807 RTM | Latest RTM in the official Stable Edition release list checked for this cycle |
| Go toolchain | 1.27.3 | Current stable patch release; Linux x64 build checksum recorded below |
| .NET | 10.0.14 | Current .NET 10 LTS servicing baseline |
| WireGuard for Windows | Official upstream installer and WireGuardNT | Use upstream-signed binaries; do not invent or pin a mirror-only release number |

## Verified source references

- SoftEther Stable Edition releases: https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases
- Go release history: https://go.dev/doc/devel/release
- Go downloads and checksums: https://go.dev/dl/
- Microsoft .NET support policy: https://dotnet.microsoft.com/en-us/platform/support/policy
- Official WireGuard for Windows installation: https://www.wireguard.com/install/
- WireGuard for Windows source mirror points to the official upstream repository at https://git.zx2c4.com/wireguard-windows
- WireGuardNT source mirror points to the official upstream repository at https://git.zx2c4.com/wireguard-nt

## Go Linux x64 archive

- File: `go1.27.3.linux-amd64.tar.gz`
- SHA-256: `65efbc80fdc1fa2cd2631cad5b481777a68e8725f34e89f50fc6ac714e379b40`

## Packaging rules

1. Download upstream release artifacts only from the vendor's official distribution endpoint or official Stable Edition release page.
2. Verify published checksums and signatures before installation or packaging.
3. Do not bundle SoftEther Developer Edition builds.
4. Do not redistribute modified WireGuard cryptography or unsigned kernel drivers.
5. Record the exact upstream artifact version, checksum, and license in every CSL VPN release manifest.
6. Refresh this document whenever a dependency baseline changes; do not silently float production releases.
