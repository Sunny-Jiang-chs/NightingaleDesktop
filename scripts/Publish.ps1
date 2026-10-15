[CmdletBinding()]
param(
    [switch]$Installer,
    [string]$InnoCompiler = 'D:\tools\inno-setup\compiler\ISCC.exe'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src\Nightingale.Desktop\Nightingale.Desktop.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Unsupported package version: $version" }
$publishDirectory = Join-Path $projectRoot "dist\Nightingale-$version-win-x64"
$dotnet = if (Test-Path -LiteralPath 'D:\tools\dotnet\dotnet.exe') {
    'D:\tools\dotnet\dotnet.exe'
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'native\mpv-2.dll'))) {
    throw 'Missing native\mpv-2.dll. Run scripts\Get-Mpv.ps1 first.'
}

# Re-publish into the same version directory without deleting user files. Only
# compiler output and the explicitly listed documentation are copied here.
& $dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
    --output $publishDirectory --nologo -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'mpv-2.dll'))) {
    throw 'Publish is incomplete: mpv-2.dll is missing.'
}

foreach ($name in @('README.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE')) {
    $source = Join-Path $projectRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $publishDirectory -Force }
}
$noticesSource = Join-Path $projectRoot 'ThirdPartyNotices'
if (Test-Path -LiteralPath $noticesSource) {
    Copy-Item -LiteralPath $noticesSource -Destination $publishDirectory -Recurse -Force
}
$nativeNotices = Join-Path $publishDirectory 'ThirdPartyNotices\mpv'
New-Item -ItemType Directory -Path $nativeNotices -Force | Out-Null
foreach ($name in @('THIRD_PARTY.md', 'LICENSE-mpv-GPL.txt', 'LICENSE-mpv-LGPL.txt', 'Copyright-mpv.txt', 'README.md')) {
    $source = Join-Path $projectRoot "native\$name"
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing third-party notice: $source" }
    Copy-Item -LiteralPath $source -Destination $nativeNotices -Force
}

Write-Host "Portable application: $publishDirectory"
if ($Installer) {
    if (-not (Test-Path -LiteralPath $InnoCompiler)) {
        $innoCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($null -eq $innoCommand) { throw "Inno Setup compiler not found at $InnoCompiler." }
        $InnoCompiler = $innoCommand.Source
    }
    & $InnoCompiler "/DAppVersion=$version" "/DPublishDir=$publishDirectory" (Join-Path $projectRoot 'installer\Nightingale.iss')
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed with exit code $LASTEXITCODE." }
    Write-Host "Installer: $(Join-Path $projectRoot "dist\Nightingale-$version-setup.exe")"
}
