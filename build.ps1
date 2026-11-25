param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319'
}
$metadata = Join-Path $env:WINDIR 'System32/WinMetadata'
$runtime = Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET/assembly/GAC_MSIL/System.Runtime') `
    -Filter System.Runtime.dll -Recurse | Select-Object -First 1
$source = Join-Path $PSScriptRoot 'src'
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($outputPath) | Out-Null
$output = Join-Path $outputPath 'Batery.exe'
$arguments = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
    "/out:$output",
    "/win32manifest:$(Join-Path $source 'Batery.manifest')",
    "/win32icon:$(Join-Path $source 'Batery.ico')",
    "/reference:$($runtime.FullName)",
    "/reference:$(Join-Path $metadata 'Windows.Devices.winmd')",
    "/reference:$(Join-Path $metadata 'Windows.Foundation.winmd')",
    "/reference:$(Join-Path $metadata 'Windows.Storage.winmd')",
    (Join-Path $source 'Program.cs'),
    (Join-Path $source 'Devices.cs'),
    (Join-Path $source 'BatteryWindow.cs'),
    (Join-Path $source 'UsbReceivers.cs'),
    (Join-Path $source 'UsbHid.cs')
)
& (Join-Path $framework 'csc.exe') @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Falha ao compilar Batery.exe (código $LASTEXITCODE)."
}
Get-Item -LiteralPath $output
