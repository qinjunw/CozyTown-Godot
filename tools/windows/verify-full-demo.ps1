#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $ExpectedCommit,
    [string] $ToolsRoot,
    [string] $InputFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Windows x64 is required for full-demo verification.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $ToolsRoot) { $ToolsRoot = Join-Path $repo '.local/tools' }
$ToolsRoot = [IO.Path]::GetFullPath($ToolsRoot)
$dotnet = Join-Path $ToolsRoot 'dotnet/dotnet.exe'
$godot = Join-Path $ToolsRoot 'godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe'
$powershell = (Get-Process -Id $PID).Path
$explicitInput = -not [string]::IsNullOrWhiteSpace($InputFile)
if ($explicitInput -and -not [IO.Path]::IsPathFullyQualified($InputFile)) { throw 'InputFile must be an absolute path.' }
if (-not $explicitInput) { $InputFile = Join-Path $repo 'game/Content/full-demo-input.json' }
$InputFile = [IO.Path]::GetFullPath($InputFile)
foreach ($file in @($dotnet, $godot, $InputFile)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing verification input: $file" }
}
$runId = (Get-Date -AsUTC -Format 'yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$output = Join-Path $repo "artifacts/full-demo-verification/$runId"
New-Item -ItemType Directory -Path $output | Out-Null
$buildEnvironment = @{
    DOTNET_ROOT = Join-Path $ToolsRoot 'dotnet'; DOTNET_ROOT_X64 = Join-Path $ToolsRoot 'dotnet'
    DOTNET_MULTILEVEL_LOOKUP = '0'; DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; DOTNET_NOLOGO = '1'
    NUGET_PACKAGES = Join-Path $ToolsRoot 'nuget-packages'; RestoreLockedMode = 'true'
    PATH = (Join-Path $ToolsRoot 'dotnet') + [IO.Path]::PathSeparator + $env:PATH
}
$runtimeEnvironment = @{
    DOTNET_ROOT = Join-Path $output 'no-installed-dotnet'; DOTNET_ROOT_X64 = Join-Path $output 'no-installed-dotnet'
    DOTNET_MULTILEVEL_LOOKUP = '0'; PATH = "$env:SystemRoot\System32;$env:SystemRoot"
}
$steps = [Collections.Generic.List[object]]::new()
$scenarios = [Collections.Generic.List[object]]::new()
$replays = [Collections.Generic.List[object]]::new()
$inputFileSha256 = (Get-FileHash -LiteralPath $InputFile -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedInputSha256 = $null
$expectedSimulationDigest = $null

function Assert-FrozenCandidate {
    $head = git -C $repo rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedCommit) { throw 'Git HEAD differs from ExpectedCommit.' }
    # Godot rewrites tracked import metadata while importing; gameplay, content and scripts remain frozen.
    $dirty = @(git -C $repo status --porcelain --untracked-files=all | Where-Object {
        -not ($_.StartsWith(' M game/Art/') -and $_.EndsWith('.import'))
    })
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -gt 0) { throw 'Candidate has uncommitted files outside generated art import metadata.' }
    if ((Get-FileHash -LiteralPath $InputFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputFileSha256) {
        throw 'Frozen input file changed during verification.'
    }
}

function Invoke-VerificationProcess {
    param([string] $Name, [string] $File, [string[]] $Arguments, [int] $TimeoutSeconds = 600,
        [string] $WorkingDirectory = $repo, [hashtable] $Environment = $buildEnvironment)
    Assert-FrozenCandidate
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.WorkingDirectory = $WorkingDirectory
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $info
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "Running $Name"
    try {
        if (-not $process.Start()) { throw "Could not start $Name." }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $outText = $stdout.GetAwaiter().GetResult(); $errText = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $output "$Name.stdout.log"), $outText)
        [IO.File]::WriteAllText((Join-Path $output "$Name.stderr.log"), $errText)
        $steps.Add([ordered]@{ name = $Name; file = $File; arguments = $Arguments; exitCode = $process.ExitCode
            timedOut = $timedOut; seconds = [math]::Round($watch.Elapsed.TotalSeconds, 3) })
        if ($timedOut -or $process.ExitCode -ne 0) { throw "$Name failed or timed out. See its stdout/stderr logs in $output." }
        if (($outText + "`n" + $errText) -match '(?m)^\s*(?:SCRIPT )?ERROR:') { throw "$Name logged an engine error." }
        Assert-FrozenCandidate
        Write-Host "Passed $Name"
        return $outText.Trim()
    }
    finally { $process.Dispose() }
}

function Read-Report {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing report: $Path" }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 100
}

function Read-ChildVerification {
    param([string] $Stdout, [string] $ArtifactArea)
    $evidenceMatches = [regex]::Matches($Stdout, '(?m)^Evidence: ([^\r\n]+)\r?$')
    if ($evidenceMatches.Count -ne 1) { throw 'Verification must identify exactly one evidence directory.' }
    $directory = [IO.Path]::GetFullPath($evidenceMatches[0].Groups[1].Value)
    $prefix = [IO.Path]::GetFullPath((Join-Path $repo $ArtifactArea)).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $directory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Child evidence directory is outside its artifact area.' }
    $path = Join-Path $directory 'verification.json'
    $report = Read-Report $path
    if ($report.passed -isnot [bool] -or -not $report.passed -or $report.sourceCommit -ne $ExpectedCommit) {
        throw "Child verification did not pass for the frozen commit: $path"
    }
    return [ordered]@{ directory = $directory; report = $path
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Assert-FixedReport {
    param([string] $Path, [string] $Scenario, [string] $DataRoot)
    $report = Read-Report $Path
    if ($report.schemaVersion -ne 1 -or $report.test -ne 'full_demo_evaluation' -or
        $report.passed -isnot [bool] -or -not $report.passed -or $report.mode -ne 'fixed' -or
        $report.realAcceptanceEvaluated -isnot [bool] -or $report.realAcceptanceEvaluated -ne $false -or
        $report.scenario -ne $Scenario -or $report.realHttpSends -ne 0 -or $null -ne $report.budget -or
        $report.budgetProfileVersion -ne 'full-demo-v1' -or $report.executionPurpose -ne 'experiment' -or
        $report.budgetId -ne 'cozytown-godot-full-demo-20261010-v1' -or
        $report.mornings -ne 1 -or $report.sleeps -ne 2 -or @($report.windows).Count -ne 2 -or
        @($report.branches).Count -ne 2 -or $report.inputSha256 -notmatch '^[0-9a-f]{64}$' -or
        $report.acceptedRealSeconds -le 0 -or $report.wallSeconds -le 0) { throw "Fixed full-demo identity or outcome failed: $Path" }
    if ($null -eq $script:expectedInputSha256) { $script:expectedInputSha256 = $report.inputSha256 }
    if ($report.inputSha256 -ne $script:expectedInputSha256) { throw 'Full-demo runs used different canonical input plans.' }
    $digest = $report.toolchain.scene.simulationSourceDigest
    if ($null -eq $script:expectedSimulationDigest) { $script:expectedSimulationDigest = $digest }
    if ($digest -notmatch '^[0-9a-f]{64}$' -or $digest -ne $script:expectedSimulationDigest -or
        -not $report.toolchain.scene.buildVersion.EndsWith($ExpectedCommit)) { throw 'Full-demo build provenance differs from the frozen candidate.' }
    $residents = @('eli', 'mina', 'ren', 'sora')
    for ($day = 1; $day -le 2; $day++) {
        $window = $report.windows[$day - 1]
        if ($window.day -ne $day -or $window.elapsedSeconds -lt 0 -or $window.elapsedSeconds -gt 180.000001 -or
            $window.ordinaryAdmissions -lt 0 -or $window.ordinaryAdmissions -gt 18 -or
            $window.drainSeconds -lt 0 -or $window.drainSeconds -gt 60.000001 -or
            $window.drained -ne $true -or $window.requiredSaveCompleted -ne $true -or
            $window.stopReason -notin @('ordinary_limit', 'duration_limit') -or
            (@($window.coverage | ForEach-Object residentId | Sort-Object) -join ',') -ne ($residents -join ',') -or
            @($window.coverage | Where-Object adopted -lt 1).Count -ne 0) { throw "Day $day lacks its required window or resident coverage: $Path" }
        if ($window.stopReason -eq 'ordinary_limit' -and $window.ordinaryAdmissions -ne 18 -or
            $window.stopReason -eq 'duration_limit' -and [math]::Abs($window.elapsedSeconds - 180) -gt .000001) {
            throw 'Window stopped before reaching its recorded limit.'
        }
    }
    if ([math]::Floor($report.windows[1].startMinute / 1440) - [math]::Floor($report.windows[0].startMinute / 1440) -ne 1) {
        throw 'Full-demo windows do not cover adjacent days.'
    }
    $commands = @($report.commands); $plan = @($report.input.commands)
    if ($commands.Count -ne $plan.Count) { throw 'Full-demo did not finish every frozen command.' }
    for ($index = 0; $index -lt $commands.Count; $index++) {
        if ($commands[$index].cursor -ne $index -or
            ($commands[$index].command | ConvertTo-Json -Compress) -ne ($plan[$index] | ConvertTo-Json -Compress)) {
            throw 'Full-demo command sequence differs from its frozen input plan.'
        }
    }
    $shown = @($report.events | Where-Object { $_.kind -eq 'player_dialogue_displayed' -and $_.source -eq 'fixed_model' })
    if ($shown.Count -ne 4 -or (@($shown | ForEach-Object residentId | Sort-Object) -join ',') -ne ($residents -join ',')) {
        throw 'Full-demo did not display four fixed model texts to their residents.'
    }
    $sessions = @($report.events | Where-Object kind -eq 'player_dialogue_opened')
    $requests = @($report.events | Where-Object kind -eq 'player_dialogue_requested')
    foreach ($display in $shown) {
        if (@($sessions | Where-Object { $_.activityId -eq $display.activityId -and $_.residentId -eq $display.residentId }).Count -ne 1 -or
            @($requests | Where-Object { $_.activityId -eq $display.activityId -and $_.residentId -eq $display.residentId -and
                $_.logicalRequestId -eq $display.logicalRequestId }).Count -ne 1 -or
            [string]::IsNullOrWhiteSpace($display.logicalRequestId)) { throw 'Player text was displayed in the wrong session.' }
    }
    foreach ($kind in @('candidate_executed', 'player_dialogue_memory_committed')) {
        $identities = @($report.events | Where-Object kind -eq $kind | ForEach-Object {
            if ($kind -eq 'candidate_executed') { $_.logicalRequestId } else { ($_.dataJson | ConvertFrom-Json).id }
        })
        if (@($identities | Sort-Object -Unique).Count -ne $identities.Count) { throw "Duplicate $kind identity in full-demo record." }
    }
    if ($report.finalState.decisionMode -ne 'rules' -or $report.finalState.budgetProfileVersion -ne 'full-demo-v1' -or
        $report.finalState.executionPurpose -ne 'experiment') { throw 'Fixed final save lost its runtime binding.' }
    $prefix = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($branch in $report.branches) {
        $path = [IO.Path]::GetFullPath($branch.export)
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -ne $branch.archiveBytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $branch.sha256) {
            throw 'Full-demo branch export path, size or hash differs from its report.'
        }
    }
    return $report
}

function Read-PackageCounts {
    param([string] $Path)
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object FullName -eq 'replay.json')
        if ($entries.Count -ne 1 -or $entries[0].Length -gt 512MB) { throw 'Export must contain exactly one supported replay.json.' }
        $stream = $entries[0].Open()
        try { $document = [Text.Json.JsonDocument]::Parse($stream) }
        finally { $stream.Dispose() }
        try {
            $root = $document.RootElement
            $boundaryCount = $root.GetProperty('boundaryCount').GetInt32()
            $allBoundaries = 0; $motionQueries = 0; $segments = 0
            while ($true) {
                $segments++
                if ($segments -gt 33) { throw 'Replay parent chain exceeds the supported depth.' }
                $allBoundaries += $root.GetProperty('boundaryCount').GetInt32()
                foreach ($boundary in $root.GetProperty('boundaries').EnumerateArray()) {
                    $motionQueries += $boundary.GetProperty('queries').GetArrayLength()
                }
                if ($root.GetProperty('scene').GetProperty('simulationSourceDigest').GetString() -ne $script:expectedSimulationDigest) {
                    throw 'A replay branch or parent uses another simulation source.'
                }
                $parent = $root.GetProperty('parent')
                if ($parent.ValueKind -eq [Text.Json.JsonValueKind]::Null) { break }
                $root = $parent.GetProperty('prefix')
            }
            return [ordered]@{ boundaryCount = $boundaryCount; allBoundaries = $allBoundaries
                motionQueries = $motionQueries; segments = $segments; packageBytes = $entries[0].Length }
        }
        finally { $document.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Assert-ReplayReport {
    param([string] $Path, [string] $Mode, [object] $Counts, [string] $PackageHash, [string] $BundledRoot = '')
    $report = Read-Report $Path
    if ($report.passed -isnot [bool] -or -not $report.passed -or $report.code -ne 'passed' -or $report.mode -ne $Mode) {
        throw "Replay did not pass in $Mode`: $Path"
    }
    $replay = if ($Mode -eq 'core_fact_replay') { $report } else { $report.replay }
    if ($replay.boundary -ne $Counts.boundaryCount -or $replay.passed -ne $true -or $replay.mode -ne $Mode) {
        throw 'Replay did not verify every branch boundary.'
    }
    if ($Mode -eq 'godot_scene_replay') {
        if ($report.headless -ne $true -or $report.inputSha256 -ne $PackageHash -or
            $report.motionQueries -ne $Counts.motionQueries -or $report.initialBodyQueries -ne 5 * $Counts.segments -or
            $report.bodyBoundaries -ne $Counts.allBoundaries + $Counts.segments -or
            $report.intersectShapeCalls -ne $Counts.motionQueries + 5 * $Counts.segments -or
            $report.castMotionCalls -ne $report.intersectShapeCalls -or
            $report.actualDependencies.simulationSourceDigest -ne $script:expectedSimulationDigest) {
            throw 'Actual scene queries, initial bodies or source identity differ from the complete package.'
        }
        $runtime = [IO.Path]::GetFullPath($report.runtimeAssemblyLocation)
        if (-not (Test-Path -LiteralPath $runtime -PathType Leaf)) { throw 'Scene replay runtime assembly is missing.' }
        if ($BundledRoot -and -not $runtime.StartsWith([IO.Path]::GetFullPath($BundledRoot).TrimEnd('\', '/') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Windows replay loaded an installed runtime outside its bundle.'
        }
    }
    return $report
}

$summary = [ordered]@{ schemaVersion = 1; passed = $false; sourceCommit = $ExpectedCommit
    startedAtUtc = [DateTime]::UtcNow.ToString('O'); mode = 'fixed'; realHttpSends = 0; graphicalRequested = $false
    realAcceptanceEvaluated = $false
    inputFileSha256 = $inputFileSha256; explicitInput = $explicitInput
    toolchainLockSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'toolchain.lock.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    steps = $steps; scenarios = $scenarios; replays = $replays; error = $null }
try {
    Assert-FrozenCandidate
    Copy-Item -LiteralPath $InputFile -Destination (Join-Path $output 'input.json')
    $stdout = Invoke-VerificationProcess 'unified-verification' $powershell @('-NoProfile', '-NonInteractive', '-File',
        (Join-Path $PSScriptRoot 'verify.ps1'), '-ToolsRoot', $ToolsRoot) 3600
    $summary.unifiedVerification = Read-ChildVerification $stdout 'artifacts/verification'
    $stdout = Invoke-VerificationProcess 'scene-replay-matrix' $powershell @('-NoProfile', '-NonInteractive', '-File',
        (Join-Path $PSScriptRoot 'verify-scene-replay.ps1'), '-ToolsRoot', $ToolsRoot) 3600
    $summary.sceneReplayVerification = Read-ChildVerification $stdout 'artifacts/scene-replay'
    $windows = Join-Path $summary.unifiedVerification.directory 'windows'
    $executable = Join-Path $windows 'CozyTown.exe'
    $cli = Join-Path $repo 'tools/CozyTown.Replay/bin/Release/net10.0/CozyTown.Replay.dll'
    foreach ($file in @($executable, $cli)) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing freshly verified runtime: $file" } }
    $summary.windowsBundle = @(Get-ChildItem -LiteralPath $windows -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ file = [IO.Path]::GetRelativePath($windows, $_.FullName); bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    foreach ($scenario in @('baseline', 'counterfactual')) {
        $dataRoot = Join-Path $output "data/$scenario"
        $reportPath = Join-Path $output "$scenario.json"
        $arguments = @('--headless', '--path', 'game', '--', '--full-demo-evaluation=fixed', "--scenario=$scenario",
            "--report=$reportPath", "--data-dir=$dataRoot")
        if ($explicitInput) { $arguments += "--full-demo-input=$InputFile" }
        $null = Invoke-VerificationProcess "fixed-$scenario" $godot $arguments
        $report = Assert-FixedReport $reportPath $scenario $dataRoot
        $scenarios.Add([ordered]@{ scenario = $scenario; report = $reportPath; mode = $report.mode; realHttpSends = $report.realHttpSends
            acceptedRealSeconds = $report.acceptedRealSeconds; wallSeconds = $report.wallSeconds
            windows = $report.windows; mornings = $report.mornings; sleeps = $report.sleeps
            peakWorkingSetBytes = $report.peakWorkingSetBytes; branches = $report.branches
            sha256 = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant() })
        $index = 0
        foreach ($branch in $report.branches) {
            $name = "$scenario-$index"
            $counts = Read-PackageCounts $branch.export
            if ($counts.boundaryCount -ne $branch.boundaries -or $counts.packageBytes -ne $branch.packageBytes -or
                $counts.allBoundaries - $counts.boundaryCount -ne $branch.parentBoundaries -or $counts.motionQueries -lt 1) {
                throw 'Complete exported package counts differ from the host branch report.'
            }
            $result = [ordered]@{ scenario = $scenario; branchId = $branch.branchId; package = $branch.export
                archiveBytes = $branch.archiveBytes; sha256 = $branch.sha256; counts = $counts }
            $path = Join-Path $output "$name-core.json"; $metrics = Join-Path $output "$name-core-metrics.json"
            $null = Invoke-VerificationProcess "$name-core" $dotnet @($cli, "--input=$($branch.export)", "--report=$path", "--metrics=$metrics")
            $null = Assert-ReplayReport $path 'core_fact_replay' $counts $branch.sha256
            $result.core = [ordered]@{ report = $path; metrics = Read-Report $metrics }
            foreach ($target in @('source', 'export')) {
                $path = Join-Path $output "$name-$target.json"
                $arguments = @('--headless')
                if ($target -eq 'source') { $arguments += @('--path', 'game') }
                $arguments += @('--', "--scene-replay=$($branch.export)", "--report=$path")
                $bundleRoot = ''
                if ($target -eq 'source') { $null = Invoke-VerificationProcess "$name-source" $godot $arguments }
                else {
                    $null = Invoke-VerificationProcess "$name-export" $executable $arguments -WorkingDirectory $windows -Environment $runtimeEnvironment
                    $bundleRoot = $windows
                }
                $scene = Assert-ReplayReport $path 'godot_scene_replay' $counts $branch.sha256 $bundleRoot
                $result[$target] = [ordered]@{ report = $path; boundary = $scene.replay.boundary; motionQueries = $scene.motionQueries
                    initialBodyQueries = $scene.initialBodyQueries; bodyBoundaries = $scene.bodyBoundaries; runtimeAssemblyLocation = $scene.runtimeAssemblyLocation }
            }
            $replays.Add($result); $index++
        }
        $report = $null
    }
    $dataRoot = Join-Path $output 'data/export-default-input'
    $path = Join-Path $output 'export-default-input.json'
    $null = Invoke-VerificationProcess 'export-default-input' $executable @('--headless', '--', '--full-demo-evaluation=fixed',
        '--scenario=baseline', "--report=$path", "--data-dir=$dataRoot") -WorkingDirectory $windows -Environment $runtimeEnvironment
    $report = Assert-FixedReport $path 'baseline' $dataRoot
    $summary.exportDefaultInput = [ordered]@{ report = $path; inputSha256 = $report.inputSha256; mode = $report.mode
        realHttpSends = $report.realHttpSends; branches = $report.branches
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    $summary.inputSha256 = $expectedInputSha256; $summary.simulationSourceDigest = $expectedSimulationDigest
    $summary.passed = $true
}
catch { $summary.error = $_.Exception.Message; throw }
finally {
    $summary.finishedAtUtc = [DateTime]::UtcNow.ToString('O')
    $summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding utf8
    Write-Host "Evidence: $output"
}
