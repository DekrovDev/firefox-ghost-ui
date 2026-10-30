#requires -Version 5.1
# End-to-end local ZIP install/remove, with an isolated profile and startup key.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
# Reject unsafe/noncanonical versions before any output path is used.
foreach ($invalidVersion in @('', '01.1.1', '1.1', '../1.1.1', '1.1.1-rc.1')) {
    $rejected = $false
    try { & (Join-Path $root 'scripts\Build-Release.ps1') -Version $invalidVersion }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid version accepted: $invalidVersion" }
}
& (Join-Path $root 'scripts\Build-Release.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sandbox = Join-Path $root ('test-output\package-' + [Guid]::NewGuid().ToString('N'))
$registry = 'HKCU:\Software\FirefoxGhostUI-PackageTests-' + [Guid]::NewGuid().ToString('N')
[IO.Directory]::CreateDirectory($sandbox) | Out-Null
$version = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim()
$archive = Join-Path $root "dist\firefox-ghost-ui-$version-windows.zip"
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $sandbox)
$package = Join-Path $sandbox 'firefox-ghost-ui'
$profile = Join-Path $sandbox 'disposable profile'
[IO.Directory]::CreateDirectory($profile) | Out-Null
[IO.File]::WriteAllText((Join-Path $profile 'prefs.js'), '// isolated package test')
try {
    # English-only release: reject translated documentation and Cyrillic in packaged text.
    $textExtensions = @('.md', '.ps1', '.psm1', '.cs', '.css', '.cmd', '.config', '.manifest')
    foreach ($file in @(Get-ChildItem -LiteralPath $package -File -Recurse)) {
        if ($file.Name -like '*.ru.md') { throw "Translation accidentally packaged: $($file.FullName)" }
        if ($textExtensions -contains $file.Extension) {
            $body = [IO.File]::ReadAllText($file.FullName)
            if ($body -match '[\u0400-\u052F]') { throw "Cyrillic text found in package: $($file.FullName)" }
        }
    }
    foreach ($name in @('Install-Handles.ps1','Uninstall-Handles.ps1','docs\adaptive-handles.md','docs\technical.md','docs\extensions-panel-workaround.md','VERSION',
                       'companion\GhostHandles.cs','companion\GhostHandles.exe.config','companion\GhostHandles.manifest')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package $name))) { throw "Missing packaged file: $name" }
    }
    if ([IO.File]::ReadAllText((Join-Path $package 'VERSION')).Trim() -cne $version) { throw 'Packaged version mismatch.' }
    $expectedHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedLine = "$expectedHash  firefox-ghost-ui-$version-windows.zip"
    if ([IO.File]::ReadAllText((Join-Path $root 'dist\SHA256SUMS.txt')).Trim() -cne $expectedLine) { throw 'Checksum mismatch.' }
    foreach ($doc in @(Get-ChildItem -LiteralPath $package -Filter '*.md' -Recurse -File)) {
        foreach ($link in [regex]::Matches([IO.File]::ReadAllText($doc.FullName), '\]\(([^)]+)\)')) {
            $target = $link.Groups[1].Value
            if ($target -match '^(https?://|#)') { continue }
            $target = $target.Split('#')[0]
            if (-not (Test-Path -LiteralPath (Join-Path $doc.DirectoryName $target))) { throw "Broken packaged link: $target" }
        }
    }
    Import-Module (Join-Path $package 'scripts\GhostUI.psm1') -Force -DisableNameChecking
    Import-Module (Join-Path $package 'scripts\Handles.psm1') -Force -DisableNameChecking
    Install-GhostUI $profile (Join-Path $package 'theme') | Out-Null
    $profileState = [IO.File]::ReadAllText((Join-Path $profile 'chrome\ghost-ui\state.json')) | ConvertFrom-Json
    if ($profileState.version -cne $version) { throw 'Installed profile version differs from packaged VERSION.' }
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
