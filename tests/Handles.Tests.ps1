#requires -Version 5.1
# Compiles real source and tests geometry + install/update/remove in an isolated
# folder and a unique HKCU test key. Does not move or inspect personal Firefox.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'scripts\Handles.psm1') -Force -DisableNameChecking
$sandbox = Join-Path $root ('test-output\handles-tests-' + [Guid]::NewGuid().ToString('N'))
$registry = 'HKCU:\Software\FirefoxGhostUI-Tests-' + [Guid]::NewGuid().ToString('N')
$install = Join-Path $sandbox 'installation with spaces'
$profileA = Join-Path $sandbox 'profile A'
$profileB = Join-Path $sandbox 'profile B'
$checks = 0
function Assert([bool]$Condition, [string]$Reason) { if (-not $Condition) { throw $Reason }; $script:checks++ }
function Failure([scriptblock]$Action, [string]$Reason) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Assert $failed $Reason
}
try {
    foreach ($profile in @($profileA, $profileB)) {
        [IO.Directory]::CreateDirectory($profile) | Out-Null
        [IO.File]::WriteAllText((Join-Path $profile 'prefs.js'), '// isolated test profile')
    }
    Failure { Install-GhostHandles -ProfilePath (Join-Path $sandbox 'missing') -InstallRoot $install -StartupRegistryPath $registry -NoStart } 'Must reject non-profile'
    $first = Install-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry -NoStart
    Assert (Test-Path -LiteralPath $first.Executable) 'Real executable compiled'
    Assert (Test-Path -LiteralPath ($first.Executable + '.config')) 'DPI configuration copied'
    Assert ($first.Startup -eq ('"' + $first.Executable + '"')) 'Startup quotes space-containing path'
    Assert ((Get-ItemProperty -LiteralPath $registry).FirefoxGhostUIHandles -eq $first.Startup) 'Startup entry matches installed executable'
    $test = Start-Process -FilePath $first.Executable -ArgumentList '--self-test' -WindowStyle Hidden -PassThru -Wait
    Assert ($test.ExitCode -eq 0) 'Compiled geometry checks pass'
    $geometry = Get-Content -LiteralPath (Join-Path (Split-Path $first.Executable -Parent) 'self-test-results.json') -Raw | ConvertFrom-Json
    Assert ($geometry.passed -ge 324) 'Geometry, native recovery without focus, efficient process identity, DPI and alpha checks actually executed'
    $again = Install-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry -NoStart
    Assert ($again.Profiles.Count -eq 1) 'Repeated install does not duplicate profile'
    Assert ($again.Executable -eq $first.Executable) 'Identical source reuses binary'
    $second = Install-GhostHandles -ProfilePath $profileB -InstallRoot $install -StartupRegistryPath $registry -NoStart
    Assert ($second.Profiles.Count -eq 2) 'Second profile registered'
    Uninstall-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry
    Assert ((Get-ItemProperty -LiteralPath $registry).FirefoxGhostUIHandles -eq $first.Startup) 'Removing one profile keeps companion for other profile'
    Assert (@((Get-Content -LiteralPath (Join-Path $install 'installed.json') -Raw | ConvertFrom-Json).profiles).Count -eq 1) 'Only remaining profile retained'
    Uninstall-GhostHandles -ProfilePath $profileB -InstallRoot $install -StartupRegistryPath $registry
    Assert (-not (Get-ItemProperty -LiteralPath $registry -Name 'FirefoxGhostUIHandles' -ErrorAction SilentlyContinue)) 'Last profile removes startup entry'
    Assert (-not (Test-Path -LiteralPath (Join-Path $install 'installed.json'))) 'Last profile removes registration'
    Assert (Test-Path -LiteralPath $first.Executable) 'Removal retains inert cached binary, never recursively deletes'
    $null = Install-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry -NoStart
    Set-ItemProperty -LiteralPath $registry -Name 'FirefoxGhostUIHandles' -Value 'foreign application'
    Failure { Install-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry -NoStart } 'Foreign startup entry is never overwritten'
    Uninstall-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry
    Assert ((Get-ItemProperty -LiteralPath $registry).FirefoxGhostUIHandles -eq 'foreign application') 'Foreign startup entry survives uninstall'
    Write-Output "PASS: $checks companion installation checks + $($geometry.passed) geometry/animation checks"
} finally {
    # This unique key is created by this test, never a real Run key.
    if (Test-Path -LiteralPath $registry) { Remove-Item -LiteralPath $registry }
}
