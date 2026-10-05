Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$script:Version = '1.0.8'
$script:PreferenceValues = [ordered]@{
    'toolkit.legacyUserProfileCustomizations.stylesheets' = $true
    'sidebar.revamp' = $true
    'sidebar.verticalTabs' = $true
    'sidebar.visibility' = 'expand-on-hover'
    'browser.tabs.inTitlebar' = 1
    'browser.download.alwaysOpenPanel' = $false
    'browser.download.panel.shown' = $true
}
$script:TrackedFiles = @('user.js', 'chrome/userChrome.css', 'chrome/userContent.css',
    'chrome/userChrome.ghost-ui-original.css', 'chrome/userContent.ghost-ui-original.css',
    'chrome/ghost-ui/userChrome.css', 'chrome/ghost-ui/userContent.css', 'chrome/ghost-ui/state.json')

function Get-GhostIOPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if ($absolute.Length -lt 240 -or $absolute.StartsWith('\\?\')) { return $absolute }
    if ($absolute.StartsWith('\\')) { return '\\?\UNC\' + $absolute.Substring(2) }
    return '\\?\' + $absolute
}

function Test-GhostFile([string]$Path) { return [IO.File]::Exists((Get-GhostIOPath $Path)) }
function Test-GhostPath([string]$Path) {
    $ioPath = Get-GhostIOPath $Path
    return ([IO.File]::Exists($ioPath) -or [IO.Directory]::Exists($ioPath))
}
function New-GhostDirectory([string]$Path) { [IO.Directory]::CreateDirectory((Get-GhostIOPath $Path)) | Out-Null }
function Copy-GhostFile([string]$Source, [string]$Target, [bool]$Overwrite) {
    [IO.File]::Copy((Get-GhostIOPath $Source), (Get-GhostIOPath $Target), $Overwrite)
}
function Remove-GhostFile([string]$Path) { [IO.File]::Delete((Get-GhostIOPath $Path)) }

function Write-GhostText([string]$Path, [string]$Text) {
    New-GhostDirectory ([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText((Get-GhostIOPath $Path), $Text, [Text.UTF8Encoding]::new($false))
}

function Read-GhostText([string]$Path) {
    if (Test-GhostFile $Path) { return [IO.File]::ReadAllText((Get-GhostIOPath $Path)) }
    return ''
}

function Get-GhostHash([string]$Path) {
    if (Test-GhostFile $Path) {
        $stream = [IO.File]::OpenRead((Get-GhostIOPath $Path))
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose(); $stream.Dispose() }
    }
    return $null
}

function Get-GhostPreference([string]$Text, [string]$Name) {
    $pattern = '(?m)^\s*user_pref\("' + [regex]::Escape($Name) + '",\s*(.+)\);\s*$'
    $found = [regex]::Matches($Text, $pattern)
    if ($found.Count -gt 0) {
        return [pscustomobject]@{ name = $Name; exists = $true; value = ($found[$found.Count-1].Groups[1].Value | ConvertFrom-Json) }
    }
    return [pscustomobject]@{ name = $Name; exists = $false; value = $null }
}

function Set-GhostPreference([string]$Text, [string]$Name, [bool]$Exists, $Value) {
    $pattern = '(?m)^\s*user_pref\("' + [regex]::Escape($Name) + '",\s*.+\);[^\S\r\n]*\r?\n?'
    $result = [regex]::Replace($Text, $pattern, '')
    if ($Exists) { $result += "`r`n" + 'user_pref(' + ($Name | ConvertTo-Json -Compress) + ', ' + ($Value | ConvertTo-Json -Compress -Depth 20) + ");`r`n" }
    return $result
}

function Add-GhostBookmarksButton([string]$Profile, $State) {
    # A user.js layout is an intentional persistent override; do not compete.
    $pinned = Get-GhostPreference (Read-GhostText (Join-Path $Profile 'user.js')) 'browser.uiCustomization.state'
    $prefsPath = Join-Path $Profile 'prefs.js'
    $text = Read-GhostText $prefsPath
    $original = Get-GhostPreference $text 'browser.uiCustomization.state'
    $record = [pscustomobject]@{ added = $false; original = $original; applied = $null }
    if ($pinned.exists) { return $record }
    if ($original.exists) {
        $layout = $original.value | ConvertFrom-Json
        if (-not $layout.PSObject.Properties['placements']) { throw 'Firefox toolbar layout is incomplete. No layout was replaced.' }
    } else {
        # Firefox 156's core navbar; missing areas retain Firefox's defaults.
        $layout = [pscustomobject]@{ currentVersion = 26; placements = [pscustomobject]@{
            'nav-bar' = @('sidebar-button','back-button','forward-button','stop-reload-button','spring','vertical-spacer','urlbar-container','spring','downloads-button','ipprotection-button','fxa-toolbar-menu-button','reset-pbm-toolbar-button','unified-extensions-button')
        } }
    }
    foreach ($area in $layout.placements.PSObject.Properties) {
        if (@($area.Value) -contains 'bookmarks-menu-button') { return $record }
    }
    if (-not $layout.placements.PSObject.Properties['nav-bar']) { throw 'Firefox navigation toolbar layout is missing.' }
    $nav = [Collections.Generic.List[string]]::new()
    foreach ($widget in $layout.placements.'nav-bar') { $nav.Add([string]$widget) }
    $index = $nav.IndexOf('downloads-button')
    if ($index -lt 0) { $index = $nav.IndexOf('urlbar-container') + 1 }
    $nav.Insert([Math]::Max(0,$index), 'bookmarks-menu-button')
    $layout.placements.'nav-bar' = $nav.ToArray()
    $record.added = $true
    $record.applied = $layout | ConvertTo-Json -Compress -Depth 20
    Write-GhostText $prefsPath (Set-GhostPreference $text 'browser.uiCustomization.state' $true $record.applied)
    return $record
}

function Remove-GhostBookmarksButton([string]$Text, $Record) {
    if (-not $Record.added) { return $Text }
    $current = Get-GhostPreference $Text 'browser.uiCustomization.state'
    if (-not $current.exists) { return $Text }
    if ($current.value -ceq $Record.applied) {
        return Set-GhostPreference $Text 'browser.uiCustomization.state' $Record.original.exists $Record.original.value
    }
    # Firefox or the user may have rearranged other widgets after installation.
    # Remove our one addition, keeping every other current placement and field.
    $layout = $current.value | ConvertFrom-Json
    if (-not $layout.PSObject.Properties['placements']) { throw 'Firefox toolbar layout is incomplete. No layout was replaced.' }
    foreach ($area in $layout.placements.PSObject.Properties) {
        $area.Value = @($area.Value | Where-Object { $_ -ne 'bookmarks-menu-button' })
    }
    return Set-GhostPreference $Text 'browser.uiCustomization.state' $true ($layout | ConvertTo-Json -Compress -Depth 20)
}

function Get-GhostProfiles([string]$FirefoxRoot = (Join-Path $env:APPDATA 'Mozilla\Firefox')) {
    $ini = Join-Path $FirefoxRoot 'profiles.ini'
    if (-not (Test-GhostFile $ini)) { return @() }
    $sections = @{}; $section = ''
    foreach ($line in [IO.File]::ReadAllLines((Get-GhostIOPath $ini))) {
        if ($line -match '^\s*\[([^\]]+)\]\s*$') {
            $section = $Matches[1]; $sections[$section] = @{}
        } elseif ($section -and $line -match '^\s*([^=;#]+?)=(.*)$') {
            $sections[$section][$Matches[1].Trim()] = $Matches[2].Trim()
        }
    }
    $defaults = @()
    foreach ($key in $sections.Keys) {
        if ($key -like 'Install*' -and $sections[$key].ContainsKey('Default')) {
            $defaultPath = $sections[$key]['Default']
            if (-not [IO.Path]::IsPathRooted($defaultPath)) { $defaultPath = Join-Path $FirefoxRoot $defaultPath }
            $defaults += [IO.Path]::GetFullPath($defaultPath)
        }
    }
    $seen = @{}
    foreach ($key in ($sections.Keys | Sort-Object)) {
        if ($key -notmatch '^Profile\d+$') { continue }
        $data = $sections[$key]
        if (-not $data.ContainsKey('Path')) { continue }
        $path = $data['Path']
        if ($data['IsRelative'] -eq '1') { $path = Join-Path $FirefoxRoot $path }
        $path = [IO.Path]::GetFullPath($path)
        if ((Test-GhostFile (Join-Path $path 'prefs.js')) -and -not $seen.ContainsKey($path)) {
            $seen[$path] = $true
            [pscustomobject]@{ Name = $data['Name']; Path = $path; IsDefault = ($defaults -contains $path -or $data['Default'] -eq '1') }
        }
    }
}

function Assert-GhostProfile([string]$ProfilePath) {
    $path = (Resolve-Path -LiteralPath $ProfilePath -ErrorAction Stop).ProviderPath
    if (-not (Test-GhostFile (Join-Path $path 'prefs.js'))) {
        throw 'This is not an initialized Firefox profile. Open Firefox once, then try again.'
    }
    # Do not follow redirected installer paths when writing or removing managed files.
    $check = @($path, (Join-Path $path 'chrome'), (Join-Path $path 'chrome\ghost-ui'), (Join-Path $path 'chrome\ghost-ui-backups'))
    $check += $script:TrackedFiles | ForEach-Object { Join-Path $path $_ }
    foreach ($item in $check) {
        if ((Test-GhostPath $item) -and ([IO.File]::GetAttributes((Get-GhostIOPath $item)) -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Redirected installer path is not supported: $item"
        }
    }
    return $path
}

function Assert-GhostFirefoxClosed {
    if (Get-Process -Name firefox -ErrorAction SilentlyContinue) {
        throw 'Close all Firefox windows before installing or uninstalling. No Firefox process will be forcefully closed.'
    }
}

function New-GhostSnapshot([string]$Profile, [string]$Destination, [string[]]$Files) {
    New-GhostDirectory $Destination
    $result = @()
    foreach ($name in $Files) {
        $source = Join-Path $Profile $name
        $exists = Test-GhostFile $source
        if ($exists) {
            $target = Join-Path $Destination $name
            New-GhostDirectory ([IO.Path]::GetDirectoryName($target))
            Copy-GhostFile $source $target $false
        }
        $result += [pscustomobject]@{ name = $name; exists = [bool]$exists }
    }
    return $result
}

function Restore-GhostSnapshot([string]$Profile, [string]$Directory, $Records) {
    foreach ($record in $Records) {
        if ($record.name -notin ($script:TrackedFiles + @('prefs.js'))) { throw 'Invalid backup file name.' }
        $target = Join-Path $Profile $record.name
        if ($record.exists) {
            New-GhostDirectory ([IO.Path]::GetDirectoryName($target))
            Copy-GhostFile (Join-Path $Directory $record.name) $target $true
        } elseif (Test-GhostFile $target) {
            Remove-GhostFile $target
        }
    }
}

function Get-GhostState([string]$Profile) {
    $statePath = Join-Path $Profile 'chrome\ghost-ui\state.json'
    if (-not (Test-GhostFile $statePath)) { return $null }
    $state = Read-GhostText $statePath | ConvertFrom-Json
    if ($state.schema -ne 1 -or $state.backupName -notmatch '^\d{8}-\d{6}-[0-9a-f]{8}$') { throw 'Invalid Ghost UI installation state.' }
    foreach ($name in $script:PreferenceValues.Keys) {
        $entries = @($state.preferences | Where-Object { $_.name -eq $name })
        # Releases through 1.0.4 did not manage download-panel preferences.
        $legacyDownload = $state.version -match '^1\.0\.[0-4]$' -and $name -in @('browser.download.alwaysOpenPanel', 'browser.download.panel.shown')
        if ($entries.Count -ne 1 -and -not ($legacyDownload -and $entries.Count -eq 0)) { throw 'Incomplete preference backup.' }
    }
    return $state
}

function Assert-GhostUnmodified([string]$Profile, $State) {
    foreach ($name in ($script:TrackedFiles | Where-Object { $_ -ne 'chrome/ghost-ui/state.json' })) {
        if (-not $State.hashes.PSObject.Properties[$name]) { throw 'Incomplete managed-file state.' }
    }
    foreach ($entry in $State.hashes.PSObject.Properties) {
        if ($entry.Name -notin $script:TrackedFiles) { throw 'Invalid managed file name.' }
        if ((Get-GhostHash (Join-Path $Profile $entry.Name)) -ne $entry.Value) {
            throw "A managed file was edited: $($entry.Name). Save those edits before updating or uninstalling. The original backup is in chrome/ghost-ui-backups/$($State.backupName)."
        }
    }
}

function Get-GhostWrapper([string]$Kind, [bool]$ImportPrevious) {
    $text = "/* Managed by Firefox Ghost UI. Use Uninstall.cmd to restore original files. */`r`n"
    if ($ImportPrevious) { $text += "@import url(`"$Kind.ghost-ui-original.css`");`r`n" }
    return $text + "@import url(`"ghost-ui/$Kind.css`");`r`n"
}

function Install-GhostUI([string]$ProfilePath, [string]$ThemePath) {
    $profile = Assert-GhostProfile $ProfilePath
    foreach ($name in @('userChrome.css', 'bonjourr.css')) {
        if (-not (Test-GhostFile (Join-Path $ThemePath $name))) { throw "Missing theme file: $name" }
    }
    $state = Get-GhostState $profile
    if ($state) { Assert-GhostUnmodified $profile $state }
    elseif ((Test-GhostFile (Join-Path $profile 'chrome\userChrome.ghost-ui-original.css')) -or
            (Test-GhostFile (Join-Path $profile 'chrome\userContent.ghost-ui-original.css'))) {
        throw 'Reserved original-CSS files already exist. No files were changed.'
    }
    $backupRoot = Join-Path $profile 'chrome\ghost-ui-backups'
    $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $transaction = Join-Path $backupRoot ("transaction-" + $stamp)
    $rollback = @(New-GhostSnapshot $profile $transaction ($script:TrackedFiles + @('prefs.js')))
    try {
        if (-not $state) {
            $backup = Join-Path $backupRoot $stamp
            $originals = @(New-GhostSnapshot $profile $backup @('user.js', 'chrome/userChrome.css', 'chrome/userContent.css'))
            $text = Read-GhostText (Join-Path $profile 'prefs.js')
            $preferences = @($script:PreferenceValues.Keys | ForEach-Object { Get-GhostPreference $text $_ })
            $state = [pscustomobject]@{ schema = 1; version = $script:Version; backupName = $stamp; originals = $originals; preferences = $preferences; hashes = @{} }
            foreach ($kind in @('userChrome', 'userContent')) {
                $original = Join-Path $backup "chrome/$kind.css"
                if (Test-GhostFile $original) {
                    Copy-GhostFile $original (Join-Path $profile "chrome/$kind.ghost-ui-original.css") $false
                }
            }
        }
        # Capture newly managed preferences at upgrade time, preserving the
        # first CSS backup and all previously recorded preference originals.
        foreach ($name in $script:PreferenceValues.Keys) {
            if (@($state.preferences | Where-Object { $_.name -eq $name }).Count -eq 0) {
                $state.preferences = @($state.preferences) + @(Get-GhostPreference (Read-GhostText (Join-Path $profile 'prefs.js')) $name)
            }
        }
        if (-not $state.PSObject.Properties['bookmarkButton']) {
            $state | Add-Member -NotePropertyName bookmarkButton -NotePropertyValue (Add-GhostBookmarksButton $profile $state)
        }
        $backup = Join-Path $backupRoot $state.backupName
        $payload = Join-Path $profile 'chrome\ghost-ui'
        New-GhostDirectory $payload
        Copy-GhostFile (Join-Path $ThemePath 'userChrome.css') (Join-Path $payload 'userChrome.css') $true
        $uuidPref = Get-GhostPreference (Read-GhostText (Join-Path $profile 'prefs.js')) 'extensions.webextensions.uuids'
        $bonjourrUUID = $null
        if ($uuidPref.exists) {
            $map = $uuidPref.value | ConvertFrom-Json
            $prop = $map.PSObject.Properties['{4f391a9e-8717-4ba6-a5b1-488a34931fcb}']
            if ($prop -and $prop.Value -match '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') { $bonjourrUUID = $prop.Value }
        }
        $content = "/* Bonjourr is optional. Run Install.cmd again after adding it. */`r`n"
        if ($bonjourrUUID) {
            $content = "/* Ghost UI: scoped to this profile's Bonjourr extension only. */`r`n@-moz-document url-prefix(`"moz-extension://$bonjourrUUID/`") {`r`n" + (Read-GhostText (Join-Path $ThemePath 'bonjourr.css')) + "`r`n}`r`n"
        }
        Write-GhostText (Join-Path $payload 'userContent.css') $content
        foreach ($kind in @('userChrome', 'userContent')) {
            $original = Join-Path $backup "chrome/$kind.css"
            $import = Test-GhostFile $original
            # A manually installed Ghost UI is replaced, not loaded twice.
            if ($kind -eq 'userChrome' -and (Read-GhostText $original) -match '^/\* Ghost UI . Firefox') { $import = $false }
            Write-GhostText (Join-Path $profile "chrome/$kind.css") (Get-GhostWrapper $kind $import)
        }
        $userJS = Read-GhostText (Join-Path $backup 'user.js')
        $userJS += "`r`n// BEGIN FIREFOX GHOST UI`r`n"
        foreach ($name in $script:PreferenceValues.Keys) {
            $userJS += 'user_pref(' + ($name | ConvertTo-Json -Compress) + ', ' + ($script:PreferenceValues[$name] | ConvertTo-Json -Compress) + ");`r`n"
        }
        $userJS += "// END FIREFOX GHOST UI`r`n"
        Write-GhostText (Join-Path $profile 'user.js') $userJS
        $hashes = @{}
        foreach ($name in ($script:TrackedFiles | Where-Object { $_ -ne 'chrome/ghost-ui/state.json' })) {
            $hashes[$name] = Get-GhostHash (Join-Path $profile $name)
        }
        $state.hashes = $hashes
        $state.version = $script:Version
        Write-GhostText (Join-Path $payload 'state.json') ($state | ConvertTo-Json -Depth 8)
        return [pscustomobject]@{ Profile = $profile; Backup = $backup; BonjourrStyled = [bool]$bonjourrUUID }
    } catch {
        Restore-GhostSnapshot $profile $transaction $rollback
        throw
    }
}

function Uninstall-GhostUI([string]$ProfilePath) {
    $profile = Assert-GhostProfile $ProfilePath
    $state = Get-GhostState $profile
    if (-not $state) { throw 'Ghost UI was not installed by this installer in this profile.' }
    Assert-GhostUnmodified $profile $state
    $backupRoot = Join-Path $profile 'chrome\ghost-ui-backups'
    $backup = Join-Path $backupRoot $state.backupName
    foreach ($name in @('user.js','chrome/userChrome.css','chrome/userContent.css')) {
        $entry = @($state.originals | Where-Object { $_.name -eq $name })
        if ($entry.Count -ne 1 -or @($state.originals).Count -ne 3 -or ($entry[0].exists -and -not (Test-GhostFile (Join-Path $backup $name)))) { throw 'Original backup is incomplete; no files were changed.' }
    }
    $transaction = Join-Path $backupRoot ('uninstall-' + [Guid]::NewGuid().ToString('N'))
    $rollback = @(New-GhostSnapshot $profile $transaction ($script:TrackedFiles + @('prefs.js')))
    try {
        Restore-GhostSnapshot $profile $backup $state.originals
        $prefs = Read-GhostText (Join-Path $profile 'prefs.js')
        if ($state.PSObject.Properties['bookmarkButton']) { $prefs = Remove-GhostBookmarksButton $prefs $state.bookmarkButton }
        foreach ($name in $script:PreferenceValues.Keys) {
            $entries = @($state.preferences | Where-Object { $_.name -eq $name })
            if ($entries.Count -eq 0) { continue } # Uninstall an older release without touching its download settings.
            $entry = $entries[0]
            $pattern = '(?m)^\s*user_pref\("' + [regex]::Escape($name) + '",\s*.+\);[^\S\r\n]*\r?\n?'
            $prefs = [regex]::Replace($prefs, $pattern, '')
            if ($entry.exists) { $prefs += "`r`n" + 'user_pref(' + ($name | ConvertTo-Json -Compress) + ', ' + ($entry.value | ConvertTo-Json -Compress) + ");`r`n" }
        }
        Write-GhostText (Join-Path $profile 'prefs.js') $prefs
        foreach ($name in @('chrome/userChrome.ghost-ui-original.css','chrome/userContent.ghost-ui-original.css',
                            'chrome/ghost-ui/userChrome.css','chrome/ghost-ui/userContent.css','chrome/ghost-ui/state.json')) {
            $path = Join-Path $profile $name
            if (Test-GhostFile $path) { Remove-GhostFile $path }
        }
        return [pscustomobject]@{ Profile = $profile; Backup = $backup }
    } catch {
        Restore-GhostSnapshot $profile $transaction $rollback
        throw
    }
}

Export-ModuleMember -Function Get-GhostProfiles, Assert-GhostProfile, Assert-GhostFirefoxClosed, Install-GhostUI, Uninstall-GhostUI
