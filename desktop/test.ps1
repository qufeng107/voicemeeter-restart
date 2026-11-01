param([switch]$RealRestart)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $projectRoot 'test-artifacts\integration'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$testExe = Join-Path $testRoot 'IntegrationTests.exe'
$references = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot $_) }
& (Join-Path $frameworkRoot 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /utf8output "/out:$testExe" @references (Join-Path $PSScriptRoot 'Core.cs') (Join-Path $PSScriptRoot 'PipeProtocol.cs') (Join-Path $PSScriptRoot 'tests\IntegrationTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Integration test compilation failed' }
$appExe = Join-Path $projectRoot 'dist\VoicemeeterReconnect\VoicemeeterReconnect.exe'
if ($RealRestart) { & $testExe $appExe $testRoot --real-restart } else { & $testExe $appExe $testRoot }
if ($LASTEXITCODE -ne 0) { throw 'Integration checks failed; see test-artifacts\integration\integration-report.txt' }
