#requires -Version 5.1
[CmdletBinding()]
param([string]$ProfilePath, [switch]$NoPrompt)
$ErrorActionPreference = 'Stop'
try {
    Import-Module (Join-Path $PSScriptRoot 'scripts\GhostUI.psm1') -Force -DisableNameChecking
    . (Join-Path $PSScriptRoot 'scripts\Select-Profile.ps1')
    $profile = Select-GhostProfile $ProfilePath -NoPrompt:$NoPrompt
    Write-Host "Restoring original Firefox configuration in:`n$profile"
    Wait-GhostFirefoxClosed -NoPrompt:$NoPrompt
    $result = Uninstall-GhostUI $profile
    Write-Host 'Uninstalled. Start Firefox to apply the restored configuration.' -ForegroundColor Green
    Write-Host "Backups remain available: $($result.Backup)"
    exit 0
} catch {
    Write-Host ("Uninstall stopped: " + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
