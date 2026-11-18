<#
.SYNOPSIS
  Builds the Windows client bundle: self-contained single-file agent + tray,
  install/uninstall scripts, zipped as CSL-VPN-client-<version>.zip.
  Run on Windows (or any host with the .NET 10 SDK). Signing is a separate,
  deliberate step (see docs/roadmap.md).
#>
param([string]$Version = '0.0.0-dev')

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$out  = Join-Path $PSScriptRoot 'out'
$bundle = Join-Path $out 'CSL-VPN-client'

Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $bundle 'agent') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $bundle 'tray')  | Out-Null

dotnet publish (Join-Path $repo 'src/CSL.Vpn.WindowsService') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $bundle 'agent')
if ($LASTEXITCODE -ne 0) { throw "agent publish failed" }

dotnet publish (Join-Path $repo 'src/CSL.Vpn.WindowsTray') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $bundle 'tray')
if ($LASTEXITCODE -ne 0) { throw "tray publish failed" }

Copy-Item (Join-Path $PSScriptRoot 'Install-CslVpn.ps1')   $bundle
Copy-Item (Join-Path $PSScriptRoot 'Uninstall-CslVpn.ps1') $bundle

@'
CSL VPN client bundle
=====================

1. (Data plane) Install SoftEther VPN Client x64 to its default path:
   https://www.softether-download.com/  ("SoftEther VPN Client")
2. Run PowerShell as Administrator in this folder:
     .\Install-CslVpn.ps1 -EnrollmentCode <code-from-operator>
   The enrollment code comes from the operator console: https://v.csl.by/admin/ui
3. Tray icon shows status; details live in C:\ProgramData\CSL VPN\state.json

Uninstall:  .\Uninstall-CslVpn.ps1            (keeps device identity)
            .\Uninstall-CslVpn.ps1 -Purge     (full wipe)
'@ | Set-Content (Join-Path $bundle 'README.txt') -Encoding utf8

$zip = Join-Path $out "CSL-VPN-client-$Version.zip"
Compress-Archive -Path $bundle -DestinationPath $zip -Force
Write-Host "Bundle written to $zip"
