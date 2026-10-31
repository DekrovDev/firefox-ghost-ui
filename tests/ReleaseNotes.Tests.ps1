#requires -Version 5.1
# Offline fixtures only. Run inside the required isolated test environment.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('test-output\release-notes-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$inputPath = Join-Path $fixture 'CHANGELOG.md'
$outputPath = Join-Path $fixture 'notes.md'
$writer = Join-Path $root 'scripts\Write-ReleaseNotes.ps1'
function Write-Fixture([string]$Body) { [IO.File]::WriteAllText($inputPath, $Body.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false)) }
function Must-Reject([string]$Body, [string]$Reason) {
    Write-Fixture $Body
    $rejected = $false
    try { & $writer -Version '1.2.0' -ChangelogPath $inputPath -OutputPath $outputPath }
    catch { $rejected = $true }
    if (-not $rejected) { throw $Reason }
}
$valid = "# Changelog`n`n## Unreleased`n`n### Summary`n- Future work.`n`n## [1.2.0] - 2026-11-01`n`n### Summary`n- Fixed downloads.`n- Improved startup.`n`n### Details`nMore information.`n`n## [1.1.0] - 2026-10-01`n`n### Summary`n- Historical work.`n"
Write-Fixture $valid
& $writer -Version '1.2.0' -ChangelogPath $inputPath -OutputPath $outputPath
$notes = [IO.File]::ReadAllText($outputPath)
if (-not $notes.Contains('- Fixed downloads.') -or -not $notes.Contains('More information.') -or $notes.Contains('Historical work') -or $notes.Contains('Future work')) { throw 'Selected release notes included the wrong section.' }
Must-Reject ($valid.Replace('## [1.2.0]', '## [1.3.0]')) 'Publication accepted a missing version.'
Must-Reject ($valid.Replace('2026-11-01', '2026-99-99')) 'Publication accepted an invalid release date.'
Must-Reject ($valid.Replace('### Summary', '### Other')) 'Publication accepted missing summaries.'
Must-Reject ($valid.Replace('- Fixed downloads.', '- ' + ('x' * 101))) 'Publication accepted an oversized summary.'
Must-Reject ($valid.Replace('- Fixed downloads.', '- <script>alert(1)</script>')) 'Publication accepted active markup.'
Must-Reject ($valid.Replace('- Fixed downloads.', '- [External link](https://example.com)')) 'Publication accepted a linked summary.'
Must-Reject ($valid.Replace('- Fixed downloads.', "- One`n- Two`n- Three")) 'Publication accepted more than three bullets.'
Must-Reject ($valid + "`n## [1.2.0] - 2026-11-02`n### Summary`n- Duplicate.`n") 'Publication accepted duplicate versions.'
Write-Fixture "## Unreleased`n`n### Summary`n- Preview only.`n"
& $writer -Version '1.2.0' -ChangelogPath $inputPath -AllowUnreleased -OutputPath $outputPath
if (-not [IO.File]::ReadAllText($outputPath).StartsWith('# Unreleased preview (not a published update)')) { throw 'Preview was not labeled as unreleased.' }
Write-Output 'PASS: release summary selection, limits, markup rejection, duplicates and preview labeling'
