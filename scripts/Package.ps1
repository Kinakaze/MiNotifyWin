param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$RepositoryRoot = Split-Path $PSScriptRoot -Parent
if (-not $SkipPublish) { & (Join-Path $PSScriptRoot 'Publish.ps1') }
$PublishDirectory = Join-Path $RepositoryRoot 'artifacts\publish\win-x64'
$Project = [xml](Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\MiPushDesk\MiPushDesk.csproj') -Raw)
$Version = $Project.Project.PropertyGroup.Version
$ReleaseDirectory = Join-Path $RepositoryRoot "artifacts\releases\$Version"
New-Item -ItemType Directory -Path $ReleaseDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'README.md') -Destination $PublishDirectory
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'docs') -Destination $PublishDirectory -Recurse -Force
$Archive = Join-Path $ReleaseDirectory "MiNotifyWin-$Version-win-x64.zip"
if (Test-Path -LiteralPath $Archive) { Remove-Item -LiteralPath $Archive }
[System.IO.Compression.ZipFile]::CreateFromDirectory($PublishDirectory, $Archive, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$Hash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText((Join-Path $ReleaseDirectory 'SHA256SUMS.txt'), "$Hash  $([System.IO.Path]::GetFileName($Archive))`n", [System.Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $RepositoryRoot "docs\releases\$Version.md") -Destination (Join-Path $ReleaseDirectory 'RELEASE-NOTES.md')
Write-Output $ReleaseDirectory
