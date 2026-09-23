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

function Read-TestCounts {
    param([string] $Path, [string] $Name)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Name did not produce a TRX report: $Path" }
    [xml] $trx = Get-Content -LiteralPath $Path -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    if ([int] $counts.total -lt 1 -or [int] $counts.passed -ne [int] $counts.total -or
        [int] $counts.executed -ne [int] $counts.total) { throw "$Name were skipped or failed." }
    return [ordered]@{
        total = [int] $counts.total
        executed = [int] $counts.executed
        passed = [int] $counts.passed
        failed = [int] $counts.failed
        skipped = [int] $counts.total - [int] $counts.executed
    }
}

function Assert-ContentReport {
    param([string] $Path, [string] $Scenario)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Content validation did not produce $Path." }
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($report.passed -isnot [bool] -or $report.passed -ne $true -or
        $report.test -ne 'content' -or $report.scenario -ne $Scenario -or
        $report.extractorVersion -ne 'scene-rect-v1' -or
        $report.fingerprint -notmatch '^[0-9a-f]{64}$' -or
        $report.inputDigest -notmatch '^[0-9a-f]{64}$' -or
        $report.presentationDigest -notmatch '^[0-9a-f]{64}$') {
        throw "Content report failed its identity checks: $Path"
    }
    $residents = @($report.definitions.residents | ForEach-Object { $_.id } | Sort-Object)
    if (($residents -join ',') -ne 'eli,mina,ren,sora' -or
        $report.manifest.width -ne 768 -or $report.manifest.height -ne 512 -or
        @($report.manifest.places).Count -ne 14 -or @($report.manifest.roads).Count -ne 14 -or
        @($report.manifest.obstacles).Count -lt 1 -or @($report.manifest.regions).Count -lt 1) {
        throw "Content report failed the four-resident map checks: $Path"
    }
    return $report
}

function Assert-SliceReport {
    param(
        [string] $Path, [string] $Scenario, [bool] $Headless,
        [string] $BundledRuntimeRoot = '', [string] $ExpectedScreenshot = ''
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Slice smoke did not produce $Path." }
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    foreach ($flag in @('passed', 'saveReloadVerified', 'pauseVerified', 'collisionVerified')) {
        if ($report.$flag -isnot [bool] -or $report.$flag -ne $true) { throw "Slice report failed $flag`: $Path" }
    }
    if ($report.test -ne 'godot_slice_smoke' -or $report.scenario -ne $Scenario -or
        $report.headless -isnot [bool] -or $report.headless -ne $Headless -or
        $report.mode -ne 'rules' -or $report.residents -ne 4 -or
        $report.contentFingerprint -notmatch '^[0-9a-f]{64}$') {
        throw "Slice report failed its mode, scenario or resident checks: $Path"
    }
    $requiredChecks = @(
        'node_rename_and_reorder_preserve_fingerprint',
        'reparent_preserving_global_transform_preserves_fingerprint',
        'duplicate_scene_identity_rejected', 'missing_referenced_place_rejected',
        'unsupported_rotation_rejected', 'unsupported_scale_rejected',
        'moving_shop_updates_shared_geometry_and_fingerprint',
        'shop_schedule_keeps_identity_and_uses_new_anchor', 'moved_shop_matches_actual_godot_collision',
        'mutation_checks_restore_original_content',
        'unregistered_world_collision_body_rejected', 'registered_obstacle_wrong_collision_layer_rejected',
        'nonidentity_map_world_transform_rejected',
        'resident_body_bindings', 'authoritative_places_clear_in_godot', 'scene_obstacle_bodies',
        'player_collision_rejection', 'player_committed_movement', 'save_during_routes', 'published_save_readable',
        'save_belongs_to_current_branch', 'route_restore_preserves_positions_and_cursors',
        'player_click_route_restored', 'player_click_route_continues',
        'stacked_pause_keeps_world_frozen', 'resume_advances_world',
        'fault_restore_rebuilds_dispatch_and_preserves_other_pause', 'fault_restore_resumes_world',
        'social_state_continues_same_commitments', 'four_residents_moved_through_scene',
        'four_independent_rule_decisions', 'ordinary_rules_created_social_activity', 'assets_conserved',
        'logical_tab_shortcut_ignores_mismatched_physical_code',
        'logical_f5_shortcut_saves_current_branch', 'logical_f9_shortcut_restores_saved_state',
        'logical_e_shortcut_opens_nearby_dialogue',
        'schedule_boundary_preempts_social_activity'
    )
    $checkNames = @($report.checks | ForEach-Object { $_.name })
    if (@($checkNames | Sort-Object -Unique).Count -ne $checkNames.Count) { throw "Slice report repeats check names: $Path" }
    foreach ($name in $requiredChecks) {
        if ($name -notin $checkNames) { throw "Slice report is missing $name`: $Path" }
    }
    foreach ($check in $report.checks) {
        if ($check.passed -isnot [bool] -or $check.passed -ne $true) { throw "Slice check failed: $($check.name) in $Path" }
    }
    if ($BundledRuntimeRoot) {
        if (-not [IO.Path]::IsPathFullyQualified($report.runtimeAssemblyLocation)) { throw 'Export runtime path must be absolute.' }
        $runtime = [IO.Path]::GetFullPath($report.runtimeAssemblyLocation)
        $prefix = [IO.Path]::GetFullPath($BundledRuntimeRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $runtime.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Slice export loaded runtime outside its bundle: $runtime"
        }
    }
    if ($ExpectedScreenshot) {
        if ($Headless -or $report.screenshotStatus -ne 'captured' -or
            [IO.Path]::GetFullPath($report.screenshotPath) -ne [IO.Path]::GetFullPath($ExpectedScreenshot) -or
            -not (Test-Path -LiteralPath $ExpectedScreenshot -PathType Leaf)) {
            throw "Slice graphical smoke did not capture the requested screenshot: $Path"
        }
        $signature = [IO.File]::ReadAllBytes($ExpectedScreenshot)
        if ($signature.Length -lt 8 -or [Convert]::ToHexString($signature[0..7]) -ne '89504E470D0A1A0A') {
            throw "Slice screenshot is not a PNG: $ExpectedScreenshot"
        }
    }
    return $report
}

function Assert-EconomyReport {
    param([string] $Path, [bool] $Headless, [string] $BundledRuntimeRoot = '', [string] $ExpectedScreenshot = '')
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Economy verification did not produce $Path." }
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($report.passed -isnot [bool] -or -not $report.passed -or $report.test -ne 'godot_economy_smoke' -or
        $report.scenario -ne 'economy-fixture' -or $report.mode -ne 'rules' -or $report.residents -ne 4 -or
        $report.headless -isnot [bool] -or $report.headless -ne $Headless -or $report.contentFingerprint -notmatch '^[0-9a-f]{64}$') {
        throw "Economy report identity or outcome failed: $Path"
    }
    $required = @(
        'economy_fixture_is_explicit',
        'baseline_player_inventory_empty',
        'player_route_reaches_shop',
        'logical_e_opens_shop',
        'shop_pause_preserves_world',
        'ui_buy_conserves_assets',
        'ui_sell_conserves_assets',
        'ui_failed_buy_preserves_assets',
        'ui_failed_sell_preserves_assets',
        'logical_escape_closes_shop',
        'logical_number_keys_select_quickbar',
        'logical_b_opens_readonly_inventory',
        'logical_escape_closes_inventory',
        'economy_save_published',
        'economy_changed_after_save',
        'save_reload_preserves_economy',
        'npc_assets_unchanged',
        'economy_journal_records_inputs_and_checkpoints',
        'fixed_step_boundary_preserves_save',
        'shop_neighbor_button_available',
        'shop_neighbor_dialogue_pauses_world',
        'shop_reopens_after_neighbor_dialogue',
        'final_shop_visible'
    )
    $names = @($report.checks | ForEach-Object name)
    if (@($names | Sort-Object -Unique).Count -ne $names.Count) { throw 'Duplicate economy verification check.' }
    foreach ($name in $required) { if ($name -notin $names) { throw "Missing economy check: $name" } }
    foreach ($check in $report.checks) {
        if ($check.passed -isnot [bool] -or -not $check.passed) { throw "Economy check failed: $($check.name)" }
    }
    if ($BundledRuntimeRoot) {
        $runtime = [IO.Path]::GetFullPath($report.runtimeAssemblyLocation)
        $prefix = [IO.Path]::GetFullPath($BundledRuntimeRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $runtime.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Economy export loaded an external runtime.' }
    }
    if ($ExpectedScreenshot) {
        if ($Headless -or $report.screenshotStatus -ne 'captured' -or
            [IO.Path]::GetFullPath($report.screenshotPath) -ne [IO.Path]::GetFullPath($ExpectedScreenshot)) { throw 'Economy screenshot identity failed.' }
        foreach ($kind in @('shop', 'bag', 'failure')) {
            $imagePath = $report.screenshots.$kind
            $expectedPath = if ($kind -eq 'shop') { $ExpectedScreenshot } else {
                Join-Path (Split-Path $ExpectedScreenshot -Parent) ([IO.Path]::GetFileNameWithoutExtension($ExpectedScreenshot) + "-$kind.png")
            }
            if ([IO.Path]::GetFullPath($imagePath) -ne [IO.Path]::GetFullPath($expectedPath)) { throw "Economy $kind screenshot identity failed." }
            if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) { throw "Missing economy $kind screenshot." }
            $signature = [IO.File]::ReadAllBytes($imagePath)
            if ($signature.Length -lt 8 -or [Convert]::ToHexString($signature[0..7]) -ne '89504E470D0A1A0A') { throw 'Economy screenshot is not PNG.' }
        }
    }
    return $report
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
    foreach ($suite in @('core', 'model', 'host')) {
        $projectName = 'CozyTown.' + (Get-Culture).TextInfo.ToTitleCase($suite) + '.Tests'
        $project = "tests/$projectName/$projectName.csproj"
        $null = Invoke-CheckedProcess "restore-$suite-tests" $dotnet @('restore', $project, '--locked-mode') 600
        $null = Invoke-CheckedProcess "$suite-tests" $dotnet @('test', $project, '-c', 'Release', '--no-restore', '--logger', "trx;LogFileName=$suite.trx", '--results-directory', $output) 300
        $summary[$suite + 'Tests'] = Read-TestCounts (Join-Path $output "$suite.trx") "$projectName tests"
    }
    $null = Invoke-CheckedProcess 'import' $godot @('--headless', '--path', 'game', '--import') 300
    $null = Invoke-CheckedProcess 'build-host' $dotnet @('build', 'game/CozyTown.Game.csproj', '-c', 'Debug', '--no-restore') 300
    $sceneReport = Join-Path $output 'scene-headless.json'
    $null = Invoke-CheckedProcess 'scene-headless' $godot @('--headless', '--path', 'game', '--', '--toolchain-smoke', "--report=$sceneReport")
    Assert-SmokeReport $sceneReport $true
    $summary.contentChecks = [ordered]@{}
    foreach ($scenario in @('baseline', 'counterfactual', 'economy-fixture')) {
        $contentReportPath = Join-Path $output "content-$scenario.json"
        $contentData = Join-Path $output "data/content-$scenario"
        $null = Invoke-CheckedProcess "content-$scenario" $godot @('--headless', '--path', 'game', '--', '--validate-content', "--scenario=$scenario", "--report=$contentReportPath", "--data-dir=$contentData")
        $contentReport = Assert-ContentReport $contentReportPath $scenario
        $summary.contentChecks[$scenario] = [ordered]@{
            passed = $true; fingerprint = $contentReport.fingerprint
            inputDigest = $contentReport.inputDigest; presentationDigest = $contentReport.presentationDigest
            places = @($contentReport.manifest.places).Count; residents = @($contentReport.definitions.residents).Count
        }
    }
    if ($summary.contentChecks.baseline.fingerprint -eq $summary.contentChecks.counterfactual.fingerprint) {
        throw 'Baseline and counterfactual content fingerprints must differ.'
    }
    $summary.sliceChecks = [ordered]@{}
    foreach ($scenario in @('baseline', 'counterfactual')) {
        $sliceName = "slice-$scenario-headless"
        $sliceReportPath = Join-Path $output "$sliceName.json"
        $sliceData = Join-Path $output "data/$sliceName"
        $null = Invoke-CheckedProcess $sliceName $godot @('--headless', '--path', 'game', '--', '--slice-smoke', "--scenario=$scenario", "--report=$sliceReportPath", "--data-dir=$sliceData") 300
        $sliceReport = Assert-SliceReport $sliceReportPath $scenario $true
        if ($sliceReport.contentFingerprint -ne $summary.contentChecks[$scenario].fingerprint) {
            throw "Slice and validation loaded different $scenario content."
        }
        $summary.sliceChecks[$sliceName] = [ordered]@{
            passed = $true; checks = @($sliceReport.checks).Count; scenario = $scenario; headless = $true
            contentFingerprint = $sliceReport.contentFingerprint; eventCount = $sliceReport.eventCount
        }
    }
    $summary.economyChecks = [ordered]@{}
    $economyReportPath = Join-Path $output 'economy-headless.json'
    $economyData = Join-Path $output 'data/economy-headless'
    $null = Invoke-CheckedProcess 'economy-headless' $godot @('--headless', '--path', 'game', '--', '--economy-smoke', '--scenario=economy-fixture', "--report=$economyReportPath", "--data-dir=$economyData") 300
    $economyReport = Assert-EconomyReport $economyReportPath $true
    if ($economyReport.contentFingerprint -ne $summary.contentChecks['economy-fixture'].fingerprint) { throw 'Economy smoke and content validation differ.' }
    $summary.economyChecks['economy-headless'] = [ordered]@{ passed = $true; checks = @($economyReport.checks).Count; contentFingerprint = $economyReport.contentFingerprint }
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
    foreach ($scenario in @('baseline', 'counterfactual')) {
        $sliceName = "export-slice-$scenario-headless"
        $sliceReportPath = Join-Path $output "$sliceName.json"
        $sliceData = Join-Path $output "data/$sliceName"
        $null = Invoke-CheckedProcess $sliceName $executable @('--headless', '--', '--slice-smoke', "--scenario=$scenario", "--report=$sliceReportPath", "--data-dir=$sliceData") 300 -WorkingDirectory $windows -Environment $runtimeEnvironment
        $sliceReport = Assert-SliceReport $sliceReportPath $scenario $true $windows
        if ($sliceReport.contentFingerprint -ne $summary.contentChecks[$scenario].fingerprint) {
            throw "Export and scene loaded different $scenario content."
        }
        $summary.sliceChecks[$sliceName] = [ordered]@{
            passed = $true; checks = @($sliceReport.checks).Count; scenario = $scenario; headless = $true
            contentFingerprint = $sliceReport.contentFingerprint; eventCount = $sliceReport.eventCount
        }
    }
    $economyReportPath = Join-Path $output 'export-economy-headless.json'
    $economyData = Join-Path $output 'data/export-economy-headless'
    $null = Invoke-CheckedProcess 'export-economy-headless' $executable @('--headless', '--', '--economy-smoke', '--scenario=economy-fixture', "--report=$economyReportPath", "--data-dir=$economyData") 300 -WorkingDirectory $windows -Environment $runtimeEnvironment
    $economyReport = Assert-EconomyReport $economyReportPath $true $windows
    if ($economyReport.contentFingerprint -ne $summary.contentChecks['economy-fixture'].fingerprint) { throw 'Economy export and content validation differ.' }
    $summary.economyChecks['export-economy-headless'] = [ordered]@{ passed = $true; checks = @($economyReport.checks).Count; contentFingerprint = $economyReport.contentFingerprint }
    if ($Graphical) {
        $graphicalReport = Join-Path $output 'export-graphical.json'
        $screenshot = Join-Path $output 'export-graphical.png'
        $null = Invoke-CheckedProcess 'export-graphical' $executable @('--', '--toolchain-smoke', "--report=$graphicalReport", "--screenshot=$screenshot") -WorkingDirectory $windows -Environment $runtimeEnvironment
        Assert-SmokeReport $graphicalReport $false $windows
        $sliceName = 'export-slice-baseline-graphical'
        $sliceReportPath = Join-Path $output "$sliceName.json"
        $sliceScreenshot = Join-Path $output "$sliceName.png"
        $sliceData = Join-Path $output "data/$sliceName"
        $null = Invoke-CheckedProcess $sliceName $executable @('--', '--slice-smoke', '--scenario=baseline', "--report=$sliceReportPath", "--data-dir=$sliceData", "--screenshot=$sliceScreenshot") 300 -WorkingDirectory $windows -Environment $runtimeEnvironment
        $sliceReport = Assert-SliceReport $sliceReportPath 'baseline' $false $windows $sliceScreenshot
        if ($sliceReport.contentFingerprint -ne $summary.contentChecks.baseline.fingerprint) {
            throw 'Graphical export and scene loaded different baseline content.'
        }
        $summary.sliceChecks[$sliceName] = [ordered]@{
            passed = $true; checks = @($sliceReport.checks).Count; scenario = 'baseline'; headless = $false
            contentFingerprint = $sliceReport.contentFingerprint; eventCount = $sliceReport.eventCount
            screenshot = $sliceScreenshot
        }
    }
    if ($Graphical) {
        $economyReportPath = Join-Path $output 'export-economy-graphical.json'
        $economyData = Join-Path $output 'data/export-economy-graphical'
        $economyScreenshot = Join-Path $output 'export-economy-graphical.png'
        $null = Invoke-CheckedProcess 'export-economy-graphical' $executable @('--', '--economy-smoke', '--scenario=economy-fixture', "--report=$economyReportPath", "--data-dir=$economyData", "--screenshot=$economyScreenshot") 300 -WorkingDirectory $windows -Environment $runtimeEnvironment
        $economyReport = Assert-EconomyReport $economyReportPath $false $windows $economyScreenshot
        if ($economyReport.contentFingerprint -ne $summary.contentChecks['economy-fixture'].fingerprint) { throw 'Graphical economy export and content validation differ.' }
        $summary.economyChecks['export-economy-graphical'] = [ordered]@{ passed = $true; checks = @($economyReport.checks).Count; contentFingerprint = $economyReport.contentFingerprint; screenshots = $economyReport.screenshots }
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
