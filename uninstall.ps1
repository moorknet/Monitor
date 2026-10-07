#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Remove the BlackBox service and program files. Recorded data is kept unless -RemoveData is given.
  The PawnIO driver is left installed (other tools may use it); remove it via Apps & Features if wanted.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'BlackBox'),
    [string]$DataDir    = (Join-Path $env:ProgramData 'BlackBox'),
    [switch]$RemoveData
)
$ErrorActionPreference = 'Stop'
$svc = 'BlackBox'

if (Get-Service $svc -ErrorAction SilentlyContinue) {
    Write-Host "Stopping and deleting service $svc"
    Stop-Service $svc -Force -ErrorAction SilentlyContinue
    (Get-Service $svc).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    sc.exe delete $svc | Out-Null
}
if (Test-Path $InstallDir) {
    Write-Host "Removing $InstallDir"
    Remove-Item $InstallDir -Recurse -Force
}
if ($RemoveData -and (Test-Path $DataDir)) {
    Write-Host "Removing data $DataDir"
    Remove-Item $DataDir -Recurse -Force
} elseif (Test-Path $DataDir) {
    Write-Host "Data kept in $DataDir (use -RemoveData to delete)"
}
Write-Host 'Done.'
