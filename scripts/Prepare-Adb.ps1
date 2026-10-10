param()
$ErrorActionPreference = 'Stop'
$RepositoryRoot = Split-Path $PSScriptRoot -Parent
$Version = '37.0.1'
$DownloadDirectory = Join-Path $RepositoryRoot 'artifacts\downloads'
$Archive = Join-Path $DownloadDirectory "platform-tools_r$Version-win.zip"
New-Item -ItemType Directory -Path $DownloadDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $Archive)) {
    Invoke-WebRequest -Uri "https://dl.google.com/android/repository/platform-tools_r$Version-win.zip" -OutFile $Archive -TimeoutSec 60
}
if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne '45F4D63113E895EBDE0C90F194099A4676B6AC653BD28D54314A9E022BBC1A99') {
    throw 'ADB download checksum mismatch.'
}
$Destination = Join-Path $RepositoryRoot 'artifacts\dependencies\adb'
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$Zip = [System.IO.Compression.ZipFile]::OpenRead($Archive)
try {
    foreach ($Name in @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'NOTICE.txt', 'source.properties')) {
        $Entry = $Zip.GetEntry("platform-tools/$Name")
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($Entry, (Join-Path $Destination $Name), $true)
    }
} finally { $Zip.Dispose() }
Write-Output "ADB $Version prepared."
