#requires -Version 5.1
[CmdletBinding()]
param([string]$ProfilePath, [switch]$NoPrompt)
$ErrorActionPreference = 'Stop'
try {
    Import-Module (Join-Path $PSScriptRoot 'scripts\GhostUI.psm1') -Force -DisableNameChecking
    . (Join-Path $PSScriptRoot 'scripts\Select-Profile.ps1')
    $profile = Select-GhostProfile $ProfilePath -NoPrompt:$NoPrompt
    Write-Host "Installing Firefox Ghost UI into:`n$profile"
    Wait-GhostFirefoxClosed -NoPrompt:$NoPrompt
    $result = Install-GhostUI $profile (Join-Path $PSScriptRoot 'theme')
    Write-Host "`nInstalled. Start Firefox to apply Ghost UI." -ForegroundColor Green
    Write-Host "Backup: $($result.Backup)"
    if ($result.BonjourrStyled) {
        Write-Host 'Bonjourr entrance animations were also installed. Existing Bonjourr settings and search were preserved.'
    } else {
        Write-Host 'Optional: install Bonjourr in Firefox, close Firefox, then run Install.cmd again to add its animations.'
        Write-Host 'https://addons.mozilla.org/firefox/addon/bonjourr-startpage/'
    }
    Write-Host 'Use Uninstall.cmd to restore your original CSS and the five changed Firefox preferences.'
    exit 0
} catch {
    Write-Host ("Installation stopped: " + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
