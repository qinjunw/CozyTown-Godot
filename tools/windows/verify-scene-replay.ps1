#requires -Version 7.0
[CmdletBinding()]
param([string] $ToolsRoot)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Windows x64 is required for scene replay verification.' }
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
$output = Join-Path $repo "artifacts/scene-replay/$runId"
$fixtures = Join-Path $output 'fixtures'
$windows = Join-Path $output 'windows'
New-Item -ItemType Directory -Path $fixtures, $windows -Force | Out-Null
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
$runtimeEnvironment = @{
    DOTNET_ROOT = Join-Path $output 'no-installed-dotnet'
    DOTNET_ROOT_X64 = Join-Path $output 'no-installed-dotnet'
    DOTNET_MULTILEVEL_LOOKUP = '0'
    PATH = "$env:SystemRoot\System32;$env:SystemRoot"
}
$steps = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()

function Invoke-VerificationProcess {
    param(
        [string] $Name, [string] $File, [string[]] $Arguments,
        [int] $ExpectedExitCode = 0, [int] $TimeoutSeconds = 300,
        [string] $WorkingDirectory = $repo, [hashtable] $Environment = $buildEnvironment
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
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $outText = $stdout.GetAwaiter().GetResult()
        $errText = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $output "$Name.stdout.log"), $outText)
        [IO.File]::WriteAllText((Join-Path $output "$Name.stderr.log"), $errText)
        $steps.Add([ordered]@{
            name = $Name; exitCode = $process.ExitCode; expectedExitCode = $ExpectedExitCode
            timedOut = $timedOut; seconds = [math]::Round($watch.Elapsed.TotalSeconds, 3)
        })
        if ($timedOut) { throw "$Name exceeded $TimeoutSeconds seconds; captured output is in $output." }
        if ($process.ExitCode -ne $ExpectedExitCode) {
            throw "$Name exited $($process.ExitCode), expected $ExpectedExitCode. See its stdout/stderr logs in $output."
        }
        if (($outText + "`n" + $errText) -match '(?m)^\s*(?:SCRIPT )?ERROR:') {
            throw "$Name logged an engine error. See its stdout/stderr logs in $output."
        }
        return $outText.Trim()
    }
    finally { $process.Dispose() }
}

function Read-JsonReport {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing report: $Path" }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-ExpectedExitCode {
    param([object] $Package, [string] $Target, [string] $Code)
    $property = $Package.PSObject.Properties["expected${Target}ExitCode"]
    if ($null -ne $property) { return [int] $property.Value }
    if ($Code -eq 'passed') { return 0 }
    if ($Target -eq 'Scene') { return $(if ($Code -eq 'divergence') { 1 } else { 2 }) }
    if ($Code -in @('missing_dependency', 'unsupported_recording')) { return 2 }
    return 1
}

function Assert-ReplayReport {
    param([string] $Path, [string] $Mode, [string] $Code, [string] $BundledRuntimeRoot = '')
    $report = Read-JsonReport $Path
    $expectedPassed = $Code -eq 'passed'
    if ($report.mode -ne $Mode -or $report.code -ne $Code -or
        $report.passed -isnot [bool] -or $report.passed -ne $expectedPassed) {
        throw "Replay report does not match mode=$Mode, code=$Code, passed=$expectedPassed`: $Path"
    }
    if ($Mode -eq 'godot_scene_replay') {
        if ($report.headless -isnot [bool] -or $report.headless -ne $true -or
            $report.replay.code -ne $report.code -or $report.replay.passed -ne $report.passed) {
            throw "Scene replay report does not match its headless replay result: $Path"
        }
        $runtime = $report.runtimeAssemblyLocation
        if (-not [IO.Path]::IsPathFullyQualified($runtime) -or
            -not (Test-Path -LiteralPath $runtime -PathType Leaf)) {
            throw "Scene replay did not report an existing runtime assembly: $Path"
        }
        if ($BundledRuntimeRoot) {
            $prefix = [IO.Path]::GetFullPath($BundledRuntimeRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            if (-not [IO.Path]::GetFullPath($runtime).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Export replay loaded a runtime outside its bundle: $Path"
            }
        }
    }
    return $report
}

$summary = [ordered]@{
    schemaVersion = 1
    startedAtUtc = [DateTime]::UtcNow.ToString('O')
    passed = $false
    sourceCommit = (git -C $repo rev-parse HEAD)
    sourceDirty = [bool] (git -C $repo status --porcelain)
    toolchainLockSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'toolchain.lock.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    steps = $steps
    packages = $checks
    error = $null
}
try {
    $summary.dotnetSdk = Invoke-VerificationProcess 'dotnet-version' $dotnet @('--version')
    if ($summary.dotnetSdk -ne '10.0.401') { throw "Unexpected .NET SDK: $($summary.dotnetSdk)" }
    $summary.godotVersion = Invoke-VerificationProcess 'godot-version' $godot @('--version')
    if ($summary.godotVersion -notlike '4.7.2.stable.mono.*') { throw "Unexpected Godot: $($summary.godotVersion)" }
    $null = Invoke-VerificationProcess 'restore-host' $dotnet @('restore', 'game/CozyTown.Game.csproj', '--locked-mode') -TimeoutSeconds 600
    $null = Invoke-VerificationProcess 'restore-cli' $dotnet @('restore', 'tools/CozyTown.Replay/CozyTown.Replay.csproj', '--locked-mode') -TimeoutSeconds 600
    $null = Invoke-VerificationProcess 'import' $godot @('--headless', '--path', 'game', '--import')
    $null = Invoke-VerificationProcess 'build-host' $dotnet @('build', 'game/CozyTown.Game.csproj', '-c', 'Debug', '--no-restore')
    $null = Invoke-VerificationProcess 'build-cli' $dotnet @('build', 'tools/CozyTown.Replay/CozyTown.Replay.csproj', '-c', 'Release', '--no-restore')
    $cli = Join-Path $repo 'tools/CozyTown.Replay/bin/Release/net10.0/CozyTown.Replay.dll'
    $fixtureReportPath = Join-Path $output 'fixture-report.json'
    $null = Invoke-VerificationProcess 'generate-fixtures' $godot @('--headless', '--path', 'game', '--', '--scene-replay-fixture', "--output=$fixtures", "--report=$fixtureReportPath")
    $fixtureReport = Read-JsonReport $fixtureReportPath
    if ($fixtureReport.passed -isnot [bool] -or $fixtureReport.passed -ne $true -or $fixtureReport.mode -ne 'rules') {
        throw "Scene fixture generation failed its report checks: $fixtureReportPath"
    }
    $manifestPath = Join-Path $fixtures 'manifest.json'
    $manifest = Read-JsonReport $manifestPath
    $packages = @($manifest.packages)
    if ($packages.Count -lt 1) { throw "Fixture manifest contains no packages: $manifestPath" }
    $executable = Join-Path $windows 'CozyTown.exe'
    $null = Invoke-VerificationProcess 'export-windows' $godot @('--headless', '--path', 'game', '--export-release', 'Windows Desktop', $executable) -TimeoutSeconds 600
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Windows export is missing.' }
    $fixturePrefix = [IO.Path]::GetFullPath($fixtures).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    for ($index = 0; $index -lt $packages.Count; $index++) {
        $package = $packages[$index]
        $path = [IO.Path]::GetFullPath((Join-Path $fixtures $package.file))
        if (-not $path.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetExtension($path) -ne '.json' -or [IO.Path]::GetFileName($path) -eq 'manifest.json' -or
            -not (Test-Path -LiteralPath $path -PathType Leaf) -or -not $seen.Add($path)) {
            throw "Manifest package must identify a unique JSON file inside the fixture directory: $($package.file)"
        }
        $coreCode = [string] $package.expectedCoreCode
        $sceneCode = [string] $package.expectedSceneCode
        if (-not $coreCode -or -not $sceneCode) { throw "Manifest package is missing expected replay codes: $($package.file)" }
        $sceneArguments = @()
        $sceneArgumentsProperty = $package.PSObject.Properties['sceneArguments']
        if ($null -ne $sceneArgumentsProperty) {
            $sceneArguments = @($sceneArgumentsProperty.Value)
            if ($sceneArguments.Count -gt 1 -or @($sceneArguments | Where-Object { $_ -isnot [string] -or $_ -ne '--scene-replay-obstruct' }).Count -gt 0) {
                throw "Manifest sceneArguments accepts only --scene-replay-obstruct: $($package.file)"
            }
        }
        $result = [ordered]@{
            file = $package.file
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            expectedCoreCode = $coreCode
            expectedSceneCode = $sceneCode
        }
        $name = '{0:D2}-core' -f $index
        $reportPath = Join-Path $output "$name.json"
        $null = Invoke-VerificationProcess $name $dotnet @($cli, "--input=$path", "--report=$reportPath") -ExpectedExitCode (Get-ExpectedExitCode $package 'Core' $coreCode)
        $report = Assert-ReplayReport $reportPath 'core_fact_replay' $coreCode
        $result.core = [ordered]@{ passed = $report.passed; code = $report.code; report = $reportPath }
        foreach ($target in @('source', 'export')) {
            $name = '{0:D2}-{1}' -f $index, $target
            $reportPath = Join-Path $output "$name.json"
            $arguments = @('--headless')
            if ($target -eq 'source') { $arguments += @('--path', 'game') }
            $arguments += @('--', "--scene-replay=$path", "--report=$reportPath")
            $arguments += $sceneArguments
            if ($target -eq 'source') {
                $null = Invoke-VerificationProcess $name $godot $arguments -ExpectedExitCode (Get-ExpectedExitCode $package 'Scene' $sceneCode)
                $report = Assert-ReplayReport $reportPath 'godot_scene_replay' $sceneCode
            }
            else {
                $null = Invoke-VerificationProcess $name $executable $arguments -ExpectedExitCode (Get-ExpectedExitCode $package 'Scene' $sceneCode) -WorkingDirectory $windows -Environment $runtimeEnvironment
                $report = Assert-ReplayReport $reportPath 'godot_scene_replay' $sceneCode $windows
            }
            $expectedQueries = $package.PSObject.Properties['expectedMotionQueries']
            if ($null -ne $expectedQueries -and $null -ne $expectedQueries.Value) {
                if ($report.motionQueries -ne [int] $expectedQueries.Value -or
                    $report.initialBodyQueries -lt 5 -or
                    $report.intersectShapeCalls -ne $report.motionQueries + $report.initialBodyQueries -or
                    $report.castMotionCalls -ne $report.intersectShapeCalls) {
                    throw "Actual scene query counts differ from the fixture contract: $reportPath"
                }
            }
            $result[$target] = [ordered]@{ passed = $report.passed; code = $report.code; report = $reportPath }
        }
        $checks.Add($result)
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
    Write-Host "Evidence: $output"
}
