#requires -Version 5.1
<#
.SYNOPSIS
    Remove BepInEx 6 from the Wolfpack installation (clean rollback).

.DESCRIPTION
    Deletes only the files/folders added by the BepInEx 6 (Unity.IL2CPP) install.
    Original game files are never touched. Every path is resolved and checked to
    live directly under the game root before removal.

.PARAMETER GameDir
    Wolfpack install directory. Defaults to the standard Steam path.

.PARAMETER Force
    Skip the confirmation prompt.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\uninstall_bepinex.ps1

.NOTES
    If PowerShell blocks the script ("running scripts is disabled"), pass
    -ExecutionPolicy Bypass as shown above.
#>
[CmdletBinding()]
param(
    [string]$GameDir = 'D:\Program Files (x86)\steam\steamapps\common\Wolfpack',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $GameDir)) {
    throw "Game directory not found: $GameDir"
}
$root = (Resolve-Path -LiteralPath $GameDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $root 'Wolfpack.exe'))) {
    throw "Refusing to run: '$root' does not look like the Wolfpack install (Wolfpack.exe missing)."
}

if (-not $Force) {
    Write-Host "BepInEx will be removed from:" -ForegroundColor Yellow
    Write-Host "  $root"
    $answer = Read-Host "Continue? [y/N]"
    if ($answer -notmatch '^(y|yes)$') {
        Write-Host "Aborted."; return
    }
}

# Only these entries are created by BepInEx 6. Nothing else is deleted.
$targets = @(
    'winhttp.dll',
    'doorstop_config.ini',
    '.doorstop_version',
    'changelog.txt',
    'BepInEx',
    'dotnet'
)

$removed = 0
foreach ($name in $targets) {
    $path = Join-Path $root $name
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host "skip  $name (not present)" -ForegroundColor DarkGray
        continue
    }

    # Safety: the resolved parent must be exactly the game root.
    $parent = (Resolve-Path -LiteralPath (Split-Path -Parent $path)).Path
    if ($parent -ne $root) {
        Write-Host "skip  $name (path escapes game root)" -ForegroundColor Red
        continue
    }

    Remove-Item -LiteralPath $path -Recurse -Force
    Write-Host "remove $name" -ForegroundColor Green
    $removed++
}

Write-Host ""
Write-Host "Removed $removed item(s). Game files were not modified." -ForegroundColor Cyan
Write-Host "If Steam ever reports a damaged install, use: Steam > Wolfpack > Properties > Installed Files > Verify integrity."
