#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ProfilePath)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'scripts\Handles.psm1') -Force -DisableNameChecking
$result = Install-GhostHandles -ProfilePath $ProfilePath
Write-Host 'Adaptive window grips installed. Approach a visible edge of a restored Firefox window.'
Write-Host 'Move: Ctrl+Alt+F9. Return to the pointer monitor: Ctrl+Alt+F10. Exit/pause: Ghost UI tray icon.'
Write-Output $result
