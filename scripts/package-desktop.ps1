[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string] $Runtime = "win-x64",

    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,

    [string] $OutputArchive
)

$ErrorActionPreference = "Stop"
$RootDirectory = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($OutputArchive)) {
    $OutputArchive = Join-Path $RootDirectory "artifacts/dvmconsole-$Runtime.zip"
}

$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$OutputArchive = [IO.Path]::GetFullPath($OutputArchive)
if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
    throw "Publish directory does not exist: $PublishDirectory"
}

foreach ($FileName in @(
    "DvmConsole.Desktop.dll",
    "DvmConsole.Desktop.deps.json",
    "DvmConsole.Desktop.runtimeconfig.json"
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $FileName) -PathType Leaf)) {
        throw "Published output is missing required file: $FileName"
    }
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
