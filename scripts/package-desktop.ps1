[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string] $Runtime = "win-x64",

    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,

    [string] $OutputArchive,

    [switch] $AllowMissingVocoder
)

$ErrorActionPreference = "Stop"
$RootDirectory = Split-Path -Parent $PSScriptRoot

function Assert-X64PeFile([string] $Path) {
    $Stream = [IO.File]::OpenRead($Path)
    try {
        $Reader = [IO.BinaryReader]::new($Stream)
        if ($Stream.Length -lt 64 -or $Reader.ReadUInt16() -ne 0x5A4D) {
            throw "File is not a Windows PE executable: $Path"
        }

        $Stream.Position = 0x3C
        $PeOffset = $Reader.ReadInt32()
        if ($PeOffset -lt 0 -or $PeOffset + 6 -gt $Stream.Length) {
            throw "File has an invalid Windows PE header: $Path"
        }

        $Stream.Position = $PeOffset
        if ($Reader.ReadUInt32() -ne 0x00004550 -or $Reader.ReadUInt16() -ne 0x8664) {
            throw "File is not a Windows x64 PE executable: $Path"
        }
    }
    finally {
        $Stream.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($OutputArchive)) {
    $OutputArchive = Join-Path $RootDirectory "artifacts/dvmconsole-$Runtime.zip"
}

$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$OutputArchive = [IO.Path]::GetFullPath($OutputArchive)
$AllowMissingVocoder = $AllowMissingVocoder -or ($env:DVM_ALLOW_MISSING_VOCODER -eq "1")
if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
    throw "Publish directory does not exist: $PublishDirectory"
}

foreach ($FileName in @(
    "DvmConsole.Desktop.exe",
    "DvmConsole.Desktop.dll",
    "DvmConsole.Desktop.deps.json",
    "DvmConsole.Desktop.runtimeconfig.json"
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $FileName) -PathType Leaf)) {
        throw "Published output is missing required file: $FileName"
    }
}

$DocumentationOverview = Join-Path $PublishDirectory "Docs/Getting Started/01-Overview.md"
if (-not (Test-Path -LiteralPath $DocumentationOverview -PathType Leaf)) {
    throw "Published output is missing the in-app Markdown documentation: $DocumentationOverview"
}

foreach ($LegacyAlert in @("alert1.wav", "alert2.wav", "alert3.wav")) {
    $LegacyAlertPath = Join-Path $PublishDirectory "Audio/$LegacyAlert"
    if (Test-Path -LiteralPath $LegacyAlertPath) {
        throw "Published output contains obsolete generated-alert asset: $LegacyAlertPath"
    }
}

Assert-X64PeFile (Join-Path $PublishDirectory "DvmConsole.Desktop.exe")

if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory "libvocoder.dll") -PathType Leaf) -and -not $AllowMissingVocoder) {
    throw "Published output is missing libvocoder.dll. Set DVMVOCODER_LIBRARY before publishing or pass -AllowMissingVocoder for a UI-only artifact."
}
if (Test-Path -LiteralPath (Join-Path $PublishDirectory "libvocoder.dll") -PathType Leaf) {
    Assert-X64PeFile (Join-Path $PublishDirectory "libvocoder.dll")
}

$PrivateCodeplug = Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @("codeplug_testing.yml", "codeplug_testing.yaml") } |
    Select-Object -First 1
if ($null -ne $PrivateCodeplug) {
    throw "Publish contains the testing codeplug: $($PrivateCodeplug.FullName)"
}

$StagingDirectory = Join-Path ([IO.Path]::GetTempPath()) ("dvmconsole-package-" + [Guid]::NewGuid().ToString("N"))
$PackageDirectory = Join-Path $StagingDirectory "DVMConsole-$Runtime"
New-Item -ItemType Directory -Path $PackageDirectory -Force | Out-Null
try {
    Copy-Item -Path (Join-Path $PublishDirectory "*") -Destination $PackageDirectory -Recurse -Force
    $OutputParent = Split-Path -Parent $OutputArchive
    if (-not [string]::IsNullOrWhiteSpace($OutputParent)) {
        New-Item -ItemType Directory -Path $OutputParent -Force | Out-Null
    }
    if (Test-Path -LiteralPath $OutputArchive) {
        Remove-Item -LiteralPath $OutputArchive -Force
    }
    Compress-Archive -Path $PackageDirectory -DestinationPath $OutputArchive -CompressionLevel Optimal
}
finally {
    if (Test-Path -LiteralPath $StagingDirectory) {
        Remove-Item -LiteralPath $StagingDirectory -Recurse -Force
    }
}

Write-Host "Packaged unsigned $Runtime output to $OutputArchive"
