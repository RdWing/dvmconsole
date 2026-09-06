# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $FilePath,

    [Parameter(Mandatory = $true)]
    [string[]] $ArgumentList,

    [Parameter(Mandatory = $true)]
    [string] $ResultPath,

    [Parameter(Mandatory = $true)]
    [string] $LogPath,

    [ValidateRange(60, 120)]
    [int] $TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$standardOutput = "$LogPath.stdout"
$standardError = "$LogPath.stderr"
Remove-Item -LiteralPath $ResultPath, $LogPath, $standardOutput, $standardError -Force -ErrorAction SilentlyContinue

$process = Start-Process `
    -FilePath $FilePath `
    -ArgumentList $ArgumentList `
    -NoNewWindow `
    -PassThru `
    -RedirectStandardOutput $standardOutput `
    -RedirectStandardError $standardError

try {
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        & taskkill.exe /PID $process.Id /T /F | Out-Null
        throw "Desktop smoke exceeded the $TimeoutSeconds second watchdog."
    }
}
finally {
    Get-Content -LiteralPath $standardOutput, $standardError -ErrorAction SilentlyContinue |
        Set-Content -LiteralPath $LogPath
    Remove-Item -LiteralPath $standardOutput, $standardError -Force -ErrorAction SilentlyContinue
}

if ($process.ExitCode -ne 0) {
    throw "Desktop smoke failed with exit code $($process.ExitCode). See $LogPath."
}
if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {
    throw "Desktop smoke did not write a result. See $LogPath."
}
$status = Get-Content -LiteralPath $ResultPath -TotalCount 1
if ($status -ne "PASS") {
    throw "Desktop smoke did not report PASS. See $LogPath."
}

Write-Host "Desktop window smoke passed: $FilePath"
