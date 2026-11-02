#requires -Version 5.1
# Build an unshipped actor against the real controller, then test a fresh Firefox.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Use a disposable GitHub-hosted Windows runner.' }
# Newer runner Firefox builds gate Marionette system access behind this variable;
# the launcher JobProcess inherits the process environment, like production.
$env:MOZ_REMOTE_ALLOW_SYSTEM_ACCESS = '1'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('test-output\browser-' + [Guid]::NewGuid().ToString('N'))
$actorFolder = Join-Path $fixture 'handles\actor'
[IO.Directory]::CreateDirectory($actorFolder) | Out-Null
$updateSource = [IO.File]::ReadAllText((Join-Path $root 'companion\GhostUpdates.cs'))
$defaultRoot = 'Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FirefoxGhostUI", "updates")'
if (-not $updateSource.Contains($defaultRoot)) { throw 'The exact update root expression could not be redirected for the fixture.' }
$updateSource = $updateSource.Replace($defaultRoot, ((Join-Path $fixture 'updates') | ConvertTo-Json -Compress))
# Keep production's recoverable UI guard, but retain its exact exception/stage
# in this disposable copy so a hidden card cannot mask the original failure.
$tickStart = $updateSource.IndexOf('        private void Tick() {', [StringComparison]::Ordinal)
$tickEnd = $updateSource.IndexOf('        private static bool Fullscreen(', $tickStart, [StringComparison]::Ordinal)
if ($tickStart -lt 0 -or $tickEnd -le $tickStart) { throw 'Could not locate the exact native UI tick for fixture diagnostics.' }
$tick = $updateSource.Substring($tickStart, $tickEnd - $tickStart)
$tick = $tick.Replace('DateTime now = DateTime.UtcNow;', 'DateTime now = DateTime.UtcNow; string fixtureStage = "start";')
foreach ($marker in @(
    @('IntPtr foreground = Native.GetForegroundWindow();', 'foreground'),
    @('uint pid; Native.GetWindowThreadProcessId(firefox, out pid);', 'session'),
    @('card.SetCompact(compact);', 'compact'),
    @('if (!card.FitToWindow(bounds))', 'fit'),
    @('card.Location = new Point(', 'position'),
    @('if (!card.Visible || owner != firefox)', 'show'),
    @('card.SetShortcutEnabled(true);', 'shortcut')
)) { $tick = $tick.Replace($marker[0], ('fixtureStage = "' + $marker[1] + '"; ' + $marker[0])) }
$guard = '} catch { card.Hide(); card.SetShortcutEnabled(false); }'
if (-not $tick.Contains($guard)) { throw 'Native UI error guard changed; fixture diagnostics need review.' }
$errorPath = (Join-Path $fixture 'ui-error.txt') | ConvertTo-Json -Compress
$diagnosticGuard = '} catch (Exception fixtureError) { try { File.WriteAllText(' + $errorPath + ', "stage=" + fixtureStage + ", disposed=" + card.IsDisposed + ", disposing=" + card.Disposing + ", handleCreated=" + card.IsHandleCreated + ", ownerValid=" + Native.IsWindow(owner) + ", foreground=" + Native.GetForegroundWindow() + "\r\n" + fixtureError); } catch { } card.Hide(); card.SetShortcutEnabled(false); }'
$tick = $tick.Replace($guard, $diagnosticGuard)
$updateSource = $updateSource.Substring(0, $tickStart) + $tick + $updateSource.Substring($tickEnd)
$sourcePath = Join-Path $fixture 'GhostUpdates.cs'
[IO.File]::WriteAllText($sourcePath, $updateSource, [Text.UTF8Encoding]::new($false))
$actor = Join-Path $actorFolder 'GhostHandles.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /codepage:65001 /target:winexe /main:GhostUI.UpdateBrowserActor /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:Accessibility.dll ("/win32manifest:" + (Join-Path $root 'companion\GhostHandles.manifest')) ("/out:" + $actor) (Join-Path $root 'companion\GhostHandles.cs') $sourcePath (Join-Path $root 'tests\UpdateBrowserActor.cs')
if ($LASTEXITCODE -ne 0) { throw 'Browser actor compilation failed.' }
Copy-Item -LiteralPath (Join-Path $root 'companion\GhostHandles.exe.config') -Destination ($actor + '.config')
python (Join-Path $root 'tests\UpdateBrowser.Tests.py') --actor $actor --root $fixture
$updateExitCode = $LASTEXITCODE
Write-Output 'Running the complete toolbar regression harness in another disposable headless Firefox profile.'
python (Join-Path $root 'tests/Toolbar.Tests.py')
$toolbarExitCode = $LASTEXITCODE
if ($updateExitCode -ne 0 -or $toolbarExitCode -ne 0) {
    throw "Firefox regression failed: update-card exit=$updateExitCode; toolbar exit=$toolbarExitCode."
}
