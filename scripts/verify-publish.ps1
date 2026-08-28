[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string] $Runtime = "win-x64",

    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory
)

$ErrorActionPreference = "Stop"

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

$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
    throw "Publish directory does not exist: $PublishDirectory"
}

$PublishFiles = @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -ErrorAction Stop)
$PublishBytes = ($PublishFiles | Measure-Object -Property Length -Sum).Sum
$MaximumPublishBytes = 180MB
$MaximumPublishFiles = 250
if ($PublishBytes -gt $MaximumPublishBytes) {
    throw "Publish exceeds the $MaximumPublishBytes byte size budget: $PublishBytes bytes."
}
if ($PublishFiles.Count -gt $MaximumPublishFiles) {
    throw "Publish exceeds the $MaximumPublishFiles file budget: $($PublishFiles.Count) files."
}

foreach ($FileName in @("DvmConsole.exe", "LICENSE")) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $FileName) -PathType Leaf)) {
        throw "Published output is missing required file: $FileName"
    }
}

if (Test-Path -LiteralPath (Join-Path $PublishDirectory "Docs")) {
    throw "Publish contains the obsolete Docs directory; documentation pages belong under Documentation."
}

$RequiredDocumentationFiles = @(
    "Getting Started/01-Overview.md",
    "Getting Started/02-Building.md",
    "Getting Started/03-Configurations/01-Codeplug Creation.md",
    "Getting Started/03-Configurations/02-Encryption Keys.md",
    "Getting Started/03-Configurations/03-RID Aliases.md",
    "Getting Started/03-Configurations/04-Groups and Patching.md",
    "Getting Started/03-Configurations/05-Talkgroup Audio Recorder.md",
    "Getting Started/04-Operations/01-Console Operation.md",
    "Getting Started/04-Operations/02-Settings Reference.md",
    "Getting Started/04-Operations/03-Audio Settings.md",
    "Getting Started/04-Operations/04-Alert Tones.md"
)
foreach ($RelativeDocument in $RequiredDocumentationFiles) {
    $DocumentPath = Join-Path (Join-Path $PublishDirectory "Documentation") $RelativeDocument
    if (-not (Test-Path -LiteralPath $DocumentPath -PathType Leaf)) {
        throw "Published output is missing documentation: $RelativeDocument"
    }
}

$DemoCodeplug = [IO.Path]::GetFullPath((Join-Path $PublishDirectory "Demo/codeplug.yml"))
$ExpectedDemoCodeplug = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../configs/codeplug.demo.yml"))
if (-not (Test-Path -LiteralPath $DemoCodeplug -PathType Leaf) -or
    (Get-FileHash -LiteralPath $DemoCodeplug -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $ExpectedDemoCodeplug -Algorithm SHA256).Hash) {
    throw "Published output is missing the exact sanitized network-disabled demonstration codeplug."
}

$DiagnosticsAssembly = Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "AvaloniaUI.DiagnosticsSupport*" } |
    Select-Object -First 1
if ($null -ne $DiagnosticsAssembly) {
    throw "Publish contains the Debug-only Avalonia diagnostics package: $($DiagnosticsAssembly.FullName)"
}

foreach ($LegacyAlert in @("alert1.wav", "alert2.wav", "alert3.wav")) {
    $LegacyAlertPath = Join-Path $PublishDirectory "Audio/$LegacyAlert"
    if (Test-Path -LiteralPath $LegacyAlertPath) {
        throw "Published output contains obsolete generated-alert asset: $LegacyAlertPath"
    }
}

Assert-X64PeFile (Join-Path $PublishDirectory "DvmConsole.exe")
foreach ($UnexpectedFile in @(
    "DvmConsole.dll",
    "DvmConsole.deps.json",
    "DvmConsole.runtimeconfig.json",
    "libdvmaudio.dylib",
    "libvocoder.dll",
    "dvmconsole_vocoder.dll"
)) {
    if (Test-Path -LiteralPath (Join-Path $PublishDirectory $UnexpectedFile)) {
        throw "Windows single-file publish contains an unexpected sidecar: $UnexpectedFile"
    }
}

$PrivateCodeplug = Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @("codeplug_testing.yml", "codeplug_testing.yaml") } |
    Select-Object -First 1
if ($null -ne $PrivateCodeplug) {
    throw "Published output contains the private testing codeplug: $($PrivateCodeplug.FullName)"
}

$TextFilePaths = @(
    Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Extension -in @(".json", ".yml", ".yaml", ".config", ".txt") -and
            $_.FullName -ne $DemoCodeplug
        } |
        Select-Object -ExpandProperty FullName
)
if ($TextFilePaths.Count -gt 0 -and
    (Select-String -Path $TextFilePaths -Pattern '10\.10\.10\.55|preshared|authPassword|password' -Quiet)) {
    throw "Publish contains credential-like or test-endpoint material."
}

Write-Host "Publish verification passed: $PublishDirectory ($Runtime, $PublishBytes bytes, $($PublishFiles.Count) files)"
