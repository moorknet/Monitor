#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Build (optional) and install BlackBox as an automatic Windows service.

.EXAMPLE
  .\install.ps1 -Build          # dotnet publish, then install
  .\install.ps1                 # install from .\publish (already built)
#>
[CmdletBinding()]
param(
    [switch]$Build,
    [string]$PublishDir = (Join-Path $PSScriptRoot 'publish'),
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'BlackBox'),
    [string]$DataDir    = (Join-Path $env:ProgramData 'BlackBox'),
    [switch]$SkipPawnIO
)
$ErrorActionPreference = 'Stop'
$svc = 'BlackBox'

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

# --- 1. build ---------------------------------------------------------------
if ($Build -or -not (Test-Path (Join-Path $PublishDir 'BlackBox.exe'))) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK 8 not found and no publish folder present. Install the .NET 8 SDK or copy a published build to .\publish.' }
    Step "Publishing to $PublishDir"
    dotnet publish (Join-Path $PSScriptRoot 'src\BlackBox.Service') -c Release -r win-x64 -o $PublishDir
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }
    dotnet publish (Join-Path $PSScriptRoot 'src\BlackBox.SensorDump') -c Release -r win-x64 -o (Join-Path $PublishDir 'sensordump')
    if ($LASTEXITCODE) { throw 'dotnet publish (SensorDump) failed' }
}

# --- 2. PawnIO driver (used by LibreHardwareMonitorLib for CPU/SMU/Super I/O access) --------
$pawn = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO' -ErrorAction SilentlyContinue
if ($pawn) {
    Write-Host "PawnIO $($pawn.DisplayVersion) installed"
} elseif (-not $SkipPawnIO) {
    Step 'PawnIO driver not found'
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install --id namazso.PawnIO -e --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE) { Write-Warning 'winget install of PawnIO failed; install it manually from https://pawnio.eu' }
    } else {
        Write-Warning 'winget not available (LTSC). Download and run the PawnIO installer from https://pawnio.eu, then re-run this script.'
        Write-Warning 'Without PawnIO, CPU temps/power and Super I/O (VRM, fans, voltages) will be missing; GPU/NVMe still work.'
    }
}

# --- 3. stop existing service ------------------------------------------------
$existing = Get-Service $svc -ErrorAction SilentlyContinue
if ($existing) {
    Step 'Stopping existing service'
    Stop-Service $svc -Force -ErrorAction SilentlyContinue
    (Get-Service $svc).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

# --- 4. files ----------------------------------------------------------------
Step "Copying to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $PublishDir '*') -Destination $InstallDir -Recurse -Force

# --- 5. data dir: admins + SYSTEM only ---------------------------------------
Step "Data directory $DataDir (admin-only ACL)"
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'logs') | Out-Null
# well-known SIDs so this works on any OS language: S-1-5-32-544 = Administrators, S-1-5-18 = SYSTEM
icacls $DataDir /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
if (-not (Test-Path (Join-Path $DataDir 'config.json'))) {
    Copy-Item (Join-Path $InstallDir 'config.example.json') (Join-Path $DataDir 'config.example.json') -Force -ErrorAction SilentlyContinue
}

# --- 6. service ----------------------------------------------------------------
$exe = Join-Path $InstallDir 'BlackBox.exe'
if (-not $existing) {
    Step 'Creating service'
    New-Service -Name $svc -BinaryPathName "`"$exe`"" -DisplayName 'BlackBox hardware recorder' `
        -Description 'Records sensors, top processes and crash events (http://127.0.0.1:8787)' -StartupType Automatic | Out-Null
} else {
    sc.exe config $svc binPath= "`"$exe`"" start= auto | Out-Null
}
# recovery: restart after 5 s, 5 s, 30 s; reset the failure count after a day
sc.exe failure $svc reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
sc.exe failureflag $svc 1 | Out-Null
# keep the GC's gen0 budget small (default scales with L3 size; the 7800X3D has 96 MB) -> lower private memory
$envVals = @('DOTNET_GCgen0MaxBudget=0x600000')
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$svc" -Name Environment -PropertyType MultiString -Value $envVals -Force | Out-Null

Step 'Starting service'
Start-Service $svc
$ok = $false
foreach ($i in 1..20) {
    Start-Sleep -Milliseconds 500
    try { $st = Invoke-RestMethod 'http://127.0.0.1:8787/api/status' -TimeoutSec 2; $ok = $true; break } catch { }
}
if ($ok) {
    Write-Host ''
    Write-Host "BlackBox running. Viewer: http://127.0.0.1:8787" -ForegroundColor Green
    Write-Host ("  driver: {0}; sensors: {1} (tier1 {2}, tier2 {3})" -f $st.driver, $st.sensors, $st.tier1, $st.tier2)
    Write-Host "  data:   $DataDir"
} else {
    Write-Warning "Service started but the API did not answer yet. Check $DataDir\logs\blackbox.log"
}
