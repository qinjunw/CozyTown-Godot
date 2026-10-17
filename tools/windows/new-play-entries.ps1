#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $LauncherExecutable,
    [Parameter(Mandatory)][string] $Settings,
    [Parameter(Mandatory)][string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Windows is required to generate double-click entries.' }
$launcherPath = (Resolve-Path -LiteralPath $LauncherExecutable).Path
$settingsPath = (Resolve-Path -LiteralPath $Settings).Path
$entryDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf) -or [IO.Path]::GetExtension($launcherPath) -ne '.exe') {
    throw 'LauncherExecutable must point to the published self-contained CozyTown.Play.exe.'
}
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) { throw 'Settings must point to an existing local launcher JSON file.' }
$files = @('Start-Real-Model.cmd', 'Start-Offline.cmd', 'Start-Play.ps1', 'entry.local.json')
foreach ($file in $files) {
    if (Test-Path -LiteralPath (Join-Path $entryDirectory $file)) { throw 'The output already contains an entry file; use a new local directory.' }
}
New-Item -ItemType Directory -Force -Path $entryDirectory | Out-Null
$local = [ordered]@{
    launcherExecutable = [IO.Path]::GetRelativePath($entryDirectory, $launcherPath)
    settings = [IO.Path]::GetRelativePath($entryDirectory, $settingsPath)
}
$local | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $entryDirectory 'entry.local.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'play-entry.ps1') -Destination (Join-Path $entryDirectory 'Start-Play.ps1')
foreach ($entry in @(@{ Name = 'Start-Real-Model.cmd'; Mode = 'real' }, @{ Name = 'Start-Offline.cmd'; Mode = 'offline' })) {
    @"
@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Play.ps1" -Mode $($entry.Mode)
if errorlevel 1 pause
"@ | Set-Content -LiteralPath (Join-Path $entryDirectory $entry.Name) -Encoding ascii
}
Write-Output 'Created real-model and offline entries. No game or provider request was started.'
