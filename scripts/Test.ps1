param([switch]$Native)
$ErrorActionPreference = 'Stop'
$RepositoryRoot = Split-Path $PSScriptRoot -Parent
dotnet run --project (Join-Path $RepositoryRoot 'tests\MiPushDesk.Checks\MiPushDesk.Checks.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
if ($Native) {
    dotnet run --project (Join-Path $RepositoryRoot 'tests\MiPushDesk.NativeChecks\MiPushDesk.NativeChecks.csproj') -c Release -- --toast
    if ($LASTEXITCODE -ne 0) { throw 'Native checks failed.' }
}
