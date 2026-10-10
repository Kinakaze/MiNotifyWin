param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$RepositoryRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $RepositoryRoot 'artifacts\publish\win-x64' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
dotnet publish (Join-Path $RepositoryRoot 'src\MiPushDesk\MiPushDesk.csproj') -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
Write-Output (Join-Path $OutputDirectory 'MiPushDesk.exe')
