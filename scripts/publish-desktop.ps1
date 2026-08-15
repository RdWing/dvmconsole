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

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RootDirectory "artifacts/$Runtime"
}

if ($env:DVM_ALLOW_MISSING_VOCODER -eq "1") {
    $AllowMissingVocoder = $true
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
