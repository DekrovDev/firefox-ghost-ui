#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'test-output\handles'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework C# compiler was not found.' }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$output = Join-Path $OutputDirectory 'GhostHandles.exe'
& $compiler /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu "/out:$output" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:Accessibility.dll "/win32manifest:$root\companion\GhostHandles.manifest" "$root\companion\GhostHandles.cs" "$root\companion\GhostUpdates.cs"
if ($LASTEXITCODE -ne 0) { throw 'Ghost UI handles compilation failed.' }
Copy-Item -LiteralPath (Join-Path $root 'companion\GhostHandles.exe.config') -Destination ($output + '.config') -Force
Write-Output $output
