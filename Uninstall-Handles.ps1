#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ProfilePath)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'scripts\Handles.psm1') -Force -DisableNameChecking
Uninstall-GhostHandles -ProfilePath $ProfilePath
Write-Host 'Window-grip registration removed for this profile. The Firefox CSS theme was preserved.'
