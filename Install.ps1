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
    $themeModule = Get-Module GhostUI
    # An update must roll back to its pre-attempt state, not uninstall the
    # previous working installation. Reuse the component's snapshot primitives.
    $checkpoint = & $themeModule {
        param($SelectedProfile)
        $folder = Join-Path $SelectedProfile ('chrome\ghost-ui-backups\combined-' + [Guid]::NewGuid().ToString('N'))
        $payloadExisted = [IO.Directory]::Exists((Join-Path $SelectedProfile 'chrome\ghost-ui'))
        $records = @(New-GhostSnapshot $SelectedProfile $folder ($script:TrackedFiles + @('prefs.js')))
        [pscustomobject]@{ Directory = $folder; Records = $records; PayloadExisted = $payloadExisted }
    } $profile
    $result = Install-GhostUI $profile (Join-Path $PSScriptRoot 'theme')
    $appliedHashes = $null
    try {
        $appliedHashes = & $themeModule {
            param($SelectedProfile)
            $hashes = @{}
            foreach ($file in ($script:TrackedFiles + @('prefs.js'))) { $hashes[$file] = Get-GhostHash (Join-Path $SelectedProfile $file) }
            return $hashes
        } $profile
        Import-Module (Join-Path $PSScriptRoot 'scripts\Handles.psm1') -Force -DisableNameChecking
        # Handles performs its own registry/metadata rollback on failure.
        $handles = Install-GhostHandles -ProfilePath $profile
    } catch {
        $helperError = $_.Exception.Message
        $rollback = 'Profile rollback completed.'
        try {
            Assert-GhostFirefoxClosed
            & $themeModule {
                param($SelectedProfile, $Checkpoint, $Expected)
                Assert-GhostProfile $SelectedProfile | Out-Null
                if (-not $Expected) { throw 'Cannot verify installed state; checkpoint retained for manual recovery.' }
                foreach ($file in $Expected.Keys) {
                    if ((Get-GhostHash (Join-Path $SelectedProfile $file)) -cne $Expected[$file]) {
                        throw 'Profile changed during helper installation; checkpoint retained and current files preserved.'
                    }
                }
                Restore-GhostSnapshot $SelectedProfile $Checkpoint.Directory $Checkpoint.Records
                $payload = Join-Path $SelectedProfile 'chrome\ghost-ui'
                if (-not $Checkpoint.PayloadExisted -and [IO.Directory]::Exists($payload)) {
                    [IO.Directory]::Delete($payload, $false) # Empty directory only; never recursive.
                }
            } $profile $checkpoint $appliedHashes
        } catch { $rollback = 'Profile rollback incomplete: ' + $_.Exception.Message }
        throw ($helperError + ' ' + $rollback + ' Recovery checkpoint: ' + $checkpoint.Directory)
    }
    Write-Host "`nInstalled. Start Firefox to apply Ghost UI." -ForegroundColor Green
    Write-Host "Backup: $($result.Backup)"
    Write-Host 'Adaptive window grips enabled. Ctrl+Alt+F9: move; Ctrl+Alt+F10: return to screen.'
    if ($result.BonjourrStyled) {
        Write-Host 'Bonjourr entrance animations were also installed. Existing Bonjourr settings and search were preserved.'
    } else {
        Write-Host 'Optional: install Bonjourr in Firefox, close Firefox, then run Install.cmd again to add its animations.'
        Write-Host 'https://addons.mozilla.org/firefox/addon/bonjourr-startpage/'
    }
    Write-Host 'Use Uninstall.cmd to restore your original CSS, five appearance preferences, and the bookmarks button addition.'
    exit 0
} catch {
    Write-Host ("Installation stopped: " + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
