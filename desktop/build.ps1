param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
$outputRoot = Join-Path (Split-Path $sourceRoot -Parent) 'dist\VoicemeeterReconnect'
if ($OutputDirectory) { $outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory) }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpfRoot = Join-Path $frameworkRoot 'WPF'
$iconPath = Join-Path $sourceRoot 'app.ico'
if (-not (Test-Path -LiteralPath $iconPath)) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = New-Object System.Drawing.Bitmap 64,64
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::FromArgb(82,117,222))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White),5
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $points = @([System.Drawing.Point]::new(12,33),[System.Drawing.Point]::new(22,33),[System.Drawing.Point]::new(28,16),[System.Drawing.Point]::new(36,48),[System.Drawing.Point]::new(43,30),[System.Drawing.Point]::new(53,30))
    $graphics.DrawLines($pen,$points)
    $icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
    $stream = [System.IO.File]::Create($iconPath)
    try { $icon.Save($stream) } finally { $stream.Dispose(); $icon.Dispose(); $pen.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
$references = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll') | ForEach-Object {
    $referencePath = Join-Path $wpfRoot $_
    if (-not (Test-Path -LiteralPath $referencePath)) { $referencePath = Join-Path $frameworkRoot $_ }
    '/reference:' + $referencePath
}
$exePath = Join-Path $outputRoot 'VoicemeeterReconnect.exe'
$sources = @('AssemblyInfo.cs','Core.cs','PipeProtocol.cs','DesktopConnection.cs','Program.cs','NativeTests.cs') | ForEach-Object { Join-Path $sourceRoot $_ }
& (Join-Path $frameworkRoot 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /utf8output /main:Reconnect.Bootstrap "/out:$exePath" "/win32manifest:$(Join-Path $sourceRoot 'app.manifest')" "/win32icon:$iconPath" "/resource:$(Join-Path $sourceRoot 'MainWindow.xaml'),MainWindow.xaml" "/resource:$iconPath,app.ico" @references @sources
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
$workerPath = Join-Path $outputRoot 'VoicemeeterReconnect.Background.exe'
$workerSources = @('AssemblyInfo.cs','Core.cs','PipeProtocol.cs','Background.cs') | ForEach-Object { Join-Path $sourceRoot $_ }
$workerReferences = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Drawing.dll','System.Windows.Forms.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot $_) }
& (Join-Path $frameworkRoot 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /utf8output /main:Reconnect.BackgroundContext "/out:$workerPath" "/win32manifest:$(Join-Path $sourceRoot 'app.manifest')" "/win32icon:$iconPath" "/resource:$iconPath,app.ico" @workerReferences @workerSources
if ($LASTEXITCODE -ne 0) { throw 'Background compilation failed.' }
Copy-Item -LiteralPath (Join-Path $sourceRoot 'VoicemeeterReconnect.exe.config') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'VoicemeeterReconnect.exe.config') -Destination ($workerPath + '.config') -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'README.md') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path (Split-Path $sourceRoot -Parent) 'LICENSE') -Destination $outputRoot -Force
$reportPath = Join-Path (Split-Path $sourceRoot -Parent) 'TEST_REPORT-2026-11-01.md'
if (Test-Path -LiteralPath $reportPath) { Copy-Item -LiteralPath $reportPath -Destination $outputRoot -Force }
Get-Item -LiteralPath $exePath | Select-Object FullName,Length
