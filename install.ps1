#Requires -Version 5.1
<#
  TheaterDim installer (dual mode).
  - If a prebuilt TheaterDim.exe sits next to this script (the Release zip),
    it is used directly -> no .NET SDK, no build, no internet needed.
  - Otherwise (a source checkout) it auto-installs the .NET 9 SDK via winget
    and publishes a self-contained exe.
  Either way it copies the exe to %LOCALAPPDATA%\TheaterDim, registers a hidden
  logon task (survives restart), opens the LAN firewall port, and starts it.

  Run:  double-click Install.bat   (or: right-click install.ps1 > Run with PowerShell)
#>
[CmdletBinding()]
param(
    [string]$TaskName = 'TheaterDim',
    [int]$Port = 8777
)
$ErrorActionPreference = 'Stop'

# --- self-elevate (firewall rule needs admin; task uses the same user SID) ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
          ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Elevating (UAC prompt)...' -ForegroundColor Yellow
    $shell = (Get-Process -Id $PID).Path
    Start-Process $shell "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -TaskName `"$TaskName`" -Port $Port" -Verb RunAs
    return
}

$root = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'TheaterDim'
$exe  = Join-Path $dest 'TheaterDim.exe'

Write-Host '== TheaterDim installer ==' -ForegroundColor Cyan

# --- locate or build the exe ---
$srcExe = $null
if     (Test-Path (Join-Path $root 'TheaterDim.exe'))          { $srcExe = Join-Path $root 'TheaterDim.exe' }          # release zip
elseif (Test-Path (Join-Path $root 'publish\TheaterDim.exe'))  { $srcExe = Join-Path $root 'publish\TheaterDim.exe' }   # already published

if (-not $srcExe) {
    # source checkout -> build self-contained
    $proj = Join-Path $root 'TheaterDim.csproj'
    if (-not (Test-Path $proj)) { throw 'No TheaterDim.exe and no TheaterDim.csproj found next to this script.' }

    function Test-Dotnet { try { return ((dotnet --version) 2>$null) -match '^\d' } catch { return $false } }
    if (-not (Test-Dotnet)) {
        Write-Host '.NET SDK not found - installing via winget...' -ForegroundColor Yellow
        winget install --id Microsoft.DotNet.SDK.9 -e --accept-source-agreements --accept-package-agreements
        $env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
                    [Environment]::GetEnvironmentVariable('Path','User')
    }
    if (-not (Test-Dotnet)) { throw '.NET 9 SDK not available. Install it and re-run.' }

    Stop-Process -Name TheaterDim -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    Write-Host 'Building self-contained exe...' -ForegroundColor Cyan
    dotnet publish $proj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o (Join-Path $root 'publish')
    $srcExe = Join-Path $root 'publish\TheaterDim.exe'
    if (-not (Test-Path $srcExe)) { throw "Publish failed - $srcExe not found." }
}

# --- copy exe to a stable location (so the download folder can be deleted) ---
Stop-Process -Name TheaterDim -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $srcExe $exe -Force
Write-Host "Installed to $exe" -ForegroundColor DarkGray

# --- register hidden logon task ---
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

# --- firewall rule for the LAN remote ---
Get-NetFirewallRule -DisplayName 'TheaterDim Remote' -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName 'TheaterDim Remote' -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort $Port -Profile Private -Program $exe | Out-Null

# --- start now ---
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 2

# --- report ---
$proc = Get-Process TheaterDim -ErrorAction SilentlyContinue
$idx  = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).InterfaceIndex
$ip   = (Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $idx -ErrorAction SilentlyContinue).IPAddress
$cfg  = Join-Path $env:APPDATA 'TheaterDim\settings.json'
$token = ''
if (Test-Path $cfg) { try { $token = (Get-Content $cfg -Raw | ConvertFrom-Json).Token } catch {} }

Write-Host ''
if ($proc) { Write-Host "OK - running (pid $($proc.Id))." -ForegroundColor Green }
else       { Write-Host 'WARN - not detected; open Task Scheduler > TheaterDim.' -ForegroundColor Yellow }
Write-Host 'Tray: clapperboard icon (click the ^ overflow near the clock).'
Write-Host 'Hotkey: Ctrl+Alt+T toggles theater dim.'
Write-Host 'Phone QR: right-click tray > Web remote > Show phone URL / QR.'
if ($token -and $ip) { Write-Host "Phone remote: http://${ip}:$Port/?t=$token" -ForegroundColor Cyan }
Write-Host 'Done - survives restart via the logon task.' -ForegroundColor Green
if (-not $env:CI) { Read-Host 'Press Enter to close' | Out-Null }
