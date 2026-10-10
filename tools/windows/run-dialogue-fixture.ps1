#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Executable,
    [ValidateSet('success', 'delayed', 'invalid', 'timeout', 'budget')]
    [string] $Fixture = 'success',
    [string] $DataRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Windows is required to run the dialogue fixture.' }
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf) -or [IO.Path]::GetExtension($executablePath) -ne '.exe') {
    throw 'Executable must point to the exported CozyTown .exe file.'
}
if (-not $DataRoot) {
    $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $DataRoot = Join-Path $repositoryRoot 'artifacts/dialogue-manual'
}
if (-not [IO.Path]::IsPathFullyQualified($DataRoot)) { throw 'DataRoot must be an absolute directory path.' }
$runId = (Get-Date -AsUTC -Format 'yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$dataDirectory = Join-Path ([IO.Path]::GetFullPath($DataRoot)) "$Fixture-$runId"
New-Item -ItemType Directory -Path $dataDirectory | Out-Null

$startInfo = [Diagnostics.ProcessStartInfo]::new($executablePath)
$startInfo.UseShellExecute = $false
$startInfo.WorkingDirectory = Split-Path -Parent $executablePath
foreach ($argument in @('--', "--dialogue-fixture=$Fixture", "--data-dir=$dataDirectory")) {
    $startInfo.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($startInfo)
if ($null -eq $process) { throw 'The exported game could not be started.' }
try {
    [pscustomobject]@{ Fixture = $Fixture; ProcessId = $process.Id; DataDirectory = $dataDirectory }
}
finally { $process.Dispose() }
