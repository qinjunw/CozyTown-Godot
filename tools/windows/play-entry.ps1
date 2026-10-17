param([ValidateSet('real', 'offline')][string] $Mode = 'offline', [switch] $CheckOnly)
$ErrorActionPreference = 'Stop'
try {
    $local = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'entry.local.json') -Raw | ConvertFrom-Json
    $launcher = if ([IO.Path]::IsPathRooted($local.launcherExecutable)) { $local.launcherExecutable } else { Join-Path $PSScriptRoot $local.launcherExecutable }
    $settings = if ([IO.Path]::IsPathRooted($local.settings)) { $local.settings } else { Join-Path $PSScriptRoot $local.settings }
    $launcher = [IO.Path]::GetFullPath($launcher)
    $settings = [IO.Path]::GetFullPath($settings)
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf) -or -not (Test-Path -LiteralPath $settings -PathType Leaf)) {
        throw 'entry_file_missing'
    }
    $arguments = @("--$Mode", "--settings=$settings")
    if ($CheckOnly) { $arguments += '--check-only' }
    & $launcher @arguments
    exit $LASTEXITCODE
}
catch {
    [Console]::Error.WriteLine('Launcher entry is unavailable. Check the local entry configuration; do not share private files.')
    exit 2
}
