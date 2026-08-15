[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string] $Runtime = "win-x64",

    [string] $OutputDirectory,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $VocoderLibrary = $env:DVMVOCODER_LIBRARY
)

$ErrorActionPreference = "Stop"
$RootDirectory = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RootDirectory "src/DvmConsole.Desktop/DvmConsole.Desktop.csproj"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RootDirectory "artifacts/$Runtime"
}

dotnet restore $Project --runtime $Runtime --ignore-failed-sources -p:NuGetAudit=false --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

dotnet publish $Project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained false `
    --no-restore `
    --output $OutputDirectory `
    /p:UseAppHost=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (-not [string]::IsNullOrWhiteSpace($VocoderLibrary)) {
    if (-not (Test-Path -LiteralPath $VocoderLibrary -PathType Leaf)) {
        throw "DVMVOCODER_LIBRARY does not point to a file: $VocoderLibrary"
    }

    Copy-Item -LiteralPath $VocoderLibrary -Destination (Join-Path $OutputDirectory "libvocoder.dll") -Force
}

$RequiredFiles = @(
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
    Write-Warning "No native vocoder was copied. The UI will run, but DMR/P25 voice requires libvocoder.dll beside the application."
}
