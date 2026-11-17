#requires -Version 5.1
<#
.SYNOPSIS
    Build UbootLib (and the sample mod) and produce a release archive.

.DESCRIPTION
    Builds both projects, stages a BepInEx-shaped layout and zips it into release/.
    The staged layout can be copied straight into the game folder.

.PARAMETER GameDir
    Wolfpack install directory. Falls back to $env:WOLFPACK_DIR, then the default Steam path.

.PARAMETER Configuration
    Build configuration (default Release).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1 -GameDir "D:\Steam\steamapps\common\Wolfpack"
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repo

if (-not $GameDir) { $GameDir = $env:WOLFPACK_DIR }
if (-not $GameDir) { $GameDir = 'D:\Program Files (x86)\steam\steamapps\common\Wolfpack' }

if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'GameAssembly.dll'))) {
    Write-Warning "GameDir '$GameDir' does not look like a Wolfpack install; interop references may fail."
}

$version = '0.4.0'
$releaseDir = Join-Path $repo 'release'
$stage = Join-Path $releaseDir "UbootLib-$version"
$zip = Join-Path $releaseDir "UbootLib-$version.zip"

Write-Host "== build ($Configuration) ==" -ForegroundColor Cyan
dotnet build (Join-Path $repo 'src\UbootLib\UbootLib.csproj') -c $Configuration -v minimal -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) { throw 'lib build failed' }

dotnet build (Join-Path $repo 'samples\SampleHud\SampleHud.csproj') -c $Configuration -v minimal -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) { throw 'sample build failed' }

Write-Host "== stage ==" -ForegroundColor Cyan
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'BepInEx\plugins\UbootLib') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Mods\SampleHud') | Out-Null

$libDll = Join-Path $repo "src\UbootLib\bin\$Configuration\net6.0\UbootLib.dll"
$sampleDll = Join-Path $repo "samples\SampleHud\bin\$Configuration\net6.0\SampleHud.dll"
if (-not (Test-Path $libDll)) { throw "missing $libDll" }
if (-not (Test-Path $sampleDll)) { throw "missing $sampleDll" }

Copy-Item $libDll (Join-Path $stage 'BepInEx\plugins\UbootLib') -Force
Copy-Item $sampleDll (Join-Path $stage 'Mods\SampleHud') -Force
Copy-Item (Join-Path $repo 'samples\SampleHud\manifest.json') (Join-Path $stage 'Mods\SampleHud') -Force
$icon = Join-Path $repo 'samples\SampleHud\icon.png'
if (Test-Path $icon) { Copy-Item $icon (Join-Path $stage 'Mods\SampleHud') -Force }

@"
UbootLib $version
=================

1. Install BepInEx 6 (Unity.IL2CPP win-x64, build #793+) into the game folder.
2. Run the game once so BepInEx generates BepInEx/interop/ (takes ~90 s).
3. Copy this folder's contents into the game folder:
      BepInEx\plugins\UbootLib\UbootLib.dll
      Mods\SampleHud\...
4. Launch the game. A 'MODS' button appears at the top-right of the main menu.
   Logs: BepInEx\LogOutput.log

Remove the sample: delete Mods\SampleHud.
"@ | Set-Content -LiteralPath (Join-Path $stage 'INSTALL.txt') -Encoding UTF8

Write-Host "== package ==" -ForegroundColor Cyan
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force

Write-Host ''
Write-Host "release: $zip" -ForegroundColor Green
Get-ChildItem -LiteralPath $zip | Select-Object @{n='KB'; e={[math]::Round($_.Length/1KB,1)}}, Name | Format-Table -AutoSize
