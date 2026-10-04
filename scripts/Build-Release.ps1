#requires -Version 5.1
[CmdletBinding()]
param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a numeric version such as 1.0.0.' }
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
[IO.Directory]::CreateDirectory($dist) | Out-Null
$output = Join-Path $dist "firefox-ghost-ui-$Version-windows.zip"
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($output, [IO.Compression.ZipArchiveMode]::Create)
try {
    # Explicit list: no profiles, state, private backups, screenshots, or Git metadata.
    foreach ($name in @('Install.cmd','Install.ps1','Uninstall.cmd','Uninstall.ps1',
                         'README.md','README.ru.md','LICENSE','theme/userChrome.css','theme/bonjourr.css',
                         'scripts/GhostUI.psm1','scripts/Select-Profile.ps1')) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root $name), "firefox-ghost-ui/$name") | Out-Null
    }
} finally { $zip.Dispose() }
$stream = [IO.File]::OpenRead($output)
$sha = [Security.Cryptography.SHA256]::Create()
try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
finally { $sha.Dispose(); $stream.Dispose() }
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($output))`n", [Text.UTF8Encoding]::new($false))
Write-Host "Built $output"
