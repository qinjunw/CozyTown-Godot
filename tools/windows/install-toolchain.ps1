#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $ToolsRoot = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.local/tools')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This installer requires Windows x64.'
}
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'This installer requires an x64 operating system.'
}

$ToolsRoot = [IO.Path]::GetFullPath($ToolsRoot)
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'toolchain.lock.json') -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1) { throw 'Unsupported toolchain lock schema.' }
New-Item -ItemType Directory -Path $ToolsRoot -Force | Out-Null
$downloads = Join-Path $ToolsRoot 'downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-ChildPath([string] $Path, [string] $Parent) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path must be inside '$Parent': $absolute"
    }
    return $absolute
}

function Remove-ManagedDirectory([string] $Path) {
    $absolute = Assert-ChildPath $Path $ToolsRoot
    if (Test-Path -LiteralPath $absolute) {
        if ((Get-Item -LiteralPath $absolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to replace a linked tool directory: $absolute"
        }
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}

function Assert-ArchiveHash([string] $Archive, $Package) {
    $actual = (Get-FileHash -LiteralPath $Archive -Algorithm $Package.hashAlgorithm).Hash
    if ($actual -ne $Package.hash) {
        throw "Checksum mismatch for '$Archive'. Expected $($Package.hashAlgorithm) $($Package.hash), got $actual. Remove or replace this archive before retrying."
    }
}

function Test-RequiredFiles([string] $Directory, $Package) {
    foreach ($relative in $Package.requiredFiles) {
        $path = Assert-ChildPath (Join-Path $Directory $relative) $Directory
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    }
    return $true
}

function Expand-Package([string] $Archive, [string] $Directory, $Package) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            if ($Package.extractFiles.Count -gt 0 -and $entry.FullName -cnotin $Package.extractFiles) { continue }
            if (-not $entry.FullName.StartsWith($Package.stripPrefix, [StringComparison]::Ordinal)) {
                throw "Archive entry has an unexpected prefix: $($entry.FullName)"
            }
            $relative = $entry.FullName.Substring($Package.stripPrefix.Length)
            if ([string]::IsNullOrEmpty($relative)) { continue }
            $target = Assert-ChildPath (Join-Path $Directory $relative) $Directory
            if ([string]::IsNullOrEmpty($entry.Name)) {
                New-Item -ItemType Directory -Path $target -Force | Out-Null
            } else {
                New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
            }
        }
    } finally { $zip.Dispose() }
}

foreach ($package in $lock.packages) {
    $archive = Assert-ChildPath (Join-Path $downloads $package.archive) $downloads
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        $partial = "$archive.partial"
        Write-Host "Downloading $($package.archive)"
        try {
            Invoke-WebRequest -Uri $package.url -OutFile $partial -UseBasicParsing
            Assert-ArchiveHash $partial $package
            Move-Item -LiteralPath $partial -Destination $archive
        } finally {
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
        }
    }
    Assert-ArchiveHash $archive $package

    $destination = Assert-ChildPath (Join-Path $ToolsRoot $package.directory) $ToolsRoot
    $marker = Join-Path $destination '.installed.json'
    if (Test-Path -LiteralPath $marker -PathType Leaf) {
        $installed = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($installed.hash -eq $package.hash -and (Test-RequiredFiles $destination $package)) {
            Write-Host "Already installed: $($package.id) $($package.version)"
            continue
        }
    }

    $staging = Assert-ChildPath (Join-Path $ToolsRoot ($package.directory + '.staging-' + [Guid]::NewGuid().ToString('N'))) $ToolsRoot
    New-Item -ItemType Directory -Path $staging | Out-Null
    try {
        Write-Host "Extracting $($package.id) $($package.version)"
        Expand-Package $archive $staging $package
        if (-not (Test-RequiredFiles $staging $package)) {
            throw "Archive does not contain the required files for $($package.id)."
        }
        $package | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $staging '.installed.json') -Encoding UTF8
        Remove-ManagedDirectory $destination
        Move-Item -LiteralPath $staging -Destination $destination
    } finally {
        Remove-ManagedDirectory $staging
    }
}

$godot = Join-Path $ToolsRoot 'godot/Godot_v4.7.2-stable_mono_win64'
$templates = Join-Path $godot 'editor_data/export_templates/4.7.2.stable.mono'
New-Item -ItemType File -Path (Join-Path $godot '_sc_') -Force | Out-Null
[pscustomobject]@{
    ToolsRoot = $ToolsRoot
    GodotEditor = Join-Path $godot 'Godot_v4.7.2-stable_mono_win64.exe'
    GodotConsole = Join-Path $godot 'Godot_v4.7.2-stable_mono_win64_console.exe'
    DotNet = Join-Path $ToolsRoot 'dotnet/dotnet.exe'
    TemplatesRoot = $templates
    DebugTemplate = Join-Path $templates 'windows_debug_x86_64.exe'
    ReleaseTemplate = Join-Path $templates 'windows_release_x86_64.exe'
}
