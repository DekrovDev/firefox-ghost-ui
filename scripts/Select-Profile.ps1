function Select-GhostProfile([string]$RequestedPath, [switch]$NoPrompt) {
    if ($RequestedPath) { return Assert-GhostProfile $RequestedPath }
    $profiles = @(Get-GhostProfiles)
    if ($profiles.Count -eq 0) { throw 'No Firefox profiles found. Open desktop Firefox once, or supply -ProfilePath.' }
    if ($profiles.Count -eq 1) { return $profiles[0].Path }
    $defaults = @($profiles | Where-Object { $_.IsDefault })
    if ($NoPrompt) {
        if ($defaults.Count -eq 1) { return $defaults[0].Path }
        throw 'Multiple Firefox profiles found. Supply -ProfilePath explicitly.'
    }
    Write-Host 'Choose the Firefox profile to change:'
    for ($i = 0; $i -lt $profiles.Count; $i++) {
        $label = ''; if ($profiles[$i].IsDefault) { $label = ' (default)' }
        Write-Host ("{0}. {1}{2}`n   {3}" -f ($i+1), $profiles[$i].Name, $label, $profiles[$i].Path)
    }
    $choice = Read-Host 'Profile number'
    $number = 0
    if (-not [int]::TryParse($choice, [ref]$number) -or $number -lt 1 -or $number -gt $profiles.Count) { throw 'Invalid profile selection. No files were changed.' }
    return $profiles[$number-1].Path
}

function Wait-GhostFirefoxClosed([switch]$NoPrompt) {
    if (-not (Get-Process -Name firefox -ErrorAction SilentlyContinue)) { return }
    if ($NoPrompt) { Assert-GhostFirefoxClosed }
    Write-Host 'Close all Firefox windows normally, then press Enter. Your browser will not be forcefully closed.'
    Read-Host | Out-Null
    Assert-GhostFirefoxClosed
}
