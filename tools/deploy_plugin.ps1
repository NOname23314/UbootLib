#requires -Version 5.1
<#
.SYNOPSIS
    Build a Wolfpack BepInEx 6 plugin and deploy it into the game's plugins folder.

.DESCRIPTION
    Builds the given csproj in Release and copies the produced assembly into
    <GameDir>\BepInEx\plugins\<PluginName>\.

.PARAMETER Project
    Path to the plugin .csproj.

.PARAMETER GameDir
    Wolfpack install directory.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\deploy_plugin.ps1 -Project src\HelloWolfpack\HelloWolfpack.csproj

.NOTES
    If PowerShell blocks the script ("running scripts is disabled"), pass
    -ExecutionPolicy Bypass as shown above.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Project,

    [string]$GameDir = 'D:\Program Files (x86)\steam\steamapps\common\Wolfpack'
)

$ErrorActionPreference = 'Stop'

$proj = (Resolve-Path -LiteralPath $Project).Path
$root = (Resolve-Path -LiteralPath $GameDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $root 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'))) {
    throw "BepInEx 6 not found under '$root'. Run the installer first."
}

Write-Host "building $proj" -ForegroundColor Cyan
dotnet build $proj -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

$name = [IO.Path]::GetFileNameWithoutExtension($proj)
$out = Join-Path (Split-Path -Parent $proj) "bin\Release\net6.0\$name.dll"
if (-not (Test-Path -LiteralPath $out)) { throw "build output not found: $out" }

$dest = Join-Path $root "BepInEx\plugins\$name"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -LiteralPath $out -Destination $dest -Force

Write-Host "deployed -> $dest\$name.dll" -ForegroundColor Green
Write-Host "launch the game, then read: $root\BepInEx\LogOutput.log"
