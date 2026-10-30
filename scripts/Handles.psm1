#requires -Version 5.1
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

function Read-HandlesState([string]$Root) {
    $path = Join-Path $Root 'installed.json'
    if (-not (Test-Path -LiteralPath $path)) {
        return [pscustomobject]@{ schema = 2; profiles = @(); executable = ''; startup = ''; previousStartup = ''; registryConfirmed = $true }
    }
    if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected handles metadata is not supported.' }
    $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    foreach ($name in @('profiles','executable','startup')) {
        if (-not $state.PSObject.Properties[$name]) { throw 'Incomplete handles metadata.' }
    }
    if ($state.profiles -isnot [Array] -or @($state.profiles).Count -eq 0 -or
        $state.executable -isnot [string] -or -not $state.executable -or
        $state.startup -isnot [string] -or -not $state.startup) { throw 'Invalid handles registration.' }
    foreach ($registered in $state.profiles) {
        if ($registered -isnot [string] -or -not [IO.Path]::IsPathRooted($registered)) { throw 'Invalid registered profile path.' }
    }
    if ($state.PSObject.Properties['schema'] -and $state.schema -notin @(1,2)) { throw 'Unsupported handles metadata schema.' }
    if ($state.PSObject.Properties['schema'] -and $state.schema -eq 2 -and
        (-not $state.PSObject.Properties['previousStartup'] -or -not $state.PSObject.Properties['registryConfirmed'])) { throw 'Incomplete startup ownership journal.' }
    if ([bool]$state.PSObject.Properties['previousStartup'] -xor [bool]$state.PSObject.Properties['registryConfirmed']) { throw 'Incomplete startup ownership journal.' }
    if (-not $state.PSObject.Properties['previousStartup']) { $state | Add-Member -NotePropertyName previousStartup -NotePropertyValue '' }
    if (-not $state.PSObject.Properties['registryConfirmed']) { $state | Add-Member -NotePropertyName registryConfirmed -NotePropertyValue $true }
    if ($state.registryConfirmed -isnot [bool]) { throw 'Invalid handles startup confirmation.' }
    if ($null -ne $state.previousStartup -and $state.previousStartup -isnot [string]) { throw 'Invalid previous startup claim.' }
    if ($state.executable) {
        if (-not [IO.Path]::IsPathRooted($state.executable)) { throw 'Handles executable must be an absolute path.' }
        $executable = [IO.Path]::GetFullPath($state.executable)
        if (-not $executable.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($executable) -ine 'GhostHandles.exe' -or $state.startup -cne ('"' + $state.executable + '"')) {
            throw 'Handles installation metadata points outside its directory or has an invalid startup claim.'
        }
    } elseif ($state.startup) { throw 'Incomplete handles executable claim.' }
    if ($state.previousStartup) {
        if ($state.previousStartup -notmatch '^"([^"\r\n]+)"$') { throw 'Invalid previous startup claim.' }
        if (-not [IO.Path]::IsPathRooted($Matches[1])) { throw 'Previous startup must be an absolute path.' }
        $previous = [IO.Path]::GetFullPath($Matches[1])
        if (-not $previous.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($previous) -ine 'GhostHandles.exe') { throw 'Previous startup claim points outside its directory.' }
    }
    return $state
}

function Write-HandlesState([string]$Root, $State) {
    $path = Join-Path $Root 'installed.json'
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Redirected handles metadata is not supported.'
    }
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReadOnly)) {
        throw 'Handles metadata is read-only; startup was not changed.'
    }
    $temporary = Join-Path $Root ('.installed-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $bytes = if ($State -is [byte[]]) { $State } else { [Text.UTF8Encoding]::new($false).GetBytes(($State | ConvertTo-Json -Depth 8)) }
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if (Test-Path -LiteralPath $path) { [IO.File]::Replace($temporary, $path, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $path) }
    } finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
}

function Get-HandlesStartup([string]$RegistryPath) {
    if (-not (Test-Path -LiteralPath $RegistryPath -ErrorAction Stop)) { return [pscustomobject]@{ exists = $false; value = $null } }
    $values = Get-ItemProperty -LiteralPath $RegistryPath -ErrorAction Stop
    $property = $values.PSObject.Properties['FirefoxGhostUIHandles']
    return [pscustomobject]@{ exists = [bool]$property; value = $(if ($property) { $property.Value } else { $null }) }
}

function Test-HandlesOwnedStartup($Entry, $State) {
    if (-not $Entry.exists) { return $true }
    return (($State.startup -and $Entry.value -ceq $State.startup) -or
            (-not $State.registryConfirmed -and $State.previousStartup -and $Entry.value -ceq $State.previousStartup))
}

function Enter-HandlesLock([string]$Root) {
    [IO.Directory]::CreateDirectory($Root) | Out-Null
    $path = Join-Path $Root '.installation.lock'
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Redirected handles lock is not supported.'
    }
    try { return [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw 'Companion installation is busy or its lock is inaccessible. Retry after the other operation finishes.' }
}

function Stop-GhostHandles([string]$Executable) {
    if (-not $Executable) { return }
    $owned = @(Get-Process -Name GhostHandles -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Executable })
    if (-not $owned.Count) { return }
    $signal = $null
    try {
        $signal = [Threading.EventWaitHandle]::OpenExisting('Local\FirefoxGhostUIHandlesStop')
        $signal.Set() | Out-Null
        foreach ($item in $owned) {
            if (-not $item.WaitForExit(5000)) { throw 'Ghost UI handles did not stop. Exit it from its tray menu and retry.' }
        }
    } finally { if ($signal) { $signal.Dispose() } }
}

function Assert-HandlesRoot([string]$Root) {
    $absolute = [IO.Path]::GetFullPath($Root)
    if ($absolute -ne [IO.Path]::GetPathRoot($absolute)) { $absolute = $absolute.TrimEnd('\', '/') }
    if ($absolute -eq [IO.Path]::GetPathRoot($absolute) -or $absolute -eq $env:USERPROFILE -or $absolute -eq $env:LOCALAPPDATA) {
        throw 'Choose a dedicated Ghost UI handles installation directory.'
    }
    # Do not install/remove through junctions, symlinks or redirected ancestor folders.
    $check = $absolute
    while ($check) {
        if ((Test-Path -LiteralPath $check) -and ((Get-Item -LiteralPath $check -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Redirected handles path is not supported: $check"
        }
        $check = [IO.Path]::GetDirectoryName($check)
    }
    return $absolute
}

function Install-GhostHandles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$ProfilePath,
        [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'FirefoxGhostUI\handles'),
        [string]$StartupRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
        [switch]$NoStart
    )
    $root = Assert-HandlesRoot $InstallRoot
    $guard = Enter-HandlesLock $root
    try {
        $profile = [IO.Path]::GetFullPath($ProfilePath)
        if (-not (Test-Path -LiteralPath (Join-Path $profile 'prefs.js'))) { throw 'An initialized Firefox profile is required.' }
        $sourceRoot = Split-Path $PSScriptRoot -Parent
        $state = Read-HandlesState $root
        if ($state.executable -and -not ([IO.Path]::GetFullPath($state.executable)).StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Handles installation metadata points outside its directory.'
        }
        $existing = Get-HandlesStartup $StartupRegistryPath
        if (-not (Test-HandlesOwnedStartup $existing $state)) { throw 'A different startup entry already uses FirefoxGhostUIHandles. It was not replaced.' }
        # Content addressing allows compilation before stopping the previous executable.
        $sourceHashes = @('GhostHandles.cs','GhostHandles.exe.config','GhostHandles.manifest') | ForEach-Object {
            (Get-FileHash -LiteralPath (Join-Path $sourceRoot ('companion\' + $_)) -Algorithm SHA256).Hash
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $sourceHash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($sourceHashes -join ':'))))).Replace('-','').Substring(0,16).ToLowerInvariant() }
        finally { $sha.Dispose() }
        $build = Join-Path $root $sourceHash
        $null = Assert-HandlesRoot $build
        $executable = Join-Path $build 'GhostHandles.exe'
        if (-not (Test-Path -LiteralPath $executable)) {
            & (Join-Path $PSScriptRoot 'Build-Handles.ps1') -OutputDirectory $build | Out-Null
        }
        $startup = '"' + $executable + '"'
        $profiles = @(@($state.profiles) + @($profile) | Select-Object -Unique)
        $previous = if ($existing.exists) { [string]$existing.value } else { '' }
        $next = [pscustomobject]@{ schema = 2; profiles = $profiles; executable = $executable; startup = $startup; previousStartup = $previous; registryConfirmed = $false }
        $metadata = Join-Path $root 'installed.json'
        $hadMetadata = Test-Path -LiteralPath $metadata
        $oldState = if ($hadMetadata) { [IO.File]::ReadAllBytes($metadata) } else { $null }
        $journaled = $false
        $oldRunning = $false
        try {
            # The durable ownership claim must exist before touching the Run value.
            Write-HandlesState $root $next
            $journaled = $true
            $current = Get-HandlesStartup $StartupRegistryPath
            if ($current.exists -ne $existing.exists -or ($current.exists -and $current.value -cne $existing.value)) { throw 'Startup changed during installation; no foreign value was replaced.' }
            if (-not (Test-Path -LiteralPath $StartupRegistryPath)) { New-Item -Path $StartupRegistryPath -Force | Out-Null }
            New-ItemProperty -LiteralPath $StartupRegistryPath -Name 'FirefoxGhostUIHandles' -Value $startup -PropertyType String -Force | Out-Null
            $next.registryConfirmed = $true
            $next.previousStartup = ''
            Write-HandlesState $root $next
            if (-not $NoStart) {
                $oldRunning = [bool]@(Get-Process -Name GhostHandles -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $state.executable }).Count
                Stop-GhostHandles $state.executable
                Start-Process -FilePath $executable -WindowStyle Hidden | Out-Null
            }
        } catch {
            $cause = $_.Exception.Message
            if (-not $journaled) { throw }
            $problems = @()
            try {
                # Keep both claims until registry rollback and metadata restoration finish.
                $next.registryConfirmed = $false; $next.previousStartup = $previous
                Write-HandlesState $root $next
                if (-not $NoStart) { Stop-GhostHandles $executable }
                $current = Get-HandlesStartup $StartupRegistryPath
                if ($current.exists -and -not (Test-HandlesOwnedStartup $current $next)) {
                    throw 'Foreign startup value was preserved; ownership journal retained for manual recovery.'
                }
                if ($existing.exists) {
                    if (-not (Test-Path -LiteralPath $StartupRegistryPath)) { New-Item -Path $StartupRegistryPath -Force | Out-Null }
                    New-ItemProperty -LiteralPath $StartupRegistryPath -Name 'FirefoxGhostUIHandles' -Value $existing.value -PropertyType String -Force | Out-Null
                } elseif ($current.exists) {
                    Remove-ItemProperty -LiteralPath $StartupRegistryPath -Name 'FirefoxGhostUIHandles'
                }
                if ($hadMetadata) { Write-HandlesState $root ([byte[]]$oldState) }
                else { [IO.File]::Delete($metadata) }
            } catch { $problems += $_.Exception.Message }
            if ($oldRunning) {
                try { Start-Process -FilePath $state.executable -WindowStyle Hidden | Out-Null }
                catch { $problems += 'Previous companion could not be restarted.' }
            }
            if ($problems.Count) { throw ($cause + ' Companion rollback incomplete: ' + ($problems -join '; ')) }
            throw ($cause + ' Companion registration rolled back.')
        }
        [pscustomobject]@{ Executable = $executable; Profiles = $profiles; Startup = $startup }
    } finally { $guard.Dispose() }
}

function Uninstall-GhostHandles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$ProfilePath,
        [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'FirefoxGhostUI\handles'),
        [string]$StartupRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    )
    $root = Assert-HandlesRoot $InstallRoot
    if (-not [IO.Directory]::Exists($root)) { return }
    $guard = Enter-HandlesLock $root
    try {
        $state = Read-HandlesState $root
        $profile = [IO.Path]::GetFullPath($ProfilePath)
        if (@($state.profiles).Count -and $profile -notin @($state.profiles)) { return }
        $remaining = @($state.profiles | Where-Object { $_ -ne $profile })
        if ($remaining.Count) {
            $state.profiles = $remaining
            Write-HandlesState $root $state
            return
        }
        if ($state.executable -and -not ([IO.Path]::GetFullPath($state.executable)).StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Handles installation metadata points outside its directory.'
        }
        foreach ($value in @($state.startup, $state.previousStartup) | Select-Object -Unique) {
            if ($value) { Stop-GhostHandles $value.Trim('"') }
        }
        $existing = Get-HandlesStartup $StartupRegistryPath
        if ($existing.exists) {
            if (Test-HandlesOwnedStartup $existing $state) { Remove-ItemProperty -LiteralPath $StartupRegistryPath -Name 'FirefoxGhostUIHandles' }
            else {
                Write-Warning 'Foreign startup value preserved. Check the installation metadata before retrying.'
                return
            }
        }
        # Only remove our metadata. Keep cached binaries so removal never follows an
        # untrusted manifest into recursively deleting a directory or a user file.
        $metadata = Join-Path $root 'installed.json'
        if (Test-Path -LiteralPath $metadata) { Remove-Item -LiteralPath $metadata }
    } finally { $guard.Dispose() }
}

Export-ModuleMember -Function Install-GhostHandles, Uninstall-GhostHandles, Stop-GhostHandles
