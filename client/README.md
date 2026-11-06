# CSL VPN Windows Client

Windows client is split into:

- **CSLVpn.Service** — privileged Windows service; owns device identity, management heartbeat, connection lifecycle, and engine processes.
- **CSLVpn.Tray** — interactive system-tray shell; it must communicate with the service over a restricted local IPC channel and must not own VPN credentials.
- **CSLVpn.Client.Core** — shared protocol and transport configuration types.

Target: Windows 10/11 and Windows Server x64, .NET 10.

## Current milestone

The service and tray shells are present. No VPN connection is claimed yet: SoftEther and WireGuard adapters, authenticated enrollment, protected device identity, IPC, and transport selection are subsequent implementation milestones.

Build on Windows with the .NET 10 SDK:

```powershell
dotnet build .\src\CSLVpn.Service\CSLVpn.Service.csproj -c Release
dotnet build .\src\CSLVpn.Tray\CSLVpn.Tray.csproj -c Release
```
