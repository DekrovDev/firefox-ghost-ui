#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'scripts\GhostUI.psm1') -Force -DisableNameChecking
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('ghost-ui-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($sandbox) | Out-Null
$script:Passed = 0
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:Passed++
}
function Write-TestFile([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
function Read-TestFile([string]$Path) { return [IO.File]::ReadAllText($Path) }
function New-TestProfile([string]$Name) {
    $path = Join-Path $sandbox $Name
    Write-TestFile (Join-Path $path 'prefs.js') 'user_pref("unrelated.preference", "keep me");'
    return $path
}
function Expect-Failure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Assert $failed $Message
}
try {
    $theme = Join-Path $root 'theme'
    $fresh = New-TestProfile 'fresh profile with spaces'
    $r = Install-GhostUI $fresh $theme
    Assert (Test-Path -LiteralPath (Join-Path $fresh 'chrome\ghost-ui\state.json')) 'Fresh install state exists'
    Assert ((Read-TestFile (Join-Path $fresh 'user.js')) -match 'sidebar.verticalTabs.*, true') 'Native vertical tabs enabled'
    Assert (-not $r.BonjourrStyled) 'Missing Bonjourr does not block installation'
    Assert ((Read-TestFile (Join-Path $fresh 'chrome\userChrome.css')) -match '@import') 'CSS uses import wrapper'
    Assert ((Read-TestFile (Join-Path $fresh 'chrome\ghost-ui\userChrome.css')) -match '--ghost-open-wait: 120ms;') 'Release includes responsive hover delay'
    Assert ((Read-TestFile (Join-Path $fresh 'chrome\ghost-ui\userChrome.css')) -match '#urlbar\[usertyping\]\[focused\]') 'Search autohide fix included'
    Assert ((Read-TestFile (Join-Path $fresh 'chrome\ghost-ui\userChrome.css')) -match ':not\(\[role="tooltip"\]\)') 'Passive previews excluded from toolbar hold'
    # An older installation keeps its original backup while receiving the new
    # theme and current version metadata.
    $statePath = Join-Path $fresh 'chrome\ghost-ui\state.json'
    $oldState = Read-TestFile $statePath | ConvertFrom-Json
    $oldState.version = '1.0.0'
    Write-TestFile $statePath ($oldState | ConvertTo-Json -Depth 12)
    $again = Install-GhostUI $fresh $theme
    $updatedState = Read-TestFile $statePath | ConvertFrom-Json
    Assert ([version]$updatedState.version -gt [version]'1.0.0') 'Update refreshes release version metadata'
    Assert ($again.Backup -eq $r.Backup) 'Update preserves first backup'
    Assert (([regex]::Matches((Read-TestFile (Join-Path $fresh 'user.js')), 'BEGIN FIREFOX GHOST UI')).Count -eq 1) 'Reinstall is idempotent'
    # Emulate Firefox persisting the installer preferences, plus later unrelated changes.
    $prefs = (Read-TestFile (Join-Path $fresh 'prefs.js')) + "`n" + (Read-TestFile (Join-Path $fresh 'user.js'))
    $prefs += "`nuser_pref(`"later.preference`", 42);`n"
    Write-TestFile (Join-Path $fresh 'prefs.js') $prefs
    Uninstall-GhostUI $fresh | Out-Null
    Assert (-not (Test-Path -LiteralPath (Join-Path $fresh 'user.js'))) 'New user.js removed on uninstall'
    Assert (-not (Test-Path -LiteralPath (Join-Path $fresh 'chrome\userChrome.css'))) 'New CSS wrapper removed on uninstall'
    $restored = Read-TestFile (Join-Path $fresh 'prefs.js')
    Assert ($restored -notmatch 'user_pref\("sidebar.verticalTabs"') 'Originally absent preference removed'
    Assert ($restored -match 'later.preference.*42') 'Later unrelated preference preserved'
    Assert ($restored -match 'unrelated.preference.*keep me') 'Original unrelated preference preserved'
    Assert (Test-Path -LiteralPath $r.Backup) 'Backups retained after uninstall'

    $layoutProfile = New-TestProfile 'custom toolbar'
    $layout = '{"placements":{"nav-bar":["back-button","urlbar-container","downloads-button","my-extension"],"widget-overflow-fixed-list":["personal-button"]},"currentVersion":26,"customField":"keep"}'
    $layoutPref = 'user_pref("browser.uiCustomization.state", ' + ($layout | ConvertTo-Json -Compress) + ');'
    Write-TestFile (Join-Path $layoutProfile 'prefs.js') $layoutPref
    Install-GhostUI $layoutProfile $theme | Out-Null
    $buttonState = Read-TestFile (Join-Path $layoutProfile 'chrome/ghost-ui/state.json') | ConvertFrom-Json
    Assert $buttonState.bookmarkButton.added 'Bookmarks button added without replacing custom toolbar'
    $placed = $buttonState.bookmarkButton.applied | ConvertFrom-Json
    Assert (($placed.placements.'nav-bar' -join ',') -eq 'back-button,urlbar-container,bookmarks-menu-button,downloads-button,my-extension') 'Button inserted beside address field; existing order preserved'
    Assert ($placed.customField -eq 'keep' -and $placed.placements.'widget-overflow-fixed-list'[0] -eq 'personal-button') 'Other layout fields and areas preserved'
    # Simulate rearrangements after installation and ensure uninstall is surgical.
    $placed.placements.'nav-bar' = @('new-user-button') + $placed.placements.'nav-bar'
    $placed.customField = 'later edit'
    Write-TestFile (Join-Path $layoutProfile 'prefs.js') ('user_pref("browser.uiCustomization.state", ' + (($placed | ConvertTo-Json -Compress -Depth 10) | ConvertTo-Json -Compress) + ');')
    Install-GhostUI $layoutProfile $theme | Out-Null
    $updated = Read-TestFile (Join-Path $layoutProfile 'prefs.js')
    Assert (([regex]::Matches($updated,'bookmarks-menu-button')).Count -eq 1) 'Update does not duplicate bookmarks button'
    Uninstall-GhostUI $layoutProfile | Out-Null
    $removed = Read-TestFile (Join-Path $layoutProfile 'prefs.js')
    Assert ($removed -notmatch 'bookmarks-menu-button' -and $removed -match 'new-user-button' -and $removed -match 'later edit') 'Uninstall preserves later toolbar changes'
    foreach ($originalLayout in @($layout.Replace('downloads-button','bookmarks-menu-button'), $layout)) {
        $case = New-TestProfile ([Guid]::NewGuid().ToString('N'))
        $before = 'user_pref("browser.uiCustomization.state", ' + ($originalLayout | ConvertTo-Json -Compress) + ');'
        Write-TestFile (Join-Path $case 'prefs.js') $before
        Install-GhostUI $case $theme | Out-Null
        Uninstall-GhostUI $case | Out-Null
        Assert ((Read-TestFile (Join-Path $case 'prefs.js')).Contains($before)) 'Unchanged layout and preexisting bookmarks button restored exactly'
    }
    $pinnedLayout = New-TestProfile 'user.js layout'
    Write-TestFile (Join-Path $pinnedLayout 'user.js') $layoutPref
    Install-GhostUI $pinnedLayout $theme | Out-Null
    Assert ((Read-TestFile (Join-Path $pinnedLayout 'prefs.js')) -notmatch 'uiCustomization') 'Persistent user.js layout override respected'
    Uninstall-GhostUI $pinnedLayout | Out-Null

    $legacy = New-TestProfile 'update from 1.0.1'
    Install-GhostUI $legacy $theme | Out-Null
    $legacyStatePath = Join-Path $legacy 'chrome/ghost-ui/state.json'
    $legacyState = Read-TestFile $legacyStatePath | ConvertFrom-Json
    $legacyState.version = '1.0.1'
    $legacyState.PSObject.Properties.Remove('bookmarkButton')
    Write-TestFile $legacyStatePath ($legacyState | ConvertTo-Json -Depth 12)
    Write-TestFile (Join-Path $legacy 'prefs.js') 'user_pref("before.update", 1);'
    $legacyBackup = $legacyState.backupName
    Install-GhostUI $legacy $theme | Out-Null
    $legacyUpdated = Read-TestFile $legacyStatePath | ConvertFrom-Json
    Assert ($legacyUpdated.bookmarkButton.added -and $legacyUpdated.backupName -eq $legacyBackup) '1.0.1 migration adds menu while retaining first backup'
    Uninstall-GhostUI $legacy | Out-Null
    $legacyPrefs = Read-TestFile (Join-Path $legacy 'prefs.js')
    Assert ($legacyPrefs.Contains('user_pref("before.update", 1);') -and $legacyPrefs -notmatch 'uiCustomization') 'Legacy migration rolls back layout without losing preferences'

    $brokenLayout = New-TestProfile 'malformed toolbar'
    $brokenPrefs = 'user_pref("browser.uiCustomization.state", "invalid json");'
    Write-TestFile (Join-Path $brokenLayout 'prefs.js') $brokenPrefs
    Expect-Failure { Install-GhostUI $brokenLayout $theme } 'Malformed toolbar layout cannot overwrite preferences'
    Assert ((Read-TestFile (Join-Path $brokenLayout 'prefs.js')) -ceq $brokenPrefs -and -not (Test-Path (Join-Path $brokenLayout 'user.js'))) 'Failed install rolls back profile files and preferences'

    $longName = ('nested-profile-' * 10)
    $longProfile = New-TestProfile $longName
    $longCSS = '/* original style in a profile with a long backup path */'
    Write-TestFile (Join-Path $longProfile 'chrome/userChrome.css') $longCSS
    $longInstalled = Install-GhostUI $longProfile $theme
    Assert ((Join-Path $longInstalled.Backup 'chrome/userChrome.css').Length -gt 260) 'Regression fixture exercises a backup path longer than 260 characters'
    Install-GhostUI $longProfile $theme | Out-Null
    Uninstall-GhostUI $longProfile | Out-Null
    Assert ((Read-TestFile (Join-Path $longProfile 'chrome/userChrome.css')) -ceq $longCSS) 'Long-path install, update and uninstall preserve original CSS'

    $existing = New-TestProfile 'existing'
    $chrome = "/* personal CSS */`r`n@import url(`"extras.css`");`r`n:root { --personal: 1; }`r`n"
    $content = 'body { font-family: sans-serif; }'
    $user = "// Existing settings`r`nuser_pref(`"personal.setting`", true);`r`n"
    Write-TestFile (Join-Path $existing 'chrome\userChrome.css') $chrome
    Write-TestFile (Join-Path $existing 'chrome\userContent.css') $content
    Write-TestFile (Join-Path $existing 'user.js') $user
    $uuid = '{"{4f391a9e-8717-4ba6-a5b1-488a34931fcb}":"11111111-2222-3333-4444-555555555555","other-extension":"66666666-7777-8888-9999-000000000000"}'
    $prefText = 'user_pref("sidebar.visibility", "hide-sidebar");' + "`n" +
        'user_pref("sidebar.verticalTabs", false);' + "`n" +
        'user_pref("browser.tabs.inTitlebar", 0);' + "`n" +
        'user_pref("extensions.webextensions.uuids", ' + ($uuid | ConvertTo-Json -Compress) + ');'
    Write-TestFile (Join-Path $existing 'prefs.js') $prefText
    $r = Install-GhostUI $existing $theme
    Assert $r.BonjourrStyled 'Installed Bonjourr detected'
    $scoped = Read-TestFile (Join-Path $existing 'chrome\ghost-ui\userContent.css')
    Assert ($scoped -match '@-moz-document url-prefix\("moz-extension://11111111-2222-3333-4444-555555555555/') 'Bonjourr CSS scoped to exact extension'
    Assert ($scoped -notmatch '66666666-7777-8888-9999-000000000000') 'Other extensions are not styled'
    Assert ($scoped -notmatch '#sb_container.*display:\s*none') 'Central search is not removed'
    Assert ((Read-TestFile (Join-Path $existing 'chrome\userChrome.ghost-ui-original.css')) -ceq $chrome) 'Original CSS copy remains byte-for-byte equivalent'
    Assert ((Read-TestFile (Join-Path $existing 'chrome\userChrome.css')) -match 'userChrome.ghost-ui-original.css') 'Original CSS imported from same directory'
    Assert ((Read-TestFile (Join-Path $existing 'user.js')).StartsWith($user)) 'Original user.js remains at start'
    # Simulate a browser run; uninstall restores only managed preferences.
    Write-TestFile (Join-Path $existing 'prefs.js') ($prefText + "`n" + (Read-TestFile (Join-Path $existing 'user.js')) + "`nuser_pref(`"after.install`", `"keep`");`n")
    Uninstall-GhostUI $existing | Out-Null
    Assert ((Read-TestFile (Join-Path $existing 'chrome\userChrome.css')) -ceq $chrome) 'Original userChrome restored'
    Assert ((Read-TestFile (Join-Path $existing 'chrome\userContent.css')) -ceq $content) 'Original userContent restored'
    Assert ((Read-TestFile (Join-Path $existing 'user.js')) -ceq $user) 'Original user.js restored'
    $restored = Read-TestFile (Join-Path $existing 'prefs.js')
    Assert ($restored -match 'sidebar.visibility.*, "hide-sidebar"') 'Previous string preference restored'
    Assert ($restored -match 'sidebar.verticalTabs.*, false') 'Previous boolean preference restored'
    Assert ($restored -match 'browser.tabs.inTitlebar.*, 0') 'Previous integer preference restored'
    Assert ($restored -match 'after.install.*, "keep"') 'Unrelated edits retained'

    $legacy = New-TestProfile 'manual Ghost UI'
    $legacyCSS = Read-TestFile (Join-Path $theme 'userChrome.css')
    Write-TestFile (Join-Path $legacy 'chrome\userChrome.css') $legacyCSS
    Install-GhostUI $legacy $theme | Out-Null
    Assert ((Read-TestFile (Join-Path $legacy 'chrome\userChrome.css')) -notmatch 'userChrome.ghost-ui-original.css') 'Manual Ghost UI is not loaded twice'
    Uninstall-GhostUI $legacy | Out-Null
    Assert ((Read-TestFile (Join-Path $legacy 'chrome\userChrome.css')) -ceq $legacyCSS) 'Manual theme can be restored'

    $edited = New-TestProfile 'edited'
    Install-GhostUI $edited $theme | Out-Null
    $wrapper = Join-Path $edited 'chrome\userChrome.css'
    Write-TestFile $wrapper ((Read-TestFile $wrapper) + '/* new user edit */')
    $before = Read-TestFile $wrapper
    Expect-Failure { Install-GhostUI $edited $theme } 'Update refuses edited managed files'
    Expect-Failure { Uninstall-GhostUI $edited } 'Uninstall refuses edited managed files'
    Assert ((Read-TestFile $wrapper) -ceq $before) 'User edit survives refusal'

    $broken = New-TestProfile 'transaction'
    Write-TestFile (Join-Path $broken 'user.js') $user
    Write-TestFile (Join-Path $broken 'chrome\userChrome.css') $chrome
    Write-TestFile (Join-Path $broken 'prefs.js') 'user_pref("extensions.webextensions.uuids", "invalid JSON");'
    Expect-Failure { Install-GhostUI $broken $theme } 'Failed installation reports error'
    Assert ((Read-TestFile (Join-Path $broken 'user.js')) -ceq $user) 'Transaction restores user.js after failure'
    Assert ((Read-TestFile (Join-Path $broken 'chrome\userChrome.css')) -ceq $chrome) 'Transaction restores CSS after failure'
    Assert (-not (Test-Path -LiteralPath (Join-Path $broken 'chrome\ghost-ui\state.json'))) 'Failure leaves no installed state'

    $discovery = Join-Path $sandbox 'Firefox root'
    $p1 = Join-Path $discovery 'Profiles\one'
    $p2 = Join-Path $discovery 'Profiles\two'
    Write-TestFile (Join-Path $p1 'prefs.js') ''
    Write-TestFile (Join-Path $p2 'prefs.js') ''
    Write-TestFile (Join-Path $discovery 'profiles.ini') "[Profile0]`nName=one`nIsRelative=1`nPath=Profiles/one`n[Profile1]`nName=two`nIsRelative=1`nPath=Profiles/two`n[InstallABC]`nDefault=Profiles/two`n"
    $profiles = @(Get-GhostProfiles $discovery)
    Assert ($profiles.Count -eq 2) 'Discovers multiple Firefox profiles'
    Assert ((@($profiles | Where-Object { $_.IsDefault })[0].Path) -eq $p2) 'Uses current Firefox installation default'
    Expect-Failure { Install-GhostUI (Join-Path $sandbox 'not-a-profile') $theme } 'Invalid profile refused'

    Write-Host "PASS: $script:Passed installer assertions (PowerShell $($PSVersionTable.PSVersion))."
} finally {
    # The only recursive cleanup target is the unique test directory created above.
    $resolved = [IO.Path]::GetFullPath($sandbox)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved) -match '^ghost-ui-tests-[0-9a-f]{32}$') {
        [IO.Directory]::Delete(('\\?\' + $resolved), $true)
    } else { throw 'Unsafe test cleanup target refused.' }
}
