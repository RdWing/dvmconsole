[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string] $Runtime = "win-x64",

    [string] $OutputDirectory,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$RootDirectory = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RootDirectory "src/DvmConsole.Desktop/DvmConsole.Desktop.csproj"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RootDirectory "artifacts/$Runtime"
}

foreach ($ExistingVocoderName in @("libvocoder.dll", "dvmconsole_vocoder.dll")) {
    $ExistingVocoder = Join-Path $OutputDirectory $ExistingVocoderName
    if (Test-Path -LiteralPath $ExistingVocoder -PathType Leaf) {
        Remove-Item -LiteralPath $ExistingVocoder -Force
    }
}
foreach ($LegacyAlert in @("alert1.wav", "alert2.wav", "alert3.wav")) {
    $LegacyAlertPath = Join-Path $OutputDirectory "Audio/$LegacyAlert"
    if (Test-Path -LiteralPath $LegacyAlertPath -PathType Leaf) {
        Remove-Item -LiteralPath $LegacyAlertPath -Force
    }
}

dotnet restore $Project `
    --runtime $Runtime `
    --force-evaluate `
    --ignore-failed-sources `
    -p:Configuration=$Configuration `
    -p:DvmConsoleTargetPlatform=windows `
    -p:PublishTrimmed=true `
    -p:TrimMode=partial `
    -p:NuGetAudit=false `
    --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

$PublishProperties = @(
    "-p:UseAppHost=true",
    "-p:NativeVocoderTarget=x86_64-pc-windows-msvc",
    "-p:DvmConsoleTargetPlatform=windows",
    "-p:DebugType=None",
    "-p:PublishTrimmed=true",
    "-p:TrimMode=partial"
)
$PublishProperties += @(
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true"
)

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

Write-Host "Published $Runtime to $OutputDirectory"
