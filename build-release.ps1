<#
.SYNOPSIS
  Builds File Tunnel for every released runtime, renames each single-file binary to
  ft-<rid>[.exe], and collects them in one folder - automating the manual release process.

.DESCRIPTION
  Uses the existing publish profiles in ft/Properties/PublishProfiles (one per RID), so the
  output matches exactly what you ship (Release, self-contained, single-file, trimmed).

.EXAMPLE
  .\build-release.ps1
  .\build-release.ps1 -OutDir C:\ft-release -Sha256
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'release'),
    [switch]$Sha256           # also write SHA256SUMS.txt
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'ft\ft.csproj'

# RIDs shipped on the GitHub releases. Each has a publish profile of the same name.
$rids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm', 'linux-arm64', 'osx-x64', 'osx-arm64')

# Fresh output folder
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null

foreach ($rid in $rids) {
    $isWin   = $rid -like 'win-*'
    $srcName = if ($isWin) { 'ft.exe' } else { 'ft' }
    $dstName = if ($isWin) { "ft-$rid.exe" } else { "ft-$rid" }

    Write-Host "Publishing $rid ..." -ForegroundColor Cyan
    dotnet publish $proj -p:PublishProfile=$rid --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

    $src = Join-Path $PSScriptRoot "ft\bin\Release\net10.0\publish\$rid\$srcName"
    if (-not (Test-Path $src)) { throw "Expected output not found: $src" }

    Copy-Item $src (Join-Path $OutDir $dstName) -Force
    Write-Host "  -> $dstName" -ForegroundColor Green
}

if ($Sha256) {
    Get-ChildItem $OutDir -File | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
    } | Set-Content (Join-Path $OutDir 'SHA256SUMS.txt') -Encoding utf8
}

Write-Host "`nDone. $($rids.Count) binaries collected in $OutDir" -ForegroundColor Green
Get-ChildItem $OutDir | Select-Object Name, Length | Format-Table -AutoSize
