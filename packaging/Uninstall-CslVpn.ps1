#requires -RunAsAdministrator
<#
.SYNOPSIS
  Removes the CSL VPN agent. Keeps C:\ProgramData\CSL VPN (device identity)
  unless -Purge is given.
#>
param([switch]$Purge)

$ErrorActionPreference = 'Stop'
$serviceName = 'CSL VPN Agent'

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
}

Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'CSL VPN Tray' -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force 'C:\Program Files\CSL VPN' -ErrorAction SilentlyContinue

if ($Purge) {
    Remove-Item -Recurse -Force 'C:\ProgramData\CSL VPN' -ErrorAction SilentlyContinue
    Write-Host "Removed including device identity (the device will re-enroll as new)."
} else {
    Write-Host "Binaries removed; $env:ProgramData\CSL VPN kept (reinstall keeps the VPN address)."
}
