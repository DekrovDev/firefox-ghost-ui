#requires -Version 5.1
# Build an unshipped actor against the real controller, then test a fresh Firefox.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Use a disposable GitHub-hosted Windows runner.' }
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('test-output\browser-' + [Guid]::NewGuid().ToString('N'))
$actorFolder = Join-Path $fixture 'handles\actor'
[IO.Directory]::CreateDirectory($actorFolder) | Out-Null
$updateSource = [IO.File]::ReadAllText((Join-Path $root 'companion\GhostUpdates.cs'))
$defaultRoot = 'Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FirefoxGhostUI", "updates")'
if (-not $updateSource.Contains($defaultRoot)) { throw 'The exact update root expression could not be redirected for the fixture.' }
$updateSource = $updateSource.Replace($defaultRoot, ((Join-Path $fixture 'updates') | ConvertTo-Json -Compress))
$sourcePath = Join-Path $fixture 'GhostUpdates.cs'
[IO.File]::WriteAllText($sourcePath, $updateSource, [Text.UTF8Encoding]::new($false))
$actor = Join-Path $actorFolder 'GhostHandles.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /codepage:65001 /target:winexe /main:GhostUI.UpdateBrowserActor /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:Accessibility.dll ("/win32manifest:" + (Join-Path $root 'companion\GhostHandles.manifest')) ("/out:" + $actor) (Join-Path $root 'companion\GhostHandles.cs') $sourcePath (Join-Path $root 'tests\UpdateBrowserActor.cs')
if ($LASTEXITCODE -ne 0) { throw 'Browser actor compilation failed.' }
Copy-Item -LiteralPath (Join-Path $root 'companion\GhostHandles.exe.config') -Destination ($actor + '.config')
python (Join-Path $root 'tests\UpdateBrowser.Tests.py') --actor $actor --root $fixture
if ($LASTEXITCODE -ne 0) { throw 'Real Firefox update-card fixture failed.' }
