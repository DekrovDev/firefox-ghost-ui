#requires -Version 5.1
# Trusted functional fixture for a disposable GitHub-hosted Windows user only.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Run this handoff fixture only on a disposable GitHub-hosted Windows runner.' }
if (Get-Process -Name firefox,GhostHandles,GhostUpdater -ErrorAction SilentlyContinue) { throw 'Handoff fixture requires a clean runner process set.' }
$root = Split-Path $PSScriptRoot -Parent
$sandbox = Join-Path $root ('test-output\handoff-' + [Guid]::NewGuid().ToString('N'))
$initial = Join-Path $sandbox 'product-initial'
$target = Join-Path $sandbox 'product-target'
$handlesRoot = Join-Path $sandbox 'handles'
$updatesRoot = Join-Path $sandbox 'updates'
$profile = Join-Path $sandbox 'profile'
$registry = 'HKCU:\Software\FirefoxGhostUI-HandoffTests-' + [Guid]::NewGuid().ToString('N')
function Write-Text([string]$Path, [string]$Body) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Body, [Text.UTF8Encoding]::new($false))
}
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$version = [IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim()
$parts = $version.Split('.')
$nextVersion = '{0}.{1}.{2}' -f $parts[0],$parts[1],([int]$parts[2] + 1)
$copy = @('Install.cmd','Install.ps1','Uninstall.cmd','Uninstall.ps1','Install-Handles.ps1','Uninstall-Handles.ps1','README.md','AGENTS.md','CHANGELOG.md','LICENSE','VERSION','scripts','theme','companion','docs')
foreach ($destination in @($initial,$target)) {
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    foreach ($name in $copy) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $destination -Recurse }
    # Only destination selection differs in the fixture. Ownership, stopping,
    # extraction and installation code are the real product implementation.
    $sourcePath = Join-Path $destination 'companion\GhostUpdates.cs'
    $source = [IO.File]::ReadAllText($sourcePath)
    $defaultRoot = 'Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FirefoxGhostUI", "updates")'
    Assert ($source.Contains($defaultRoot)) 'Fixture could not identify the exact update root expression.'
    Write-Text $sourcePath ($source.Replace($defaultRoot, ($updatesRoot | ConvertTo-Json -Compress)))
    $modulePath = Join-Path $destination 'scripts\Handles.psm1'
    $module = [IO.File]::ReadAllText($modulePath)
    $oldRoot = '(Join-Path $env:LOCALAPPDATA ''FirefoxGhostUI\handles'')'
    $oldRegistry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Assert ($module.Contains($oldRoot) -and $module.Contains($oldRegistry)) 'Fixture could not identify the exact companion defaults.'
    Write-Text $modulePath ($module.Replace($oldRoot, ("'" + $handlesRoot.Replace("'", "''") + "'")).Replace($oldRegistry,$registry))
}
Write-Text (Join-Path $target 'VERSION') $nextVersion
Write-Text (Join-Path $target 'CHANGELOG.md') ("# Changelog`n`n## [$nextVersion] - 2026-10-31`n`n### Summary`n- Validate the installer handoff.`n")
$targetSource = Join-Path $target 'companion\GhostUpdates.cs'
Write-Text $targetSource ([IO.File]::ReadAllText($targetSource) + "`n// Distinct target content hash for the handoff fixture.`n")
$uuidMap = @{ '{4f391a9e-8717-4ba6-a5b1-488a34931fcb}' = '12345678-1234-1234-1234-123456789abc' } | ConvertTo-Json -Compress
Write-Text (Join-Path $profile 'prefs.js') ('user_pref("extensions.webextensions.uuids", ' + ($uuidMap | ConvertTo-Json -Compress) + ');')
try {
    Import-Module (Join-Path $initial 'scripts\GhostUI.psm1') -Force -DisableNameChecking
    Import-Module (Join-Path $initial 'scripts\Handles.psm1') -Force -DisableNameChecking
    Install-GhostUI $profile (Join-Path $initial 'theme') | Out-Null
    $installed = Install-GhostHandles -ProfilePath $profile -InstallRoot $handlesRoot -StartupRegistryPath $registry -NoStart
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /codepage:65001 /target:exe /main:GhostUI.UpdateHandoffParent /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:Accessibility.dll ("/out:" + $installed.Executable) (Join-Path $initial 'companion\GhostHandles.cs') (Join-Path $initial 'companion\GhostUpdates.cs') (Join-Path $root 'tests\UpdateHandoffParent.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Handoff parent fixture compilation failed.' }
    & (Join-Path $target 'scripts\Build-Release.ps1') | Out-Null
    $zip = Join-Path $target ("dist\firefox-ghost-ui-$nextVersion-windows.zip")
    $digest = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.Directory]::CreateDirectory($updatesRoot) | Out-Null
    Copy-Item -LiteralPath $zip -Destination (Join-Path $updatesRoot "package-$nextVersion-$digest.zip")
    $state = @{ Release = @{ Version = $nextVersion; Size = (Get-Item -LiteralPath $zip).Length; Digest = $digest; Summary = @('Validate the installer handoff.') }; Disabled = $true }
    Write-Text (Join-Path $updatesRoot 'updates.json') ($state | ConvertTo-Json -Depth 5)
    $parent = Start-Process -FilePath $installed.Executable -WindowStyle Hidden -PassThru
    Assert ($parent.WaitForExit(60000)) 'Approved companion parent did not exit within the handoff deadline.'
    Assert ($parent.ExitCode -eq 0) 'Approved companion parent did not receive the helper stop signal.'
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 250
        $result = [IO.File]::ReadAllText((Join-Path $updatesRoot 'updates.json')) | ConvertFrom-Json
    } while (-not $result.PSObject.Properties['Result'] -and [DateTime]::UtcNow -lt $deadline)
    Assert ($result.PSObject.Properties['Result'] -and $result.Result -eq 'Update installed. Start Firefox to apply it.') 'Helper did not complete the real installer handoff.'
    $profileState = [IO.File]::ReadAllText((Join-Path $profile 'chrome\ghost-ui\state.json')) | ConvertFrom-Json
    Assert ($profileState.version -eq $nextVersion) 'Installer did not advance the selected profile version.'
    $handleState = [IO.File]::ReadAllText((Join-Path $handlesRoot 'installed.json')) | ConvertFrom-Json
    Assert ($handleState.executable -ne $installed.Executable) 'Target companion did not get a new content identity.'
    Assert ((Get-ItemProperty -LiteralPath $registry).FirefoxGhostUIHandles -eq ('"' + $handleState.executable + '"')) 'Target startup registration differs from the installed companion.'
    Write-Output 'PASS: separate GhostUpdater survives parent exit and real Install.ps1; profile, companion and startup advance together'
} finally {
    Import-Module (Join-Path $target 'scripts\Handles.psm1') -Force -DisableNameChecking
    Uninstall-GhostHandles -ProfilePath $profile -InstallRoot $handlesRoot -StartupRegistryPath $registry
    if (Test-Path -LiteralPath $registry) { Remove-Item -LiteralPath $registry }
}
