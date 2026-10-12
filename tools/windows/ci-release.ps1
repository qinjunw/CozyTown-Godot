#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Package', 'Publish', 'Verify')][string] $Mode,
    [Parameter(Mandatory)][string] $PackageRoot,
    [string] $VerificationRoot,
    [string] $Repository,
    [string] $RunId,
    [int] $RunAttempt,
    [string] $HeadSha,
    [string] $Event,
    [ValidateSet('success', 'failure')][string] $Outcome = 'success',
    [long] $MaximumAssetBytes = 1900000000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Invoke-Git([string[]] $Arguments) {
    $value = & git -C $repo @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the release source in Git.' }
    return ($value -join "`n").Trim()
}

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-Gh([string[]] $Arguments) {
    $value = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'GitHub release operation failed; keep this run and any partial Draft Release for diagnosis.' }
    return ($value -join "`n")
}

function Assert-RelativePath([string] $Path, [switch] $Asset) {
    if ([string]::IsNullOrEmpty($Path) -or $Path -match '[:\\]' -or $Path.StartsWith('/') -or
        @($Path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0 -or
        ($Asset -and $Path.Contains('/'))) { throw "Unsafe release path: $Path" }
}

function Assert-File([string] $Root, $Record) {
    Assert-RelativePath $Record.name -Asset
    $path = Join-Path $Root $Record.name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-Item -LiteralPath $path).Length -ne $Record.bytes -or
        (Get-Sha256 $path) -ne $Record.sha256) { throw "Release asset length or SHA-256 mismatch: $($Record.name)" }
}

function Assert-Package([string] $Root) {
    $manifest = Get-Content -LiteralPath (Join-Path $Root 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
        $manifest.runId -notmatch '^[1-9][0-9]*$' -or $manifest.runAttempt -lt 1 -or
        $manifest.releaseTag -ne "ci-windows-$($manifest.runId)-$($manifest.runAttempt)" -or
        $manifest.sourceTreeSha -ne $manifest.headTreeSha -or @($manifest.assets).Count -eq 0) {
        throw 'Release manifest identity is invalid.'
    }
    $assetNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($asset in $manifest.assets) {
        if (-not $assetNames.Add($asset.name) -or $asset.bytes -ge 2147483648) { throw 'Release assets must be unique and smaller than 2 GiB.' }
        Assert-File $Root $asset
    }
    Assert-RelativePath $manifest.archive.name -Asset
    $archivePath = Join-Path $Root $manifest.archive.name
    if (-not ($manifest.assets.Count -eq 1 -and $manifest.assets[0].name -eq $manifest.archive.name)) {
        $output = [IO.File]::Create($archivePath)
        try {
            foreach ($asset in $manifest.assets) {
                $input = [IO.File]::OpenRead((Join-Path $Root $asset.name))
                try { $input.CopyTo($output) } finally { $input.Dispose() }
            }
        } finally { $output.Dispose() }
    }
    Assert-File $Root $manifest.archive
    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        Assert-RelativePath $file.path
        if (-not $expected.TryAdd($file.path, $file)) { throw 'Release file inventory contains duplicate paths.' }
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($entry in $zip.Entries) {
            Assert-RelativePath $entry.FullName
            if (-not $expected.ContainsKey($entry.FullName)) { throw "Unexpected or duplicate ZIP member: $($entry.FullName)" }
            $record = $expected[$entry.FullName]
            $stream = $entry.Open()
            try { $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            if ($entry.Length -ne $record.bytes -or $sha -ne $record.sha256) { throw "ZIP member length or SHA-256 mismatch: $($entry.FullName)" }
            $null = $expected.Remove($entry.FullName)
        }
        if ($expected.Count -ne 0) { throw 'Release archive is missing inventoried files.' }
    } finally { $zip.Dispose() }
    return $manifest
}

if ($Mode -eq 'Verify') {
    $manifest = Assert-Package ([IO.Path]::GetFullPath($PackageRoot))
    Write-Host "Verified $($manifest.files.Count) files for $($manifest.releaseTag)."
    exit 0
}
if ($Mode -eq 'Publish') {
    $packagePath = [IO.Path]::GetFullPath($PackageRoot)
    $manifest = Assert-Package $packagePath
    if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -ne $manifest.repository -or
        $env:GITHUB_RUN_ID -ne $manifest.runId -or $env:GITHUB_RUN_ATTEMPT -ne [string] $manifest.runAttempt -or
        $env:GITHUB_EVENT_NAME -ne $manifest.event -or $env:GITHUB_SHA -ne $manifest.sourceCommit) {
        throw 'Publishing requires the matching GitHub Actions repository, run, attempt, event and source SHA.'
    }
    if ($manifest.event -eq 'pull_request') {
        $eventData = Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json
        if ($eventData.pull_request.head.repo.full_name -ne $manifest.repository -or
            $eventData.pull_request.head.sha -ne $manifest.headSha) {
            throw 'Fork pull requests cannot publish releases.'
        }
    } elseif ($manifest.headSha -ne $manifest.sourceCommit) {
        throw 'Push and manual runs must target their actual verification source.'
    }
    $repositoryData = Invoke-Gh @('api', "repos/$($manifest.repository)") | ConvertFrom-Json
    if ($repositoryData.private -ne $true) { throw 'CI evidence may only be published to a private repository.' }
    $tag = $manifest.releaseTag
    $null = Invoke-Gh @('api', '--method', 'POST', "repos/$($manifest.repository)/git/refs",
        '-f', "ref=refs/tags/$tag", '-f', "sha=$($manifest.headSha)")
    $notesPath = Join-Path $packagePath 'release-notes.md'
    @"
Windows toolchain $($manifest.outcome) evidence for run $($manifest.runId), attempt $($manifest.runAttempt).

Source: $($manifest.sourceCommit); tree: $($manifest.sourceTreeSha).
Release target: $($manifest.headSha); tree: $($manifest.headTreeSha).
Run: $($manifest.runUrl)

Download manifest.json and every asset listed in its assets array. Verify using tools/windows/ci-release.ps1 -Mode Verify -PackageRoot <download-directory>. Split assets are concatenated in manifest order before ZIP members are checked.
"@ | Set-Content -LiteralPath $notesPath -Encoding utf8
    $null = Invoke-Gh @('release', 'create', $tag, '--repo', $manifest.repository, '--draft', '--verify-tag',
        '--title', "Windows CI $($manifest.runId) / $($manifest.runAttempt) ($($manifest.outcome))", '--notes-file', $notesPath)
    $uploads = @((Join-Path $packagePath 'manifest.json')) + @($manifest.assets | ForEach-Object { Join-Path $packagePath $_.name })
    $null = Invoke-Gh (@('release', 'upload', $tag, '--repo', $manifest.repository) + $uploads)
    $release = Invoke-Gh @('api', "repos/$($manifest.repository)/releases/tags/$tag") | ConvertFrom-Json
    if ($release.draft -ne $true -or $release.author.login -ne 'github-actions[bot]') {
        throw 'The uploaded release is not a Draft Release authored by this Actions publisher.'
    }
    $expectedAssets = @([pscustomobject]@{
        name = 'manifest.json'; bytes = (Get-Item -LiteralPath (Join-Path $packagePath 'manifest.json')).Length
        sha256 = Get-Sha256 (Join-Path $packagePath 'manifest.json')
    }) + @($manifest.assets)
    if (@($release.assets).Count -ne $expectedAssets.Count) { throw 'The Draft Release asset inventory differs from the package.' }
    foreach ($expected in $expectedAssets) {
        $remote = @($release.assets | Where-Object { $_.name -ceq $expected.name })
        if ($remote.Count -ne 1 -or $remote[0].size -ne $expected.bytes -or
            $remote[0].digest -ne "sha256:$($expected.sha256)") { throw "Remote release asset length or digest mismatch: $($expected.name)" }
    }
    $download = Join-Path $packagePath 'downloaded'
    if (Test-Path -LiteralPath $download) { throw 'The release download verification directory already exists.' }
    $null = New-Item -ItemType Directory -Path $download
    $null = Invoke-Gh @('release', 'download', $tag, '--repo', $manifest.repository, '--dir', $download)
    foreach ($expected in $expectedAssets) { Assert-File $download $expected }
    $downloadedManifest = Assert-Package $download
    Write-Host "Published and downloaded verified private Draft Release $tag ($($downloadedManifest.files.Count) files)."
    exit 0
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
    $RunId -notmatch '^[1-9][0-9]*$' -or $RunAttempt -lt 1 -or
    $HeadSha -notmatch '^[0-9a-f]{40}$' -or $Event -notin @('pull_request', 'push', 'workflow_dispatch')) {
    throw 'Package requires a repository, run ID, positive attempt, full head SHA and supported event.'
}
if ($MaximumAssetBytes -lt 1024 -or $MaximumAssetBytes -ge 2147483648) {
    throw 'MaximumAssetBytes must be between 1024 and 2147483647.'
}
$sourceCommit = Invoke-Git @('rev-parse', 'HEAD')
$sourceTree = Invoke-Git @('rev-parse', 'HEAD^{tree}')
$headTree = Invoke-Git @('rev-parse', "$HeadSha^{tree}")
if ($sourceTree -ne $headTree) { throw 'The verification source and release head must have the same Git tree.' }
$verificationPath = (Resolve-Path -LiteralPath $VerificationRoot).Path
$packagePath = [IO.Path]::GetFullPath($PackageRoot)
if ($packagePath.StartsWith($verificationPath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PackageRoot must be outside VerificationRoot.'
}
if (Test-Path -LiteralPath $packagePath) { throw 'PackageRoot must not already exist.' }
$runs = @(Get-ChildItem -LiteralPath $verificationPath -Directory | Sort-Object Name)
if ($Outcome -eq 'success' -and $runs.Count -eq 0) { throw 'No verification run directories were found.' }
$allItems = @((Get-Item -LiteralPath $verificationPath)) + @(Get-ChildItem -LiteralPath $verificationPath -Recurse -Force)
if (@($allItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
    throw 'Verification evidence must not contain symbolic links or other reparse points.'
}
$selected = [Collections.Generic.List[object]]::new()
$runReports = [Collections.Generic.List[object]]::new()
foreach ($run in $runs) {
    $reportPath = Join-Path $run.FullName 'verification.json'
    $report = $null
    if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
        try { $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json }
        catch { if ($Outcome -eq 'success') { throw } }
    }
    if ($Outcome -eq 'success' -and ($null -eq $report -or
        $report.sourceCommit -ne $sourceCommit -or $report.sourceDirty -ne $false)) {
        throw "Verification report does not describe this clean source: $($run.Name)"
    }
    if ($Outcome -eq 'success' -and ($report.passed -ne $true -or
        -not (Test-Path -LiteralPath (Join-Path $run.FullName 'windows/CozyTown.exe') -PathType Leaf))) {
        throw "Successful verification requires its report and Windows executable: $($run.Name)"
    }
    $runReports.Add([ordered]@{
        directory = $run.Name
        report = $(if (Test-Path -LiteralPath $reportPath -PathType Leaf) { "$($run.Name)/verification.json" } else { $null })
        reportSha256 = $(if (Test-Path -LiteralPath $reportPath -PathType Leaf) { Get-Sha256 $reportPath } else { $null })
        passed = $(if ($null -ne $report -and $null -ne $report.PSObject.Properties['passed']) { $report.passed } else { $null })
    })
}
foreach ($file in $allItems | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($verificationPath, $file.FullName).Replace('\', '/')
    if ($Outcome -eq 'success' -and -not ($relative -match '^[^/]+/windows/.+' -or
        ($relative -match '^[^/]+/[^/]+$' -and $file.Extension -in @('.json', '.trx', '.log')))) { continue }
    Assert-RelativePath $relative
    $selected.Add([ordered]@{
        path = $relative; bytes = $file.Length
        sha256 = Get-Sha256 $file.FullName; inputPath = $file.FullName
    })
}
if ($selected.Count -eq 0) {
    throw 'No verification evidence files were selected.'
}
$null = New-Item -ItemType Directory -Path $packagePath
$archiveName = if ($Outcome -eq 'success') { 'windows-toolchain.zip' } else { 'windows-toolchain-failure.zip' }
$archivePath = Join-Path $packagePath $archiveName
$zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $selected) {
        $entry = $zip.CreateEntry($file.path, [IO.Compression.CompressionLevel]::Optimal)
        $input = [IO.File]::OpenRead($file.inputPath)
        $output = $entry.Open()
        try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
    }
} finally { $zip.Dispose() }
$archive = [ordered]@{
    name = $archiveName; bytes = (Get-Item -LiteralPath $archivePath).Length
    sha256 = Get-Sha256 $archivePath
}
$assets = [Collections.Generic.List[object]]::new()
if ($archive.bytes -le $MaximumAssetBytes) {
    $assets.Add($archive)
} else {
    $input = [IO.File]::OpenRead($archivePath)
    $buffer = [byte[]]::new(1048576)
    try {
        $partNumber = 0
        while ($input.Position -lt $input.Length) {
            $partNumber++
            $partName = $archiveName + ('.part{0:D3}' -f $partNumber)
            $partPath = Join-Path $packagePath $partName
            $output = [IO.File]::Create($partPath)
            try {
                $remaining = [Math]::Min($MaximumAssetBytes, $input.Length - $input.Position)
                while ($remaining -gt 0) {
                    $read = $input.Read($buffer, 0, [int] [Math]::Min($buffer.Length, $remaining))
                    if ($read -eq 0) { throw 'Unexpected end of the archive while splitting.' }
                    $output.Write($buffer, 0, $read)
                    $remaining -= $read
                }
            } finally { $output.Dispose() }
            $assets.Add([ordered]@{ name = $partName; bytes = (Get-Item -LiteralPath $partPath).Length; sha256 = Get-Sha256 $partPath })
        }
    } finally { $input.Dispose() }
}
$manifest = [ordered]@{
    schemaVersion = 1; repository = $Repository; runId = $RunId; runAttempt = $RunAttempt
    event = $Event; workflow = 'Windows toolchain'
    runUrl = "https://github.com/$Repository/actions/runs/$RunId/attempts/$RunAttempt"
    releaseTag = "ci-windows-$RunId-$RunAttempt"; outcome = $Outcome
    headSha = $HeadSha; headTreeSha = $headTree; sourceCommit = $sourceCommit; sourceTreeSha = $sourceTree
    verificationRuns = @($runReports.ToArray()); archive = $archive; assets = @($assets.ToArray())
    files = @($selected | ForEach-Object { [ordered]@{ path = $_.path; bytes = $_.bytes; sha256 = $_.sha256 } })
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packagePath 'manifest.json') -Encoding utf8
Write-Host "Packaged $($selected.Count) files for $($manifest.releaseTag)."
