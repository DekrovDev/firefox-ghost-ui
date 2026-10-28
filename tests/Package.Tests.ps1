#requires -Version 5.1
# End-to-end local ZIP install/remove, with an isolated profile and startup key.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $root 'scripts\Build-Release.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sandbox = Join-Path $root ('test-output\package-' + [Guid]::NewGuid().ToString('N'))
$registry = 'HKCU:\Software\FirefoxGhostUI-PackageTests-' + [Guid]::NewGuid().ToString('N')
[IO.Directory]::CreateDirectory($sandbox) | Out-Null
$archive = Join-Path $root 'dist\firefox-ghost-ui-1.1.1-windows.zip'
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $sandbox)
$package = Join-Path $sandbox 'firefox-ghost-ui'
$profile = Join-Path $sandbox 'disposable profile'
[IO.Directory]::CreateDirectory($profile) | Out-Null
[IO.File]::WriteAllText((Join-Path $profile 'prefs.js'), '// isolated package test')
try {
    foreach ($name in @('Install-Handles.ps1','Uninstall-Handles.ps1','docs\adaptive-handles.md',
                       'companion\GhostHandles.cs','companion\GhostHandles.exe.config','companion\GhostHandles.manifest')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package $name))) { throw "Missing packaged file: $name" }
    }
    Import-Module (Join-Path $package 'scripts\GhostUI.psm1') -Force -DisableNameChecking
    Import-Module (Join-Path $package 'scripts\Handles.psm1') -Force -DisableNameChecking
    Install-GhostUI $profile (Join-Path $package 'theme') | Out-Null
    $installed = Install-GhostHandles -ProfilePath $profile -InstallRoot (Join-Path $sandbox 'companion') -StartupRegistryPath $registry -NoStart
    if (-not (Test-Path -LiteralPath $installed.Executable)) { throw 'Packaged source did not compile into installed executable.' }
    if (-not (Test-Path -LiteralPath (Join-Path $profile 'chrome\ghost-ui\userChrome.css'))) { throw 'Packaged theme did not install.' }
    Uninstall-GhostHandles -ProfilePath $profile -InstallRoot (Join-Path $sandbox 'companion') -StartupRegistryPath $registry
    Uninstall-GhostUI $profile | Out-Null
    if (Test-Path -LiteralPath (Join-Path $profile 'chrome\userChrome.css')) { throw 'Packaged theme did not uninstall.' }
    if (Get-ItemProperty -LiteralPath $registry -Name 'FirefoxGhostUIHandles' -ErrorAction SilentlyContinue) { throw 'Packaged companion left startup entry.' }
    Write-Output 'PASS: packaged theme + real companion compile/install/remove'
} finally {
    if (Test-Path -LiteralPath $registry) { Remove-Item -LiteralPath $registry }
}
