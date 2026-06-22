#Requires -Version 5.1
# Removes TheaterDim: stops it, deletes the logon task and firewall rule.
# (Leaves the cloned repo + %APPDATA%\TheaterDim\settings.json in place.)
[CmdletBinding()]
param([string]$TaskName = 'TheaterDim')
$ErrorActionPreference = 'Continue'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
          ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    $shell = (Get-Process -Id $PID).Path
    Start-Process $shell "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -TaskName `"$TaskName`"" -Verb RunAs
    return
}

Stop-Process -Name TheaterDim -Force -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Get-NetFirewallRule -DisplayName 'TheaterDim Remote' -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue

Write-Host 'TheaterDim uninstalled (task + firewall rule removed).' -ForegroundColor Green
Write-Host 'To wipe settings/token too: Remove-Item "$env:APPDATA\TheaterDim" -Recurse -Force'
if (-not $env:CI) { Read-Host 'Press Enter to close' | Out-Null }
