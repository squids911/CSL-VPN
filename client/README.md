# CSL VPN Windows Client

Windows client is split into:

- **CSLVpn.Service** — privileged Windows service; owns device identity, management enrollment, heartbeat, connection lifecycle, and engine processes.
- **CSLVpn.Tray** — interactive system-tray shell; it will communicate with the service over restricted local IPC and must not own VPN credentials.
- **CSLVpn.Client.Core** — management API client, protocol/transport models, and device credential helpers.

Target: Windows 10/11 and Windows Server x64, .NET 10 LTS (CI resolves the latest servicing SDK).

## Implemented

- HTTPS-only management API client.
- Client-generated 256-bit device credential and stable random device identity fingerprint.
- Device identity and credential persisted using Windows DPAPI (CurrentUser scope under the Windows service account).
- Service enrollment against the management API; identity is saved before the first network request so retry/reinstall keeps the same device identity.
- Idempotent server enrollment returns the same VPN address for the same fingerprint and credential.
- Self-contained Windows x64 publish jobs for service and tray.

## Not implemented yet

The client does not establish a VPN tunnel yet. SoftEther and WireGuard engine adapters, service/tray IPC, connection orchestration, session heartbeat once a data plane is active, and signed installer/updater are still in progress.

## First enrollment configuration

Create a one-time enrollment code through the admin API for the device name, then set machine-level environment variables before starting the service:

- `CSLVPN_SERVER_URL=https://v.csl.by/`
- `CSLVPN_BOOTSTRAP_TOKEN=<operator-provisioned secret>`

The bootstrap token is a single-use, expiring code bound to the device name and is required only for first enrollment. Do not bake it into the MSI or distribute it in a shared client package. The service runs under its Windows service account and protects the persistent identity with DPAPI.

## Build on Windows

```powershell
dotnet build .\src\CSLVpn.Service\CSLVpn.Service.csproj -c Release
dotnet build .\src\CSLVpn.Tray\CSLVpn.Tray.csproj -c Release
dotnet publish .\src\CSLVpn.Service\CSLVpn.Service.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish .\src\CSLVpn.Tray\CSLVpn.Tray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
