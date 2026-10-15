[CmdletBinding()]
param(
    [string]$ToolsRoot = 'D:\tools',
    [string]$Destination = (Join-Path $PSScriptRoot '..\native'),
    [switch]$SkipDownload
)
$ErrorActionPreference = 'Stop'

# Pin both the upstream build and GitHub's release-asset SHA-256 digest.
$version = '20261013'
$archiveName = 'mpv-dev-x86_64-20261013-git-9d144e57b6.7z'
$expectedSha256 = '10860837bc04491862c1e66a95dc1af1d7886d69757ce447a09e1e49ae993b52'
$url = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$version/$archiveName"
$cache = Join-Path $ToolsRoot "mpv\$version"
$archive = Join-Path $cache $archiveName
$extracted = Join-Path $cache 'extracted'
New-Item -ItemType Directory -Force -Path $cache, $extracted, $Destination | Out-Null

if (-not (Test-Path -LiteralPath $archive)) {
    if ($SkipDownload) { throw "Missing cached libmpv archive: $archive" }
    Write-Host "Downloading pinned libmpv build $version..."
    Invoke-WebRequest -Uri $url -OutFile $archive
}
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
if ($actual -ne $expectedSha256) {
    throw "libmpv SHA-256 mismatch. Expected $expectedSha256; got $actual. The existing cache was retained for inspection."
}

# The libarchive tar supplied by Windows 10/11 reads .7z archives directly.
$tar = Get-Command tar.exe -ErrorAction Stop
& $tar.Source -xf $archive -C $extracted
if ($LASTEXITCODE -ne 0) { throw "tar failed to extract libmpv (exit $LASTEXITCODE)." }
$library = Join-Path $extracted 'libmpv-2.dll'
if (-not (Test-Path -LiteralPath $library)) { throw 'The verified archive does not contain libmpv-2.dll.' }
Copy-Item -LiteralPath $library -Destination (Join-Path $Destination 'mpv-2.dll') -Force
Write-Host "libmpv is ready at $(Join-Path $Destination 'mpv-2.dll') (SHA-256 verified)."
