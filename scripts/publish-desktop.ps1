[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string] $Runtime = "win-x64",

    [string] $OutputDirectory,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $VocoderLibrary = $env:DVMVOCODER_LIBRARY,

    [switch] $AllowMissingVocoder
)

$ErrorActionPreference = "Stop"
$RootDirectory = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RootDirectory "src/DvmConsole.Desktop/DvmConsole.Desktop.csproj"

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

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RootDirectory "artifacts/$Runtime"
}

if ($env:DVM_ALLOW_MISSING_VOCODER -eq "1") {
    $AllowMissingVocoder = $true
}

$ExistingVocoder = Join-Path $OutputDirectory "libvocoder.dll"
if (Test-Path -LiteralPath $ExistingVocoder -PathType Leaf) {
    Remove-Item -LiteralPath $ExistingVocoder -Force
}
foreach ($LegacyAlert in @("alert1.wav", "alert2.wav", "alert3.wav")) {
    $LegacyAlertPath = Join-Path $OutputDirectory "Audio/$LegacyAlert"
    if (Test-Path -LiteralPath $LegacyAlertPath -PathType Leaf) {
        Remove-Item -LiteralPath $LegacyAlertPath -Force
    }
}

dotnet restore $Project --runtime $Runtime --ignore-failed-sources -p:NuGetAudit=false --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

dotnet publish $Project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --no-restore `
    --output $OutputDirectory `
    /p:UseAppHost=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (-not [string]::IsNullOrWhiteSpace($VocoderLibrary)) {
    if (-not (Test-Path -LiteralPath $VocoderLibrary -PathType Leaf)) {
        throw "DVMVOCODER_LIBRARY does not point to a file: $VocoderLibrary"
    }

    Copy-Item -LiteralPath $VocoderLibrary -Destination (Join-Path $OutputDirectory "libvocoder.dll") -Force
} elseif (-not $AllowMissingVocoder) {
    throw "DVMVOCODER_LIBRARY is required for a working digital-voice package. Build the native vocoder, set DVMVOCODER_LIBRARY, or pass -AllowMissingVocoder for a UI-only artifact."
}

$RequiredFiles = @(
    "DvmConsole.Desktop.exe",
    "DvmConsole.Desktop.dll",
    "DvmConsole.Desktop.deps.json",
    "DvmConsole.Desktop.runtimeconfig.json"
)
foreach ($FileName in $RequiredFiles) {
    $Path = Join-Path $OutputDirectory $FileName
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Published output is missing required file: $Path"
    }
}

foreach ($LegacyAlert in @("alert1.wav", "alert2.wav", "alert3.wav")) {
    $LegacyAlertPath = Join-Path $OutputDirectory "Audio/$LegacyAlert"
    if (Test-Path -LiteralPath $LegacyAlertPath) {
        throw "Published output contains obsolete generated-alert asset: $LegacyAlertPath"
    }
}

Assert-X64PeFile (Join-Path $OutputDirectory "DvmConsole.Desktop.exe")
if (Test-Path -LiteralPath (Join-Path $OutputDirectory "libvocoder.dll") -PathType Leaf) {
    Assert-X64PeFile (Join-Path $OutputDirectory "libvocoder.dll")
}

$PrivateCodeplug = Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @("codeplug_testing.yml", "codeplug_testing.yaml") } |
    Select-Object -First 1
if ($null -ne $PrivateCodeplug) {
    throw "Published output contains the private testing codeplug: $($PrivateCodeplug.FullName)"
}

Write-Host "Published $Runtime to $OutputDirectory"
if ([string]::IsNullOrWhiteSpace($VocoderLibrary)) {
    Write-Warning "No native vocoder was copied. This is a UI-only artifact; DMR/P25 voice is unavailable."
}
