#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Package', 'Verify', 'PrepareDraft')][string] $Mode,
    [Parameter(Mandatory)][string] $PackageRoot,
    [string] $VerificationRoot,
    [string] $Repository,
    [string] $RunId,
    [int] $RunAttempt,
    [string] $SourceCommit,
    [string] $ExpectedSha256,
    [string] $Version
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageRoot)
if ($ExpectedSha256) { $ExpectedSha256 = $ExpectedSha256.ToLowerInvariant() }

function Get-Digest([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Read-Json([string] $Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Write-Json($Value, [string] $Path) { $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8 }
function Invoke-Gh([string[]] $Arguments) {
    $result = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'GitHub delivery operation failed. Preserve local evidence and any partial remote draft; nothing was rolled back.' }
    return ($result -join "`n")
}
function Assert-NoLinks([string] $Root) {
    # Inspect ancestors too: a linked package directory must not redirect writes.
    $item = Get-Item -LiteralPath $Root -Force
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Delivery paths must not contain links.' }
        $item = $item.Parent
    }
    if (@(Get-ChildItem -LiteralPath $Root -Recurse -Force | Where-Object {
        $_.Attributes -band [IO.FileAttributes]::ReparsePoint
    }).Count) { throw 'Delivery files must not contain links.' }
}
function Assert-Path([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -match '[:\\*?"<>|\x00-\x1f]' -or $Path.StartsWith('/') -or
        @($Path.Split('/') | Where-Object { $_ -in @('', '.', '..') -or $_ -match '[. ]$' -or
            $_.StartsWith('.') -or
            $_ -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)' }).Count) { throw "Unsafe candidate path: $Path" }
    $runtime = 'data_CozyTown.Game_windows_x86_64/'
    if ($Path -cin @('data_CozyTown.Game_windows_x86_64/createdump.exe', 'launcher/createdump.exe')) { return }
    if ($Path -in @('CozyTown.exe', 'CozyTown.console.exe', 'CozyTown.pck', 'START-HERE.md', 'Start-Playtest.cmd')) { return }
    if ($Path -in @('launcher/launcher.example.json', 'launcher/new-play-entries.ps1', 'launcher/play-entry.ps1', 'launcher/CozyTown.Play.exe')) { return }
    if ($Path -cmatch '^launcher/[^/]+\.(dll|pdb)$' -or $Path -cmatch '^launcher/[^/]+\.(deps|runtimeconfig)\.json$') { return }
    if (-not $Path.StartsWith($runtime, [StringComparison]::Ordinal) -or
        $Path -notmatch '\.(dll|json|pdb|dat|bin)$' -or $Path -match '(?i)(^|/)(\.local|data|saves?|logs?|credentials?|ledger|world|model-config)([./-]|$)') {
        throw "Unexpected file in Windows candidate: $Path"
    }
}
function Assert-Required($Paths) {
    foreach ($required in @('CozyTown.exe', 'CozyTown.pck')) {
        if ($required -cnotin $Paths) { throw "Incomplete Windows export: $required" }
    }
    foreach ($assembly in @('System.Private.CoreLib.dll', 'CozyTown.Game.dll')) {
        if (-not @($Paths | Where-Object { $_ -clike "data_CozyTown.Game_windows_x86_64/*$assembly" }).Count) {
            throw "Incomplete Windows runtime: $assembly"
        }
    }
}
function Assert-Candidate([string] $Root, [string] $Digest) {
    if ($Digest -cnotmatch '^[0-9a-f]{64}$') { throw 'ExpectedSha256 must be a lowercase SHA-256 digest of the tested ZIP.' }
    Assert-NoLinks $Root
    $receipt = Read-Json (Join-Path $Root 'candidate.json')
    if ($receipt.schemaVersion -ne 1 -or $receipt.repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
        $receipt.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $receipt.runId -notmatch '^[1-9][0-9]*$' -or $receipt.runAttempt -lt 1 -or
        $receipt.archive.name -cne "CozyTown-windows-x64-$($receipt.sourceCommit.Substring(0,12)).zip" -or
        $receipt.archive.sha256 -cne $Digest) { throw 'Candidate identity or expected digest does not match.' }
    $archive = Join-Path $Root $receipt.archive.name
    if ((Get-Item -LiteralPath $archive).Length -ne $receipt.archive.bytes -or (Get-Digest $archive) -cne $Digest) { throw 'Candidate ZIP length or digest mismatch.' }
    $sums = (Get-Content -LiteralPath (Join-Path $Root 'SHA256SUMS.txt') -Raw).TrimEnd("`r", "`n")
    if ($sums -cne "$Digest  $($receipt.archive.name)") { throw 'SHA256SUMS.txt differs from the candidate.' }
    $inventory = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $receipt.files) {
        Assert-Path $file.path
        if ($file.bytes -lt 0 -or $file.sha256 -cnotmatch '^[0-9a-f]{64}$' -or -not $inventory.TryAdd($file.path, $file)) { throw 'Invalid candidate file inventory.' }
    }
    Assert-Required @($inventory.Keys)
    foreach ($required in @('START-HERE.md', 'Start-Playtest.cmd')) {
        if (-not $inventory.ContainsKey($required)) { throw "Candidate is missing $required." }
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            Assert-Path $entry.FullName
            if (($entry.ExternalAttributes -shr 16 -band 0xf000) -eq 0xa000 -or
                -not $inventory.ContainsKey($entry.FullName)) { throw 'Unexpected, duplicate or linked ZIP member.' }
            $record = $inventory[$entry.FullName]
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            if ($record.path -cne $entry.FullName -or $record.bytes -ne $entry.Length -or $record.sha256 -cne $hash) { throw 'ZIP inventory hash or length mismatch.' }
            $null = $inventory.Remove($entry.FullName)
        }
        if ($inventory.Count) { throw 'ZIP is missing candidate members.' }
    } finally { $zip.Dispose() }
    return $receipt
}

if ($Mode -eq 'Verify') {
    $receipt = Assert-Candidate $package $ExpectedSha256
    Write-Host "Verified $($receipt.archive.name) SHA-256 $ExpectedSha256, source $($receipt.sourceCommit)."
    exit 0
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $RunId -notmatch '^[1-9][0-9]*$') { throw 'A repository and positive run ID are required.' }
if (Test-Path -LiteralPath $package) { throw 'PackageRoot must not already exist.' }
if ($Mode -eq 'Package') {
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $RunAttempt -lt 1) { throw 'A full source commit and positive attempt are required.' }
    $verification = (Resolve-Path -LiteralPath $VerificationRoot).Path
    Assert-NoLinks $verification
    if ($package.StartsWith($verification.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'PackageRoot must be outside VerificationRoot.' }
    $reports = @(Get-ChildItem -LiteralPath $verification -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'verification.json') })
    if ($reports.Count -ne 1) { throw 'Expected exactly one verification run.' }
    $report = Read-Json (Join-Path $reports[0].FullName 'verification.json')
    if ($report.passed -isnot [bool] -or -not $report.passed -or $report.sourceDirty -isnot [bool] -or $report.sourceDirty -or
        $report.sourceCommit -cne $SourceCommit) { throw 'Candidate requires successful verification of the specified clean source.' }
    $windows = Join-Path $reports[0].FullName 'windows'
    $files = @(Get-ChildItem -LiteralPath $windows -File -Recurse -Force | Sort-Object FullName)
    $paths = @($files | ForEach-Object { [IO.Path]::GetRelativePath($windows, $_.FullName).Replace('\','/') })
    foreach ($path in $paths) {
        Assert-Path $path
        if ($path -in @('START-HERE.md', 'Start-Playtest.cmd')) { throw 'Export must not supply generated delivery instructions.' }
    }
    Assert-Required $paths
    $parent = Split-Path $package
    $null = New-Item -ItemType Directory -Force -Path $parent
    Assert-NoLinks $parent
    $null = New-Item -ItemType Directory -Path $package
    $short = $SourceCommit.Substring(0,12)
    $launcher = "@echo off`r`nsetlocal`r`ncd /d `"%~dp0`"`r`n`"%~dp0CozyTown.exe`" -- `"--data-dir=%LOCALAPPDATA%\CozyTown-Godot\Playtests\$short`"`r`n"
    $guide = "# Windows playtest candidate`n`nSource: $SourceCommit`nCI: https://github.com/$Repository/actions/runs/$RunId/attempts/$RunAttempt`n`nKeep the complete extracted directory. Double-click Start-Playtest.cmd for offline rules mode. Playtest data is isolated by source under LOCALAPPDATA/CozyTown-Godot/Playtests/$short. F5 saves; after closing and reopening, F9 loads that save. Do not pass private model configuration for this playtest.`n`nRecord the source, ZIP SHA-256, CI run, tested actions and observed failures with your acceptance result. This candidate is awaiting human acceptance.`n"
    $generated = @{ 'Start-Playtest.cmd' = $launcher; 'START-HERE.md' = $guide }
    $inventory = [Collections.Generic.List[object]]::new()
    $name = "CozyTown-windows-x64-$short.zip"
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $package $name), [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            $path = [IO.Path]::GetRelativePath($windows, $file.FullName).Replace('\','/')
            $entry = $zip.CreateEntry($path)
            $input = [IO.File]::OpenRead($file.FullName)
            $output = $entry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
            $inventory.Add(@{path=$path; bytes=$file.Length; sha256=(Get-Digest $file.FullName)})
        }
        foreach ($path in @('START-HERE.md', 'Start-Playtest.cmd')) {
            $bytes = [Text.Encoding]::UTF8.GetBytes($generated[$path])
            $entry = $zip.CreateEntry($path)
            $output = $entry.Open()
            try { $output.Write($bytes) } finally { $output.Dispose() }
            $inventory.Add(@{path=$path; bytes=$bytes.Length; sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()})
        }
    } finally { $zip.Dispose() }
    $digest = Get-Digest (Join-Path $package $name)
    Write-Json ([ordered]@{schemaVersion=1; repository=$Repository; sourceCommit=$SourceCommit; runId=$RunId; runAttempt=$RunAttempt
        archive=@{name=$name; bytes=(Get-Item -LiteralPath (Join-Path $package $name)).Length; sha256=$digest}; files=@($inventory.ToArray())}) (Join-Path $package 'candidate.json')
    "$digest  $name" | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS.txt') -Encoding utf8
    $null = Assert-Candidate $package $digest
    Write-Host "Candidate $name SHA-256 $digest"
    exit 0
}

if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -cne $Repository -or $env:GITHUB_EVENT_NAME -ne 'workflow_dispatch' -or
    $env:GITHUB_REF -ne 'refs/heads/main' -or
    $env:GITHUB_WORKFLOW_REF -cne "$Repository/.github/workflows/windows-prerelease.yml@refs/heads/main") { throw 'PrepareDraft requires the explicit main prerelease workflow.' }
if ($Version -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+-[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*$' -or $ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'A prerelease version and tested ZIP digest are required.' }
$workflow = Invoke-Gh @('api', "repos/$Repository/actions/workflows/windows-toolchain.yml") | ConvertFrom-Json
$run = Invoke-Gh @('api', "repos/$Repository/actions/runs/$RunId") | ConvertFrom-Json
if ([string]$run.id -cne $RunId -or $run.workflow_id -ne $workflow.id -or $run.event -ne 'push' -or $run.head_branch -ne 'main' -or
    $run.head_repository.full_name -cne $Repository -or $run.status -ne 'completed' -or $run.conclusion -ne 'success' -or
    $run.head_sha -cnotmatch '^[0-9a-f]{40}$' -or $run.run_attempt -lt 1) { throw 'Candidate source must be a successful main push in this repository and workflow.' }
$artifactName = "windows-candidate-$RunId-$($run.run_attempt)"
$pages = Invoke-Gh @('api', '--paginate', '--slurp', "repos/$Repository/actions/runs/$RunId/artifacts?per_page=100") | ConvertFrom-Json
$artifacts = @($pages | ForEach-Object { $_.artifacts } | Where-Object { $_.name -ceq $artifactName })
if ($artifacts.Count -ne 1 -or $artifacts[0].expired -isnot [bool] -or $artifacts[0].expired -or
    [string]$artifacts[0].workflow_run.id -cne $RunId -or $artifacts[0].workflow_run.head_sha -cne $run.head_sha) { throw 'Expected one unexpired artifact for the successful run attempt.' }
$tags = @(Invoke-Gh @('api', "repos/$Repository/git/matching-refs/tags/$Version") | ConvertFrom-Json)
$releases = Invoke-Gh @('api', '--paginate', '--slurp', "repos/$Repository/releases?per_page=100") | ConvertFrom-Json
if (@($tags | Where-Object { $_.ref -ceq "refs/tags/$Version" }).Count -or
    @($releases | ForEach-Object { $_ } | Where-Object { $_.tag_name -ceq $Version }).Count) { throw 'Version tag or Release already exists; refusing to overwrite.' }
$parent = Split-Path $package
$null = New-Item -ItemType Directory -Force -Path $parent
Assert-NoLinks $parent
$null = New-Item -ItemType Directory -Path $package
$null = Invoke-Gh @('run', 'download', $RunId, '--repo', $Repository, '--name', $artifactName, '--dir', $package)
$receipt = Assert-Candidate $package $ExpectedSha256
if ($receipt.repository -cne $Repository -or $receipt.sourceCommit -cne $run.head_sha -or
    [string]$receipt.runId -cne $RunId -or $receipt.runAttempt -ne $run.run_attempt) { throw 'Downloaded candidate differs from its successful main run.' }
$assets = @($receipt.archive.name, 'candidate.json', 'SHA256SUMS.txt')
if (@(Get-ChildItem -LiteralPath $package -Force).Count -ne 3) { throw 'Artifact must contain only the ZIP, receipt and checksum file.' }
$assetDigests = @{}
foreach ($asset in $assets) { $assetDigests[$asset] = Get-Digest (Join-Path $package $asset) }
$tagRequest = Join-Path $package 'tag-request.json'
Write-Json @{ref="refs/tags/$Version"; sha=$receipt.sourceCommit} $tagRequest
$null = Invoke-Gh @('api', '--method', 'POST', "repos/$Repository/git/refs", '--input', $tagRequest)
$request = Join-Path $package 'release-request.json'
Write-Json @{tag_name=$Version; name="CozyTown $Version"; draft=$true; prerelease=$true
    body="Windows candidate awaiting human acceptance.`nSource: $($receipt.sourceCommit)`nZIP SHA-256: $ExpectedSha256`nCI: https://github.com/$Repository/actions/runs/$RunId/attempts/$($run.run_attempt)`nUse the complete extracted directory and Start-Playtest.cmd. Public publication requires acceptance of this exact ZIP."} $request
$release = Invoke-Gh @('api', '--method', 'POST', "repos/$Repository/releases", '--input', $request) | ConvertFrom-Json
if ($release.id -notmatch '^[1-9][0-9]*$' -or $release.draft -ne $true -or $release.prerelease -ne $true) { throw 'GitHub did not create the requested prerelease draft.' }
$null = Invoke-Gh (@('release', 'upload', $Version, '--repo', $Repository) + @($assets | ForEach-Object { Join-Path $package $_ }))
$remote = Invoke-Gh @('api', "repos/$Repository/releases/$($release.id)") | ConvertFrom-Json
if ($remote.draft -ne $true -or $remote.prerelease -ne $true -or $remote.tag_name -cne $Version) { throw 'Release state changed; expected the same unpublished prerelease draft.' }
$download = Join-Path $package 'downloaded'
$null = New-Item -ItemType Directory -Path $download
$null = Invoke-Gh @('release', 'download', $Version, '--repo', $Repository, '--dir', $download)
if (@(Get-ChildItem -LiteralPath $download -Force).Count -ne 3) { throw 'Draft asset inventory differs from candidate.' }
foreach ($asset in $assets) {
    if ((Get-Digest (Join-Path $download $asset)) -cne $assetDigests[$asset]) { throw 'Draft download differs from tested candidate bytes.' }
}
$null = Assert-Candidate $download $ExpectedSha256
Write-Host "Prepared unpublished prerelease draft $Version for source $($receipt.sourceCommit), ZIP SHA-256 $ExpectedSha256."
