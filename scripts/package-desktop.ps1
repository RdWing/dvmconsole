# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string] $Runtime = "win-x64",

    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,

    [string] $OutputArchive
)

$ErrorActionPreference = "Stop"
$RootDirectory = Split-Path -Parent $PSScriptRoot
$PythonCommand = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $PythonCommand) {
    $PythonCommand = Get-Command python3 -ErrorAction SilentlyContinue
}
if ($null -eq $PythonCommand) {
    throw "Python 3 is required to validate package destinations and contents."
}
$Python = $PythonCommand.Source

$OutputWasSupplied = -not [string]::IsNullOrWhiteSpace($OutputArchive)
if (-not $OutputWasSupplied) {
    $OutputArchive = Join-Path $RootDirectory "artifacts/dvmconsole-$Runtime.zip"
}

$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
    throw "Publish directory does not exist: $PublishDirectory"
}
$StagingDirectory = Join-Path ([IO.Path]::GetTempPath()) ("dvmconsole-package-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $StagingDirectory | Out-Null
try {
    $TargetArguments = @(
        (Join-Path $PSScriptRoot "validate-package-target.py"),
        "--target", $OutputArchive,
        "--extension", ".zip",
        "--repository-root", $RootDirectory,
        "--publish-root", $PublishDirectory,
        "--staging-root", $StagingDirectory
    )
    if (-not $OutputWasSupplied) {
        $TargetArguments += "--allow-repository-target"
    }
    $ResolvedTarget = & $Python @TargetArguments
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($ResolvedTarget)) {
        throw "Unsafe ZIP output target: $OutputArchive"
    }
    $OutputArchive = [string]$ResolvedTarget

    & (Join-Path $PSScriptRoot "verify-publish.ps1") `
        -Runtime $Runtime `
        -PublishDirectory $PublishDirectory

    $PackageDirectory = Join-Path $StagingDirectory "DVMConsole-$Runtime"
    New-Item -ItemType Directory -Path $PackageDirectory | Out-Null
    Copy-Item -Path (Join-Path $PublishDirectory "*") -Destination $PackageDirectory -Recurse -Force
    $OutputParent = Split-Path -Parent $OutputArchive
    if (-not [string]::IsNullOrWhiteSpace($OutputParent)) {
        New-Item -ItemType Directory -Path $OutputParent -Force | Out-Null
    }
    $TemporaryArchive = Join-Path $StagingDirectory ([IO.Path]::GetFileName($OutputArchive))
    $SourceDateEpoch = if ([string]::IsNullOrWhiteSpace($env:SOURCE_DATE_EPOCH)) {
        (& git -C $RootDirectory log -1 --format=%ct).Trim()
    } else {
        $env:SOURCE_DATE_EPOCH
    }
    & $Python (Join-Path $PSScriptRoot "create-reproducible-zip.py") `
        --source-root $PackageDirectory `
        --archive $TemporaryArchive `
        --source-date-epoch $SourceDateEpoch
    if ($LASTEXITCODE -ne 0) {
        throw "Reproducible ZIP creation failed with exit code $LASTEXITCODE."
    }
    & $Python (Join-Path $PSScriptRoot "verify-package.py") `
        --archive $TemporaryArchive `
        --publish-root $PublishDirectory `
        --staged-root $PackageDirectory `
        --rid $Runtime
    if ($LASTEXITCODE -ne 0) {
        throw "Package inventory validation failed with exit code $LASTEXITCODE."
    }
    [IO.File]::Move($TemporaryArchive, $OutputArchive, $true)
}
finally {
    if (Test-Path -LiteralPath $StagingDirectory) {
        Remove-Item -LiteralPath $StagingDirectory -Recurse -Force
    }
}

Write-Host "Packaged unsigned $Runtime output to $OutputArchive"
