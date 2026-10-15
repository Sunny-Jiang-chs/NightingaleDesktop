[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = if (Test-Path -LiteralPath 'D:\tools\dotnet\dotnet.exe') {
    'D:\tools\dotnet\dotnet.exe'
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$project = Join-Path $projectRoot 'src\Nightingale.Desktop\Nightingale.Desktop.csproj'

if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'native\mpv-2.dll'))) {
    throw 'Missing native\mpv-2.dll. Run scripts\Get-Mpv.ps1 first.'
}

& $dotnet build $project --configuration $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

if ($Run) {
    $executable = Join-Path $projectRoot "tmp\build\Nightingale.Desktop\bin\$Configuration\net10.0-windows\Nightingale.exe"
    Start-Process -FilePath $executable -WorkingDirectory $projectRoot | Out-Null
}
