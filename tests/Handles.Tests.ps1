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
function With-StateWriteFailure([int[]]$At, [scriptblock]$Action) {
    $module = Get-Module Handles
    $original = & $module { (Get-Item Function:Write-HandlesState).ScriptBlock }
    & $module {
        param($Writer, $Failures)
        $script:TestStateWriter = $Writer
        $script:TestWriteFailures = $Failures
        $script:TestWriteCount = 0
        function script:Write-HandlesState($Root, $State) {
            $script:TestWriteCount++
            if ($script:TestWriteCount -in $script:TestWriteFailures) { throw 'Injected isolated metadata write failure.' }
            & $script:TestStateWriter $Root $State
        }
    } $original $At
    try { & $Action }
    finally { & $module { param($Writer) Set-Item Function:script:Write-HandlesState $Writer } $original }
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
    Uninstall-GhostHandles -ProfilePath $profileB -InstallRoot $install -StartupRegistryPath $registry
    Assert ((Get-ItemProperty -LiteralPath $registry).FirefoxGhostUIHandles -ceq $first.Startup) 'Unregistered profile cannot disturb another registration'
    $held = [IO.File]::Open((Join-Path $install '.installation.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Failure { Install-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry -NoStart } 'Concurrent companion install is rejected'
        Failure { Uninstall-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry } 'Concurrent companion uninstall is rejected'
        Assert ((Get-ItemProperty -LiteralPath $registry).FirefoxGhostUIHandles -ceq $first.Startup) 'Busy lock preserves startup'
    } finally { $held.Dispose() }
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
    Assert (Test-Path -LiteralPath (Join-Path $install 'installed.json')) 'Foreign conflict retains recovery metadata'
    Set-ItemProperty -LiteralPath $registry -Name 'FirefoxGhostUIHandles' -Value $first.Startup
    Uninstall-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry

    # A failed first metadata write must precede all Run-value mutations.
    $metadata = Join-Path $install 'installed.json'
    $legacyRecord = [ordered]@{ profiles = @($profileA); executable = $first.Executable; startup = $first.Startup }
    [IO.File]::WriteAllText($metadata, ($legacyRecord | ConvertTo-Json))
    [IO.File]::SetAttributes($metadata, [IO.FileAttributes]::ReadOnly)
    try {
        Failure { Install-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry -NoStart } 'Read-only metadata rejects installation'
        Assert (-not (Get-ItemProperty -LiteralPath $registry -Name 'FirefoxGhostUIHandles' -ErrorAction SilentlyContinue)) 'Failed journal write leaves no startup entry'
    } finally { [IO.File]::SetAttributes($metadata, [IO.FileAttributes]::Normal) }

    # Upgrade interruption: the old Run value is still covered by the pending claim.
    $futureExe = Join-Path $install '1111111111111111\GhostHandles.exe'
    $pending = [ordered]@{ schema = 2; profiles = @($profileA); executable = $futureExe; startup = ('"' + $futureExe + '"'); previousStartup = $first.Startup; registryConfirmed = $false }
    [IO.File]::WriteAllText($metadata, ($pending | ConvertTo-Json))
    New-ItemProperty -LiteralPath $registry -Name 'FirefoxGhostUIHandles' -Value $first.Startup -PropertyType String -Force | Out-Null
    $repaired = Install-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry -NoStart
    $confirmed = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
    Assert ($confirmed.schema -eq 2 -and $confirmed.registryConfirmed -and -not $confirmed.previousStartup) 'Retry reconciles an interrupted upgrade and clears stale ownership'
    Uninstall-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry
    Assert (-not (Get-ItemProperty -LiteralPath $registry -Name 'FirefoxGhostUIHandles' -ErrorAction SilentlyContinue)) 'Repaired installation uninstalls cleanly'

    [IO.File]::WriteAllText($metadata, ($pending | ConvertTo-Json))
    New-ItemProperty -LiteralPath $registry -Name 'FirefoxGhostUIHandles' -Value $first.Startup -PropertyType String -Force | Out-Null
    Uninstall-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry
    Assert (-not (Test-Path -LiteralPath $metadata)) 'Uninstall handles the pending previous claim directly'
    Assert (-not (Get-ItemProperty -LiteralPath $registry -Name 'FirefoxGhostUIHandles' -ErrorAction SilentlyContinue)) 'Pending previous startup is removed'

    # Schema 1 remains readable without ownership-journal fields.
    [IO.File]::WriteAllText($metadata, ($legacyRecord | ConvertTo-Json))
    New-ItemProperty -LiteralPath $registry -Name 'FirefoxGhostUIHandles' -Value $first.Startup -PropertyType String -Force | Out-Null
    Uninstall-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry
    Assert (-not (Test-Path -LiteralPath $metadata)) 'Legacy metadata remains removable'

    With-StateWriteFailure @(2) {
        Failure { Install-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry -NoStart } 'Confirmation-write failure is reported'
        Assert (-not (Get-ItemProperty -LiteralPath $registry -Name 'FirefoxGhostUIHandles' -ErrorAction SilentlyContinue)) 'Confirmation failure rolls startup back'
        Assert (-not (Test-Path -LiteralPath $metadata)) 'Confirmation failure removes only newly created metadata'
    }
    $legacyText = $legacyRecord | ConvertTo-Json
    [IO.File]::WriteAllText($metadata, $legacyText)
    New-ItemProperty -LiteralPath $registry -Name 'FirefoxGhostUIHandles' -Value $first.Startup -PropertyType String -Force | Out-Null
    With-StateWriteFailure @(2) {
        Failure { Install-GhostHandles -ProfilePath $profileB -InstallRoot $install -StartupRegistryPath $registry -NoStart } 'Failed registration update is reported'
        Assert ([IO.File]::ReadAllText($metadata) -ceq $legacyText) 'Failed update restores exact previous metadata bytes'
        Assert ((Get-ItemProperty -LiteralPath $registry).FirefoxGhostUIHandles -ceq $first.Startup) 'Failed update preserves previous autorun'
    }
    Uninstall-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry
    With-StateWriteFailure @(2,3) {
        Failure { Install-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry -NoStart } 'Failed rollback journal is reported'
        Assert (Test-Path -LiteralPath $metadata) 'Rollback failure retains durable ownership information'
        Assert ((Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json).startup -ceq (Get-ItemProperty -LiteralPath $registry).FirefoxGhostUIHandles) 'Remaining autorun is still tracked'
    }
    Uninstall-GhostHandles -ProfilePath $profileA -InstallRoot $install -StartupRegistryPath $registry
    Assert (-not (Get-ItemProperty -LiteralPath $registry -Name 'FirefoxGhostUIHandles' -ErrorAction SilentlyContinue)) 'Retry can clean up a failed rollback'
    Write-Output "PASS: $checks companion installation checks + $($geometry.passed) geometry/animation checks"
} finally {
    # This unique key is created by this test, never a real Run key.
    if (Test-Path -LiteralPath $registry) { Remove-Item -LiteralPath $registry }
}
