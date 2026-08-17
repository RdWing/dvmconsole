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

dotnet restore $Project --runtime $Runtime --ignore-failed-sources -p:NuGetAudit=false --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

$PublishProperties = @(
    "-p:UseAppHost=true",
    "-p:NativeVocoderTarget=x86_64-pc-windows-msvc"
)
$PublishProperties += @(
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=None"
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

$RequiredFiles = @(
    "DvmConsole.exe"
)
foreach ($FileName in $RequiredFiles) {
    $Path = Join-Path $OutputDirectory $FileName
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Published output is missing required file: $Path"
    }
}

if (Test-Path -LiteralPath (Join-Path $OutputDirectory "Docs")) {
    throw "Published output must not contain documentation; the app reads current pages from GitHub."
}

foreach ($LegacyAlert in @("alert1.wav", "alert2.wav", "alert3.wav")) {
    $LegacyAlertPath = Join-Path $OutputDirectory "Audio/$LegacyAlert"
    if (Test-Path -LiteralPath $LegacyAlertPath) {
        throw "Published output contains obsolete generated-alert asset: $LegacyAlertPath"
    }
}

Assert-X64PeFile (Join-Path $OutputDirectory "DvmConsole.exe")
foreach ($SidecarName in @("libvocoder.dll", "dvmconsole_vocoder.dll")) {
    if (Test-Path -LiteralPath (Join-Path $OutputDirectory $SidecarName) -PathType Leaf) {
        throw "The vocoder must be embedded in DvmConsole.exe, not shipped as $SidecarName."
    }
}

$PrivateCodeplug = Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @("codeplug_testing.yml", "codeplug_testing.yaml") } |
    Select-Object -First 1
if ($null -ne $PrivateCodeplug) {
    throw "Published output contains the private testing codeplug: $($PrivateCodeplug.FullName)"
}

Write-Host "Published $Runtime to $OutputDirectory"
