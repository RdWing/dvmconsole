# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string] $Runtime = "win-x64",

    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory
)

$ErrorActionPreference = "Stop"
$PythonCommand = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $PythonCommand) {
    $PythonCommand = Get-Command python3 -ErrorAction SilentlyContinue
}
if ($null -eq $PythonCommand) {
    throw "Python 3 is required to verify packaged documentation."
}
$Python = $PythonCommand.Source

function Assert-PeArchitecture([string] $Path, [UInt16] $ExpectedMachine, [string] $Architecture) {
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
        if ($Reader.ReadUInt32() -ne 0x00004550 -or $Reader.ReadUInt16() -ne $ExpectedMachine) {
            throw "File is not a Windows $Architecture PE executable: $Path"
        }
    }
    finally {
        $Stream.Dispose()
    }
}

$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
& $Python (Join-Path $PSScriptRoot "verify-package.py") `
    --publish-only --publish-root $PublishDirectory --rid $Runtime
if ($LASTEXITCODE -ne 0) {
    throw "Publish inventory validation failed with exit code $LASTEXITCODE."
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

$ExpectedMachine = if ($Runtime -eq "win-arm64") { [UInt16]0xAA64 } else { [UInt16]0x8664 }
$ExpectedArchitecture = if ($Runtime -eq "win-arm64") { "ARM64" } else { "x64" }
Assert-PeArchitecture (Join-Path $PublishDirectory "DvmConsole.exe") $ExpectedMachine $ExpectedArchitecture
$PublishFiles = @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -ErrorAction Stop)
$NativeWindowsFiles = $PublishFiles | Where-Object { $_.Extension -in @(".exe", ".dll") }
foreach ($NativeWindowsFile in $NativeWindowsFiles) {
    Assert-PeArchitecture $NativeWindowsFile.FullName $ExpectedMachine $ExpectedArchitecture
}
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

Write-Host "Publish verification passed: $PublishDirectory ($Runtime)"
