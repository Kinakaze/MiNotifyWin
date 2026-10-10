param([string]$Executable)
$ErrorActionPreference = 'Stop'
$RepositoryRoot = Split-Path $PSScriptRoot -Parent
if (-not $Executable) { $Executable = Join-Path $RepositoryRoot 'artifacts\publish\win-x64\MiPushDesk.exe' }
$CheckDirectory = Join-Path $RepositoryRoot ('artifacts\checks\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$PreviousData = $env:MIPUSHDESK_DATA_DIR
$PreviousRender = $env:MIPUSHDESK_RENDER_DIR
try {
    $env:MIPUSHDESK_DATA_DIR = Join-Path $CheckDirectory 'data'
    $env:MIPUSHDESK_RENDER_DIR = $CheckDirectory
    $Process = Start-Process -FilePath $Executable -WindowStyle Hidden -PassThru
} finally {
    $env:MIPUSHDESK_DATA_DIR = $PreviousData
    $env:MIPUSHDESK_RENDER_DIR = $PreviousRender
}
if (-not $Process.WaitForExit(60000)) { throw "Interface check is still running (PID $($Process.Id)): $CheckDirectory" }
$Result = Get-Content -LiteralPath (Join-Path $CheckDirectory 'checks.json') -Raw | ConvertFrom-Json
if (-not $Result.succeeded) { throw $Result.error }
Write-Output "Interface checks passed ($($Result.captures.Count) captures): $CheckDirectory"
