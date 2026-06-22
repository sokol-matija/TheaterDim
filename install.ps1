#Requires -Version 5.1
<#
  TheaterDim installer.
  - Ensures .NET 9 SDK (auto-installs via winget if missing).
  - Publishes a self-contained single-file exe (no runtime needed to RUN).
  - Registers a hidden logon Scheduled Task so it survives restart.
  - Adds a Private-network firewall rule so the phone remote is reachable.
  Run:  right-click > Run with PowerShell   (it will self-elevate for the firewall step)
  or:   powershell -ExecutionPolicy Bypass -File install.ps1
#>
[CmdletBinding()]
param(
    [string]$TaskName = 'TheaterDim',
    [int]$Port = 8777
)
$ErrorActionPreference = 'Stop'

# --- self-elevate (needed for the firewall rule; task reg uses the same user SID) ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
          ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Elevating (UAC prompt)...' -ForegroundColor Yellow
    $shell = (Get-Process -Id $PID).Path   # same host: powershell.exe or pwsh.exe
    Start-Process $shell "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -TaskName `"$TaskName`" -Port $Port" -Verb RunAs
    return
}

$root   = $PSScriptRoot
$proj   = Join-Path $root 'TheaterDim.csproj'
$pubDir = Join-Path $root 'publish'
$exe    = Join-Path $pubDir 'TheaterDim.exe'

Write-Host '== TheaterDim installer ==' -ForegroundColor Cyan

# --- 1. ensure .NET SDK ---
function Test-Dotnet { try { return ((dotnet --version) 2>$null) -match '^\d' } catch { return $false } }
if (-not (Test-Dotnet)) {
    Write-Host '.NET SDK not found - installing via winget...' -ForegroundColor Yellow
    winget install --id Microsoft.DotNet.SDK.9 -e --accept-source-agreements --accept-package-agreements
    $env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
                [Environment]::GetEnvironmentVariable('Path','User')
}
if (-not (Test-Dotnet)) { throw '.NET 9 SDK not available. Install it and re-run.' }

# --- 2. stop any running instance (release exe lock) ---
Stop-Process -Name TheaterDim -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# --- 3. publish self-contained single-file ---
Write-Host 'Building self-contained exe...' -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $pubDir
if (-not (Test-Path $exe)) { throw "Publish failed - $exe not found." }

# --- 4. register hidden logon task ---
Write-Host "Registering logon task '$TaskName'..." -ForegroundColor Cyan
$sid = ([Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
$action    = New-ScheduledTaskAction -Execute $exe
$trigger   = New-ScheduledTaskTrigger -AtLogOn
$trigger.UserId = $sid
$principal = New-ScheduledTaskPrincipal -UserId $sid -LogonType Interactive -RunLevel Limited
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew -Hidden -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings `
    -Description 'TheaterDim: multi-monitor dimming + PotPlayer web remote' -Force | Out-Null

# --- 5. firewall rule for the LAN remote ---
Get-NetFirewallRule -DisplayName 'TheaterDim Remote' -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName 'TheaterDim Remote' -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort $Port -Profile Private -Program $exe | Out-Null

# --- 6. start now ---
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 2

# --- 7. report ---
$proc = Get-Process TheaterDim -ErrorAction SilentlyContinue
$idx  = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).InterfaceIndex
$ip   = (Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $idx -ErrorAction SilentlyContinue).IPAddress
$cfg  = Join-Path $env:APPDATA 'TheaterDim\settings.json'
$token = ''
if (Test-Path $cfg) { try { $token = (Get-Content $cfg -Raw | ConvertFrom-Json).Token } catch {} }

Write-Host ''
if ($proc) { Write-Host "OK - running (pid $($proc.Id))." -ForegroundColor Green }
else       { Write-Host 'WARN - process not detected; check Task Scheduler > TheaterDim.' -ForegroundColor Yellow }
Write-Host 'Tray: clapperboard icon (click the ^ overflow near the clock).'
Write-Host 'Hotkey: Ctrl+Alt+T toggles theater dim.'
if ($token -and $ip) { Write-Host "Phone remote: http://${ip}:$Port/?t=$token" -ForegroundColor Cyan }
Write-Host 'Done. Survives restart via the logon task.' -ForegroundColor Green
if (-not $env:CI) { Read-Host 'Press Enter to close' | Out-Null }
