#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $ToolsRoot,
    [switch] $Graphical
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Windows x64 is required for this verification.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $ToolsRoot) { $ToolsRoot = Join-Path $repo '.local/tools' }
$ToolsRoot = [IO.Path]::GetFullPath($ToolsRoot)
$dotnet = Join-Path $ToolsRoot 'dotnet/dotnet.exe'
$godot = Join-Path $ToolsRoot 'godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe'
foreach ($executable in @($dotnet, $godot)) {
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Missing $executable. Run tools/windows/install-toolchain.ps1 first."
    }
}
$runId = (Get-Date -AsUTC -Format 'yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$output = Join-Path $repo "artifacts/verification/$runId"
$windows = Join-Path $output 'windows'
New-Item -ItemType Directory -Path $windows -Force | Out-Null
$buildEnvironment = @{
    DOTNET_ROOT = Join-Path $ToolsRoot 'dotnet'
    DOTNET_ROOT_X64 = Join-Path $ToolsRoot 'dotnet'
    DOTNET_MULTILEVEL_LOOKUP = '0'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_NOLOGO = '1'
    NUGET_PACKAGES = Join-Path $ToolsRoot 'nuget-packages'
    RestoreLockedMode = 'true'
    PATH = (Join-Path $ToolsRoot 'dotnet') + [IO.Path]::PathSeparator + $env:PATH
}
$steps = [Collections.Generic.List[object]]::new()

function Invoke-CheckedProcess {
    param(
        [string] $Name, [string] $File, [string[]] $Arguments,
        [int] $TimeoutSeconds = 120, [string] $WorkingDirectory = $repo,
        [hashtable] $Environment = $buildEnvironment
    )
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = $WorkingDirectory
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "Running $Name"
    try {
        if (-not $process.Start()) { throw "Could not start $Name." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $outText = $stdout.GetAwaiter().GetResult()
        $errText = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $output "$Name.stdout.log"), $outText)
        [IO.File]::WriteAllText((Join-Path $output "$Name.stderr.log"), $errText)
        $steps.Add([ordered]@{ name = $Name; exitCode = $process.ExitCode; timedOut = $timedOut; seconds = [math]::Round($watch.Elapsed.TotalSeconds, 3) })
        if ($timedOut) { throw "$Name exceeded $TimeoutSeconds seconds; captured output is in $output." }
        if ($process.ExitCode -ne 0) { throw "$Name exited $($process.ExitCode).`n$outText`n$errText" }
        if (($outText + "`n" + $errText) -match '(?m)^\s*(?:SCRIPT )?ERROR:') {
            throw "$Name logged an engine error despite exit code zero.`n$outText`n$errText"
        }
        Write-Host "Passed $Name"
        return $outText.Trim()
    }
    finally { $process.Dispose() }
}

function Assert-SmokeReport {
    param([string] $Path, [bool] $Headless, [string] $BundledRuntimeRoot = '')
    if (-not (Test-Path -LiteralPath $Path)) { throw "Smoke did not produce $Path." }
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($report.passed -ne $true -or $report.headless -ne $Headless -or
        $report.advanceButtonVerified -ne $true -or $report.saveReloadButtonVerified -ne $true -or
        $report.initialTicks -ne 0 -or $report.afterAdvanceTicks -ne 120 -or
        $report.afterReloadTicks -ne 120 -or $report.finalTicks -ne 121) {
        throw "Smoke report failed its behavioral checks: $Path"
    }
    if ($BundledRuntimeRoot) {
        $runtime = [IO.Path]::GetFullPath($report.runtimeAssemblyLocation)
        $prefix = [IO.Path]::GetFullPath($BundledRuntimeRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $runtime.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Export loaded runtime outside its bundle: $runtime"
        }
    }
    if (-not $Headless -and ($report.screenshotStatus -ne 'captured' -or
        -not (Test-Path -LiteralPath $report.screenshotPath -PathType Leaf))) {
        throw "Graphical smoke did not capture a screenshot: $Path"
    }
}

$summary = [ordered]@{
    schemaVersion = 1
    startedAtUtc = [DateTime]::UtcNow.ToString('O')
    passed = $false
    osVersion = [Environment]::OSVersion.VersionString
    graphicalRequested = [bool] $Graphical
    sourceCommit = (git -C $repo rev-parse HEAD)
    sourceDirty = [bool] (git -C $repo status --porcelain)
    toolchainLockSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'toolchain.lock.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    steps = $steps
    error = $null
}
try {
    $summary.dotnetSdk = Invoke-CheckedProcess 'dotnet-version' $dotnet @('--version')
    if ($summary.dotnetSdk -ne '10.0.401') { throw "Unexpected .NET SDK: $($summary.dotnetSdk)" }
    $summary.godotVersion = Invoke-CheckedProcess 'godot-version' $godot @('--version')
    if ($summary.godotVersion -notlike '4.7.2.stable.mono.*') { throw "Unexpected Godot: $($summary.godotVersion)" }
    $null = Invoke-CheckedProcess 'restore-host' $dotnet @('restore', 'game/CozyTown.Game.csproj', '--locked-mode') 600
    $null = Invoke-CheckedProcess 'restore-tests' $dotnet @('restore', 'tests/CozyTown.Core.Tests/CozyTown.Core.Tests.csproj', '--locked-mode') 600
    $null = Invoke-CheckedProcess 'core-tests' $dotnet @('test', 'tests/CozyTown.Core.Tests/CozyTown.Core.Tests.csproj', '-c', 'Release', '--no-restore', '--logger', 'trx;LogFileName=core.trx', '--results-directory', $output) 300
    [xml] $trx = Get-Content -LiteralPath (Join-Path $output 'core.trx') -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    if ([int] $counts.total -lt 1 -or [int] $counts.passed -ne [int] $counts.total) { throw 'Core tests were skipped or failed.' }
    $summary.coreTests = [ordered]@{ total = [int] $counts.total; passed = [int] $counts.passed }
    $null = Invoke-CheckedProcess 'import' $godot @('--headless', '--path', 'game', '--import') 300
    $null = Invoke-CheckedProcess 'build-host' $dotnet @('build', 'game/CozyTown.Game.csproj', '-c', 'Debug', '--no-restore') 300
    $sceneReport = Join-Path $output 'scene-headless.json'
    $null = Invoke-CheckedProcess 'scene-headless' $godot @('--headless', '--path', 'game', '--', '--toolchain-smoke', "--report=$sceneReport")
    Assert-SmokeReport $sceneReport $true
    $executable = Join-Path $windows 'CozyTown.exe'
    $null = Invoke-CheckedProcess 'export-windows' $godot @('--headless', '--path', 'game', '--export-release', 'Windows Desktop', $executable) 600
    if (-not (Test-Path -LiteralPath $executable)) { throw 'Windows export is missing.' }
    $runtimeEnvironment = @{
        DOTNET_ROOT = Join-Path $output 'no-installed-dotnet'
        DOTNET_ROOT_X64 = Join-Path $output 'no-installed-dotnet'
        DOTNET_MULTILEVEL_LOOKUP = '0'
        PATH = "$env:SystemRoot\System32;$env:SystemRoot"
    }
    $exportReport = Join-Path $output 'export-headless.json'
    $null = Invoke-CheckedProcess 'export-headless' $executable @('--headless', '--', '--toolchain-smoke', "--report=$exportReport") -WorkingDirectory $windows -Environment $runtimeEnvironment
    Assert-SmokeReport $exportReport $true $windows
    if ($Graphical) {
        $graphicalReport = Join-Path $output 'export-graphical.json'
        $screenshot = Join-Path $output 'export-graphical.png'
        $null = Invoke-CheckedProcess 'export-graphical' $executable @('--', '--toolchain-smoke', "--report=$graphicalReport", "--screenshot=$screenshot") -WorkingDirectory $windows -Environment $runtimeEnvironment
        Assert-SmokeReport $graphicalReport $false $windows
    }
    $summary.passed = $true
}
catch {
    $summary.error = $_.Exception.Message
    throw
}
finally {
    $summary.finishedAtUtc = [DateTime]::UtcNow.ToString('O')
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding utf8
    $publishLogs = Join-Path (Split-Path $godot -Parent) 'editor_data/mono/build_logs'
    if (Test-Path -LiteralPath $publishLogs -PathType Container) {
        Copy-Item -LiteralPath $publishLogs -Destination (Join-Path $output 'dotnet-export-logs') -Recurse
    }
    Write-Host "Evidence: $output"
}
