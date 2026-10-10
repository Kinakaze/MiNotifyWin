param([string]$Revision = '9b9d749221bec6bf5bc4bd91e673bbae97d8550e')
$ErrorActionPreference = 'Stop'
$ProjectDirectory = Split-Path $PSScriptRoot -Parent
$DownloadDirectory = Join-Path $ProjectDirectory 'artifacts\icon-source'
$AssetDirectory = Join-Path $ProjectDirectory 'src\MiPushDesk\Assets\AppIcons'
New-Item -ItemType Directory -Path $DownloadDirectory -Force | Out-Null
$SourceFile = Join-Path $DownloadDirectory ("delta-$Revision.zip")
if (-not (Test-Path -LiteralPath $SourceFile)) {
    Invoke-WebRequest -Uri "https://codeload.github.com/Delta-Icons/android/zip/$Revision" -OutFile $SourceFile -TimeoutSec 300
}
$Source = [System.IO.Compression.ZipFile]::OpenRead($SourceFile)
$PackFile = Join-Path $AssetDirectory 'delta.zip'
$TemporaryPack = Join-Path $DownloadDirectory 'delta-pack.tmp.zip'
if (Test-Path -LiteralPath $TemporaryPack) { Remove-Item -LiteralPath $TemporaryPack }
$Pack = [System.IO.Compression.ZipFile]::Open($TemporaryPack, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $Images = @($Source.Entries | Where-Object { $_.FullName -match '/app/src/main/res/drawable-nodpi/[^/]+\.png$' })
    $FilterEntry = $Source.Entries | Where-Object { $_.FullName.EndsWith('/app/src/main/res/xml/appfilter.xml') } | Select-Object -First 1
    $Reader = [System.IO.StreamReader]::new($FilterEntry.Open())
    try { [xml]$Filter = $Reader.ReadToEnd() } finally { $Reader.Dispose() }
    $Available = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($Entry in $Images) {
        $FileName = [System.IO.Path]::GetFileName($Entry.FullName)
        $Available.Add([System.IO.Path]::GetFileNameWithoutExtension($FileName)) | Out-Null
        $Output = $Pack.CreateEntry("icons/$FileName", [System.IO.Compression.CompressionLevel]::Optimal).Open()
        $InputStream = $Entry.Open()
        try { $InputStream.CopyTo($Output) } finally { $InputStream.Dispose(); $Output.Dispose() }
    }
    $Mappings = [ordered]@{}
    foreach ($Item in $Filter.resources.item) {
        if ($Item.component -match '^ComponentInfo\{([^/{}]+)/[^{}]+\}$' -and $Available.Contains($Item.drawable)) {
            $Package = $Matches[1]
            if (-not $Mappings.Contains($Package)) { $Mappings[$Package] = "icons/$($Item.drawable).png" }
        }
    }
    $Common = Get-Content -LiteralPath (Join-Path $AssetDirectory 'common-apps.json') -Raw | ConvertFrom-Json
    foreach ($Alias in $Common.aliases.PSObject.Properties) {
        if ($Mappings.Contains($Alias.Value)) { $Mappings[$Alias.Name] = $Mappings[$Alias.Value] }
    }
    $CommonCache = Join-Path $DownloadDirectory 'common'
    New-Item -ItemType Directory -Path $CommonCache -Force | Out-Null
    $Missing = [System.Collections.Generic.List[string]]::new()
    $Supplemented = 0
    $Sources = [ordered]@{}
    foreach ($Package in $Common.packages | Sort-Object -Unique) {
        if ($Mappings.Contains($Package)) { continue }
        $CachedFile = Join-Path $CommonCache ($Package + '.bin')
        $ExplicitImage = $Common.images.PSObject.Properties[$Package].Value
        $SourceUrl = if ($ExplicitImage) { $ExplicitImage.source } else { 'https://app.mi.com/details?id=' + $Package }
        try {
            if (-not (Test-Path -LiteralPath $CachedFile)) {
                if ($ExplicitImage) { $ImageUrl = $ExplicitImage.url }
                else {
                    $Page = Invoke-WebRequest -Uri $SourceUrl -TimeoutSec 12
                    $Section = [regex]::Match($Page.Content, '<div\b[^>]*class=["'']app-info["''][^>]*>([\s\S]*)').Groups[1].Value
                    $Image = [regex]::Match($Section, '<img\b[^>]*>').Value
                    $ImageUrl = [Net.WebUtility]::HtmlDecode([regex]::Match($Image, '\bsrc=["'']([^"'']+)["'']').Groups[1].Value)
                }
                if ($ImageUrl.StartsWith('http://')) { $ImageUrl = 'https://' + $ImageUrl.Substring(7) }
                if (-not $ImageUrl.StartsWith('https://')) { throw 'No application icon.' }
                Invoke-WebRequest -Uri $ImageUrl -OutFile $CachedFile -TimeoutSec 12
            }
            $Bytes = [IO.File]::ReadAllBytes($CachedFile)
            $Extension = if ($Bytes.Length -ge 24 -and $Bytes[0] -eq 137 -and $Bytes[1] -eq 80) { '.png' }
                elseif ($Bytes.Length -ge 4 -and $Bytes[0] -eq 255 -and $Bytes[1] -eq 216) { '.jpg' } else { throw 'Unsupported application icon.' }
            $Name = 'common/' + $Package + $Extension
            $Output = $Pack.CreateEntry($Name, [IO.Compression.CompressionLevel]::Optimal).Open()
            try { $Output.Write($Bytes) } finally { $Output.Dispose() }
            $Mappings[$Package] = $Name
            $Sources[$Package] = $SourceUrl
            $Supplemented++
        } catch { $Missing.Add($Package) }
    }
    $SourcesStream = $Pack.CreateEntry('common-sources.json').Open()
    try { $SourcesStream.Write([Text.Encoding]::UTF8.GetBytes(($Sources | ConvertTo-Json -Compress))) } finally { $SourcesStream.Dispose() }
    $Index = @{ source = 'Delta Icons'; revision = $Revision; icons = $Mappings } | ConvertTo-Json -Depth 3 -Compress
    $IndexStream = $Pack.CreateEntry('index.json').Open()
    try { $IndexStream.Write([Text.Encoding]::UTF8.GetBytes($Index)) } finally { $IndexStream.Dispose() }
    foreach ($Name in @('LICENSE.md', 'NOTICE.md')) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($Pack, (Join-Path $AssetDirectory $Name), $Name) | Out-Null
    }
} finally { $Pack.Dispose(); $Source.Dispose() }
Copy-Item -LiteralPath $TemporaryPack -Destination $PackFile -Force
[pscustomobject]@{ PackageCount=$Mappings.Count; IconCount=$Images.Count; Supplemented=$Supplemented; Missing=$Missing; Bytes=(Get-Item -LiteralPath $PackFile).Length; Revision=$Revision } | ConvertTo-Json
