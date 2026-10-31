#requires -Version 5.1
[CmdletBinding()]
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $PSBoundParameters.ContainsKey('Version')) { $Version = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim() }
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Use a stable numeric SemVer such as 1.0.0.' }
$dist = Join-Path $root 'dist'
[IO.Directory]::CreateDirectory($dist) | Out-Null
& (Join-Path $PSScriptRoot 'Write-ReleaseNotes.ps1') -Version $Version -AllowUnreleased -OutputPath (Join-Path $dist 'RELEASE_NOTES.md')
$output = Join-Path $dist "firefox-ghost-ui-$Version-windows.zip"
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($output, [IO.Compression.ZipArchiveMode]::Create)
try {
    # Explicit list: only distributable files and approved demo screenshots.
    foreach ($name in @('Install.cmd','Install.ps1','Uninstall.cmd','Uninstall.ps1',
                         'README.md','AGENTS.md','CHANGELOG.md','LICENSE','theme/userChrome.css','theme/bonjourr.css',
                         'scripts/GhostUI.psm1','scripts/Select-Profile.ps1',
                         'Install-Handles.ps1','Uninstall-Handles.ps1','scripts/Handles.psm1','scripts/Build-Handles.ps1',
                         'companion/GhostHandles.cs','companion/GhostUpdates.cs','companion/GhostHandles.exe.config','companion/GhostHandles.manifest',
                         'VERSION','docs/adaptive-handles.md','docs/technical.md','docs/extensions-panel-workaround.md','docs/updates.md',
                         'docs/images/new-tab.png','docs/images/floating-toolbar.png','docs/images/sidebar.png')) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root $name), "firefox-ghost-ui/$name") | Out-Null
    }
} finally { $zip.Dispose() }
$stream = [IO.File]::OpenRead($output)
$sha = [Security.Cryptography.SHA256]::Create()
try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
finally { $sha.Dispose(); $stream.Dispose() }
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($output))`n", [Text.UTF8Encoding]::new($false))
Write-Host "Built $output"
