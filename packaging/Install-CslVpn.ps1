#requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs the CSL VPN agent (management plane + optional SoftEther data plane).

.DESCRIPTION
  - Copies agent/tray binaries to C:\Program Files\CSL VPN\
  - Creates C:\ProgramData\CSL VPN (ACL: SYSTEM + Administrators)
  - Registers and starts the "CSL VPN Agent" Windows service (auto-restart)
  - Registers the tray app for all users (HKLM Run key)
  - Optionally drops a one-time enrollment code

  Prerequisite for the data plane: SoftEther VPN Client installed to its
  default location (C:\Program Files\SoftEther VPN Client). The agent only
  drives an installed vpnclient; it does not bundle one.

.PARAMETER EnrollmentCode
  One-time enrollment code from the operator console (https://v.csl.by/admin/ui).

.PARAMETER SourceDir
  Folder containing agent\ and tray\ (defaults to the script's folder).
#>
param(
    [string]$EnrollmentCode,
    [string]$SourceDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

$installRoot = 'C:\Program Files\CSL VPN'
$dataDir     = 'C:\ProgramData\CSL VPN'
$serviceName = 'CSL VPN Agent'
$softEtherVpncmd = 'C:\Program Files\SoftEther VPN Client\vpncmd.exe'

foreach ($relative in 'agent\CSL.Vpn.WindowsService.exe', 'tray\CSL.Vpn.WindowsTray.exe') {
    $path = Join-Path $SourceDir $relative
    if (-not (Test-Path $path)) { throw "Bundle is incomplete: $path not found." }
}

if (-not (Test-Path $softEtherVpncmd)) {
    Write-Warning "SoftEther VPN Client not found at '$softEtherVpncmd'."
    Write-Warning "The management agent will be installed, but the tunnel stays down until the client is installed."
}

# 1. Binaries
New-Item -ItemType Directory -Force (Join-Path $installRoot 'agent') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $installRoot 'tray')  | Out-Null
Copy-Item -Force (Join-Path $SourceDir 'agent\*') (Join-Path $installRoot 'agent')
Copy-Item -Force (Join-Path $SourceDir 'tray\*')  (Join-Path $installRoot 'tray')

# 2. Data directory, ACL'd to SYSTEM + Administrators
New-Item -ItemType Directory -Force $dataDir | Out-Null
$acl = Get-Acl $dataDir
$acl.SetAccessRuleProtection($true, $false)
foreach ($rule in @($acl.Access)) { $acl.RemoveAccessRule($rule) | Out-Null }
foreach ($identity in 'NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators') {
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
Set-Acl $dataDir $acl

# 3. Enrollment code (optional)
if ($EnrollmentCode) {
    Set-Content -Path (Join-Path $dataDir 'enrollment-code.txt') -Value $EnrollmentCode -NoNewline -Encoding ascii
    Write-Host "Enrollment code written to $dataDir\enrollment-code.txt"
}

# 4. Service
$serviceExe = Join-Path $installRoot 'agent\CSL.Vpn.WindowsService.exe'
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}
sc.exe create $serviceName binPath= "`"$serviceExe`"" start= auto | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
sc.exe start $serviceName | Out-Null
Write-Host "Service '$serviceName' installed and started."

# 5. Tray autostart for all users
$trayExe = Join-Path $installRoot 'tray\CSL.Vpn.WindowsTray.exe'
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'CSL VPN Tray' -Value "`"$trayExe`""
Write-Host "Tray app registered for autostart: $trayExe"

Write-Host ""
Write-Host "Done. Status: Get-Service '$serviceName'; state file: $dataDir\state.json"
