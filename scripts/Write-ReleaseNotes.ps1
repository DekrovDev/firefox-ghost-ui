#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Version,
    [string]$ChangelogPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'CHANGELOG.md'),
    [string]$OutputPath,
    [switch]$AllowUnreleased
)
$ErrorActionPreference = 'Stop'
if (-not $PSBoundParameters.ContainsKey('Version')) {
    $Version = [IO.File]::ReadAllText((Join-Path (Split-Path $PSScriptRoot -Parent) 'VERSION')).Trim()
}
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Invalid release version.' }
$text = [IO.File]::ReadAllText($ChangelogPath).Replace("`r`n", "`n")
if ($text -match '[\u0400-\u052f]') { throw 'Release notes must be in English.' }
$sections = @([regex]::Matches($text, '(?m)^## (.+)\n'))
$matches = @($sections | Where-Object { $_.Groups[1].Value -cmatch ('^\[' + [regex]::Escape($Version) + '\] - \d{4}-\d{2}-\d{2}$') })
if ($matches.Count -gt 1) { throw 'Duplicate release section.' }
$unreleased = $false
if ($matches.Count -eq 0 -and $AllowUnreleased) {
    $matches = @($sections | Where-Object { $_.Groups[1].Value -ceq 'Unreleased' })
    $unreleased = $true
}
if ($matches.Count -ne 1) { throw "CHANGELOG.md needs exactly one versioned summary for $Version." }
$section = $matches[0]
if (-not $unreleased) {
    $dateText = $section.Groups[1].Value.Substring($section.Groups[1].Value.Length - 10)
    $dateValue = [DateTime]::MinValue
    if (-not [DateTime]::TryParseExact($dateText, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$dateValue)) { throw 'Invalid release date.' }
}
$end = $text.Length
foreach ($next in $sections) { if ($next.Index -gt $section.Index) { $end = $next.Index; break } }
$body = $text.Substring($section.Index + $section.Length, $end - $section.Index - $section.Length).Trim()
$summary = [regex]::Matches($body, '(?ms)^### Summary\n(.*?)(?=^### |\z)')
if ($summary.Count -ne 1) { throw 'Release section needs exactly one Summary heading.' }
$lines = @($summary[0].Groups[1].Value.Trim().Split("`n") | Where-Object { $_.Trim() })
if ($lines.Count -lt 1 -or $lines.Count -gt 3) { throw 'Summary needs 1-3 bullet points.' }
foreach ($line in $lines) {
    if ($line -cnotmatch '^- [^\r\n]{1,100}$' -or $line.Substring(2).Trim().Length -eq 0 -or $line.Substring(2) -match '[\p{Cc}\p{Cf}\p{Zl}\p{Zp}<>\[\]`*]') {
        throw 'Summary bullets must be plain text, at most 100 characters each.'
    }
}
$heading = if ($unreleased) { '# Unreleased preview (not a published update)' } else { "# Firefox Ghost UI $Version" }
$notes = $heading + "`n`n" + $body + "`n"
if ($OutputPath) {
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $notes, [Text.UTF8Encoding]::new($false))
} else { Write-Output $notes }
