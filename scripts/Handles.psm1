#requires -Version 5.1
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

function Read-HandlesState([string]$Root) {
    $path = Join-Path $Root 'installed.json'
    if (Test-Path -LiteralPath $path) { return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) }
    return [pscustomobject]@{ profiles = @(); executable = ''; startup = '' }
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
    $profile = [IO.Path]::GetFullPath($ProfilePath)
    if (-not (Test-Path -LiteralPath (Join-Path $profile 'prefs.js'))) { throw 'An initialized Firefox profile is required.' }
    $sourceRoot = Split-Path $PSScriptRoot -Parent
    $state = Read-HandlesState $root
    if ($state.executable -and -not ([IO.Path]::GetFullPath($state.executable)).StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Handles installation metadata points outside its directory.'
    }
    $existing = Get-ItemProperty -LiteralPath $StartupRegistryPath -Name 'FirefoxGhostUIHandles' -ErrorAction SilentlyContinue
    if ($existing -and $existing.FirefoxGhostUIHandles -ne $state.startup) { throw 'A different startup entry already uses FirefoxGhostUIHandles. It was not replaced.' }
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
    if (-not $NoStart) { Stop-GhostHandles $state.executable }
    $startup = '"' + $executable + '"'
    if (-not (Test-Path -LiteralPath $StartupRegistryPath)) { New-Item -Path $StartupRegistryPath -Force | Out-Null }
    New-ItemProperty -LiteralPath $StartupRegistryPath -Name 'FirefoxGhostUIHandles' -Value $startup -PropertyType String -Force | Out-Null
    $profiles = @(@($state.profiles) + @($profile) | Select-Object -Unique)
    $next = [ordered]@{ profiles = $profiles; executable = $executable; startup = $startup }
    [IO.File]::WriteAllText((Join-Path $root 'installed.json'), ($next | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    if (-not $NoStart) { Start-Process -FilePath $executable -WindowStyle Hidden | Out-Null }
    [pscustomobject]@{ Executable = $executable; Profiles = $profiles; Startup = $startup }
}

function Uninstall-GhostHandles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$ProfilePath,
        [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'FirefoxGhostUI\handles'),
        [string]$StartupRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    )
    $root = Assert-HandlesRoot $InstallRoot
    $state = Read-HandlesState $root
    $profile = [IO.Path]::GetFullPath($ProfilePath)
    $remaining = @($state.profiles | Where-Object { $_ -ne $profile })
    if ($remaining.Count) {
        $state.profiles = $remaining
        [IO.File]::WriteAllText((Join-Path $root 'installed.json'), ($state | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
        return
    }
    if ($state.executable -and -not ([IO.Path]::GetFullPath($state.executable)).StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Handles installation metadata points outside its directory.'
    }
    Stop-GhostHandles $state.executable
    $existing = Get-ItemProperty -LiteralPath $StartupRegistryPath -Name 'FirefoxGhostUIHandles' -ErrorAction SilentlyContinue
    if ($existing -and $existing.FirefoxGhostUIHandles -eq $state.startup) {
        Remove-ItemProperty -LiteralPath $StartupRegistryPath -Name 'FirefoxGhostUIHandles'
    }
    # Only remove our metadata. Keep cached binaries so removal never follows an
    # untrusted manifest into recursively deleting a directory or a user file.
    $metadata = Join-Path $root 'installed.json'
    if (Test-Path -LiteralPath $metadata) { Remove-Item -LiteralPath $metadata }
}
Export-ModuleMember -Function Install-GhostHandles, Uninstall-GhostHandles, Stop-GhostHandles
