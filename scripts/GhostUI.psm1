Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$script:Version = '1.0.2'
$script:PreferenceValues = [ordered]@{
    'toolkit.legacyUserProfileCustomizations.stylesheets' = $true
    'sidebar.revamp' = $true
    'sidebar.verticalTabs' = $true
    'sidebar.visibility' = 'expand-on-hover'
    'browser.tabs.inTitlebar' = 1
}
$script:TrackedFiles = @('user.js', 'chrome/userChrome.css', 'chrome/userContent.css',
    'chrome/userChrome.ghost-ui-original.css', 'chrome/userContent.ghost-ui-original.css',
    'chrome/ghost-ui/userChrome.css', 'chrome/ghost-ui/userContent.css', 'chrome/ghost-ui/state.json')

function Write-GhostText([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Read-GhostText([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return [IO.File]::ReadAllText($Path) }
    return ''
}

function Get-GhostHash([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $stream = [IO.File]::OpenRead($Path)
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
    if (-not (Test-Path -LiteralPath $ini)) { return @() }
    $sections = @{}; $section = ''
    foreach ($line in [IO.File]::ReadAllLines($ini)) {
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
        if ((Test-Path -LiteralPath (Join-Path $path 'prefs.js')) -and -not $seen.ContainsKey($path)) {
            $seen[$path] = $true
            [pscustomobject]@{ Name = $data['Name']; Path = $path; IsDefault = ($defaults -contains $path -or $data['Default'] -eq '1') }
        }
    }
}

function Assert-GhostProfile([string]$ProfilePath) {
    $path = (Resolve-Path -LiteralPath $ProfilePath -ErrorAction Stop).ProviderPath
    if (-not (Test-Path -LiteralPath (Join-Path $path 'prefs.js') -PathType Leaf)) {
        throw 'This is not an initialized Firefox profile. Open Firefox once, then try again.'
    }
    # Do not follow redirected installer paths when writing or removing managed files.
    $check = @($path, (Join-Path $path 'chrome'), (Join-Path $path 'chrome\ghost-ui'), (Join-Path $path 'chrome\ghost-ui-backups'))
    $check += $script:TrackedFiles | ForEach-Object { Join-Path $path $_ }
    foreach ($item in $check) {
        if ((Test-Path -LiteralPath $item) -and ((Get-Item -LiteralPath $item -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
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
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    $result = @()
    foreach ($name in $Files) {
        $source = Join-Path $Profile $name
        $exists = Test-Path -LiteralPath $source -PathType Leaf
        if ($exists) {
            $target = Join-Path $Destination $name
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            Copy-Item -LiteralPath $source -Destination $target
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
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            Copy-Item -LiteralPath (Join-Path $Directory $record.name) -Destination $target -Force
        } elseif (Test-Path -LiteralPath $target -PathType Leaf) {
            Remove-Item -LiteralPath $target -Force
        }
    }
}

function Get-GhostState([string]$Profile) {
    $statePath = Join-Path $Profile 'chrome\ghost-ui\state.json'
    if (-not (Test-Path -LiteralPath $statePath)) { return $null }
    $state = Read-GhostText $statePath | ConvertFrom-Json
    if ($state.schema -ne 1 -or $state.backupName -notmatch '^\d{8}-\d{6}-[0-9a-f]{8}$') { throw 'Invalid Ghost UI installation state.' }
    foreach ($name in $script:PreferenceValues.Keys) {
        if (@($state.preferences | Where-Object { $_.name -eq $name }).Count -ne 1) { throw 'Incomplete preference backup.' }
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
        if (-not (Test-Path -LiteralPath (Join-Path $ThemePath $name))) { throw "Missing theme file: $name" }
    }
    $state = Get-GhostState $profile
    if ($state) { Assert-GhostUnmodified $profile $state }
    elseif ((Test-Path -LiteralPath (Join-Path $profile 'chrome\userChrome.ghost-ui-original.css')) -or
            (Test-Path -LiteralPath (Join-Path $profile 'chrome\userContent.ghost-ui-original.css'))) {
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
                if (Test-Path -LiteralPath $original) {
                    Copy-Item -LiteralPath $original -Destination (Join-Path $profile "chrome/$kind.ghost-ui-original.css")
                }
            }
        }
        if (-not $state.PSObject.Properties['bookmarkButton']) {
            $state | Add-Member -NotePropertyName bookmarkButton -NotePropertyValue (Add-GhostBookmarksButton $profile $state)
        }
        $backup = Join-Path $backupRoot $state.backupName
        $payload = Join-Path $profile 'chrome\ghost-ui'
        [IO.Directory]::CreateDirectory($payload) | Out-Null
        Copy-Item -LiteralPath (Join-Path $ThemePath 'userChrome.css') -Destination (Join-Path $payload 'userChrome.css') -Force
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
            $import = Test-Path -LiteralPath $original
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
        if ($entry.Count -ne 1 -or @($state.originals).Count -ne 3 -or ($entry[0].exists -and -not (Test-Path -LiteralPath (Join-Path $backup $name)))) { throw 'Original backup is incomplete; no files were changed.' }
    }
    $transaction = Join-Path $backupRoot ('uninstall-' + [Guid]::NewGuid().ToString('N'))
    $rollback = @(New-GhostSnapshot $profile $transaction ($script:TrackedFiles + @('prefs.js')))
    try {
        Restore-GhostSnapshot $profile $backup $state.originals
        $prefs = Read-GhostText (Join-Path $profile 'prefs.js')
        if ($state.PSObject.Properties['bookmarkButton']) { $prefs = Remove-GhostBookmarksButton $prefs $state.bookmarkButton }
        foreach ($name in $script:PreferenceValues.Keys) {
            $entry = @($state.preferences | Where-Object { $_.name -eq $name })[0]
            $pattern = '(?m)^\s*user_pref\("' + [regex]::Escape($name) + '",\s*.+\);[^\S\r\n]*\r?\n?'
            $prefs = [regex]::Replace($prefs, $pattern, '')
            if ($entry.exists) { $prefs += "`r`n" + 'user_pref(' + ($name | ConvertTo-Json -Compress) + ', ' + ($entry.value | ConvertTo-Json -Compress) + ");`r`n" }
        }
        Write-GhostText (Join-Path $profile 'prefs.js') $prefs
        foreach ($name in @('chrome/userChrome.ghost-ui-original.css','chrome/userContent.ghost-ui-original.css',
                            'chrome/ghost-ui/userChrome.css','chrome/ghost-ui/userContent.css','chrome/ghost-ui/state.json')) {
            $path = Join-Path $profile $name
            if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
        }
        return [pscustomobject]@{ Profile = $profile; Backup = $backup }
    } catch {
        Restore-GhostSnapshot $profile $transaction $rollback
        throw
    }
}

Export-ModuleMember -Function Get-GhostProfiles, Assert-GhostProfile, Assert-GhostFirefoxClosed, Install-GhostUI, Uninstall-GhostUI
