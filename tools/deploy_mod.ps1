#requires -Version 5.1
<#
.SYNOPSIS
    Build a Wolfpack mod and deploy it into <GameDir>\Mods\<ModId>\.

.DESCRIPTION
    Builds the mod project in Release, then copies the manifest, the mod assembly and
    any loose content files (icon.png, lang/, missions/) into the kit's Mods folder.
    Waits for the game process to exit first, otherwise the copy fails on a locked dll.

.PARAMETER Project
    Path to the mod .csproj.

.PARAMETER ModId
    Folder name under Mods/. Defaults to the project name.

.PARAMETER GameDir
    Wolfpack install directory.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\deploy_mod.ps1 -Project src\samples\SampleHud\SampleHud.csproj
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Project,

    [string]$ModId,

    [string]$GameDir = 'D:\Program Files (x86)\steam\steamapps\common\Wolfpack'
)

$ErrorActionPreference = 'Stop'

$proj = (Resolve-Path -LiteralPath $Project).Path
$projDir = Split-Path -Parent $proj
$name = [IO.Path]::GetFileNameWithoutExtension($proj)
if (-not $ModId) { $ModId = $name }

$root = (Resolve-Path -LiteralPath $GameDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $root 'BepInEx\plugins\WolfpackModKit\WolfpackModKit.dll'))) {
    throw "Wolfpack ModKit is not installed under '$root'."
}

$proc = Get-Process -Name 'Wolfpack' -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "waiting for the game to exit..." -ForegroundColor Yellow
    $proc | Stop-Process -Force
    $proc | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

Write-Host "building $proj" -ForegroundColor Cyan
dotnet build $proj -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

$dll = Join-Path $projDir "bin\Release\net6.0\$name.dll"
if (-not (Test-Path -LiteralPath $dll)) { throw "build output not found: $dll" }

$dest = Join-Path $root "Mods\$ModId"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

Copy-Item -LiteralPath $dll -Destination $dest -Force

$manifest = Join-Path $projDir 'manifest.json'
if (-not (Test-Path -LiteralPath $manifest)) { throw "manifest.json missing next to the project: $manifest" }
Copy-Item -LiteralPath $manifest -Destination $dest -Force

# Optional loose content shipped next to the project.
foreach ($item in @('icon.png', 'assets', 'lang', 'missions')) {
    $src = Join-Path $projDir $item
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination $dest -Recurse -Force
    }
}

Write-Host "deployed $ModId -> $dest" -ForegroundColor Green
Get-ChildItem -LiteralPath $dest | Select-Object Length, Name | Format-Table -AutoSize
Write-Host "launch the game, then read: $root\BepInEx\LogOutput.log"
