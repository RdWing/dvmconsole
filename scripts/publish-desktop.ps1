# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string] $Runtime = "win-x64",

    [string] $OutputDirectory,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$RootDirectory = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RootDirectory "src/DvmConsole.Desktop/DvmConsole.Desktop.csproj"
$PythonCommand = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $PythonCommand) {
    $PythonCommand = Get-Command python3 -ErrorAction SilentlyContinue
}
if ($null -eq $PythonCommand) {
    throw "Python 3 is required to validate publish destinations."
}
$Python = $PythonCommand.Source

$OutputWasSupplied = -not [string]::IsNullOrWhiteSpace($OutputDirectory)
if (-not $OutputWasSupplied) {
    $OutputDirectory = Join-Path $RootDirectory "artifacts/$Runtime"
}

$BuildRoot = Join-Path ([IO.Path]::GetTempPath()) ("dvmconsole-publish-build-" + [Guid]::NewGuid().ToString("N"))
$DeterministicMetadataRoot = Join-Path ([IO.Path]::GetTempPath()) "dvmconsole-package-metadata/$Runtime"
New-Item -ItemType Directory -Path $BuildRoot -Force | Out-Null
if (Test-Path -LiteralPath $DeterministicMetadataRoot) {
    $MetadataItem = Get-Item -LiteralPath $DeterministicMetadataRoot -Force
    if ($MetadataItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Deterministic metadata root must not be a link: $DeterministicMetadataRoot"
    }
    Remove-Item -LiteralPath $DeterministicMetadataRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $DeterministicMetadataRoot -Force | Out-Null
$env:CARGO_TARGET_DIR = Join-Path $BuildRoot "cargo"

try {
$TargetArguments = @(
    (Join-Path $PSScriptRoot "validate-package-target.py"),
    "--target", $OutputDirectory,
    "--directory",
    "--repository-root", $RootDirectory,
    "--staging-root", $BuildRoot
)
if (-not $OutputWasSupplied) {
    $TargetArguments += "--allow-repository-target"
}
$ResolvedOutput = & $Python @TargetArguments
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($ResolvedOutput)) {
    throw "Unsafe publish output directory: $OutputDirectory"
}
$OutputDirectory = [string]$ResolvedOutput
if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

dotnet restore $Project `
    --runtime $Runtime `
    --locked-mode `
    --ignore-failed-sources `
    -p:Configuration=$Configuration `
    -p:DvmConsoleTargetPlatform=windows `
    -p:DvmConsolePackageRuntime=$Runtime `
    -p:PublishTrimmed=true `
    -p:TrimMode=partial `
    -p:NuGetAudit=false `
    -p:DvmConsoleIsolatedBuildRoot=$BuildRoot `
    -p:DvmConsoleDeterministicMetadataRoot=$DeterministicMetadataRoot `
    --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

$NativeVocoderTarget = if ($Runtime -eq "win-arm64") {
    "aarch64-pc-windows-msvc"
} else {
    "x86_64-pc-windows-msvc"
}
$PublishProperties = @(
    "-p:UseAppHost=true",
    "-p:NativeVocoderTarget=$NativeVocoderTarget",
    "-p:DvmConsoleTargetPlatform=windows",
    "-p:DvmConsolePackageRuntime=$Runtime",
    "-p:DebugType=None",
    "-p:PublishTrimmed=true",
    "-p:TrimMode=partial",
    "-p:DvmConsoleIsolatedBuildRoot=$BuildRoot",
    "-p:DvmConsoleDeterministicMetadataRoot=$DeterministicMetadataRoot"
)
$PublishProperties += @(
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true"
)
if (-not [string]::IsNullOrWhiteSpace($env:DVM_RELEASE_VERSION)) {
    $PublishProperties += @(
        "-p:Version=$($env:DVM_RELEASE_VERSION)",
        "-p:InformationalVersion=$($env:DVM_RELEASE_VERSION)"
    )
}

dotnet publish $Project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --no-restore `
    --output $OutputDirectory `
    @PublishProperties
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot "verify-publish.ps1") `
    -Runtime $Runtime `
    -PublishDirectory $OutputDirectory
}
finally {
    if (Test-Path -LiteralPath $BuildRoot) {
        Remove-Item -LiteralPath $BuildRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $DeterministicMetadataRoot) {
        Remove-Item -LiteralPath $DeterministicMetadataRoot -Recurse -Force
    }
}

Write-Host "Published $Runtime to $OutputDirectory"
