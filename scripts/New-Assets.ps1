$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$AssetDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\MiPushDesk\Assets'
New-Item -ItemType Directory -Path $AssetDirectory -Force | Out-Null
$IconFrames = [System.Collections.Generic.List[byte[]]]::new()
$IconSizes = @(16, 24, 32, 48, 64, 128, 256)
$IconFamily = [System.Drawing.FontFamily]::new('Segoe Fluent Icons')
foreach ($Size in $IconSizes) {
    $Bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    $Graphics = [System.Drawing.Graphics]::FromImage($Bitmap)
    $Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $Graphics.ScaleTransform($Size / 256.0, $Size / 256.0)
    $Graphics.Clear([System.Drawing.Color]::Transparent)
    $Ink = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#737373'))
    $Shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $Shape.AddString([string][char]0xECAA, $IconFamily, 0, 256, [System.Drawing.PointF]::Empty, [System.Drawing.StringFormat]::GenericTypographic)
    $Bounds = $Shape.GetBounds()
    $Scale = 220 / [Math]::Max($Bounds.Width, $Bounds.Height)
    $Transform = [System.Drawing.Drawing2D.Matrix]::new($Scale, 0, 0, $Scale,
        (256 - $Bounds.Width * $Scale) / 2 - $Bounds.X * $Scale,
        (256 - $Bounds.Height * $Scale) / 2 - $Bounds.Y * $Scale)
    $Shape.Transform($Transform)
    $Graphics.FillPath($Ink, $Shape)
    $Memory = [System.IO.MemoryStream]::new()
    $Bitmap.Save($Memory, [System.Drawing.Imaging.ImageFormat]::Png)
    $IconFrames.Add($Memory.ToArray())
    if ($Size -eq 256) { $Bitmap.Save((Join-Path $AssetDirectory 'MiPushDesk.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $Memory.Dispose(); $Transform.Dispose(); $Shape.Dispose(); $Ink.Dispose(); $Graphics.Dispose(); $Bitmap.Dispose()
}
$IconFamily.Dispose()
$Output = [System.IO.File]::Create((Join-Path $AssetDirectory 'MiPushDesk.ico'))
$Writer = [System.IO.BinaryWriter]::new($Output)
$Writer.Write([uint16]0); $Writer.Write([uint16]1); $Writer.Write([uint16]$IconSizes.Length)
$Offset = 6 + 16 * $IconSizes.Length
for ($Index = 0; $Index -lt $IconSizes.Length; $Index++) {
    $Dimension = if ($IconSizes[$Index] -eq 256) { 0 } else { $IconSizes[$Index] }
    $Writer.Write([byte]$Dimension); $Writer.Write([byte]$Dimension); $Writer.Write([byte]0); $Writer.Write([byte]0)
    $Writer.Write([uint16]1); $Writer.Write([uint16]32); $Writer.Write([uint32]$IconFrames[$Index].Length); $Writer.Write([uint32]$Offset)
    $Offset += $IconFrames[$Index].Length
}
foreach ($Frame in $IconFrames) { $Writer.Write($Frame) }
$Writer.Dispose(); $Output.Dispose()
$Preview = [System.Drawing.Bitmap]::new(1000, 330)
$Canvas = [System.Drawing.Graphics]::FromImage($Preview)
$Canvas.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$Gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.Rectangle]::new(0, 0, 1000, 330), [System.Drawing.ColorTranslator]::FromHtml('#758E9E'), [System.Drawing.ColorTranslator]::FromHtml('#D0C6BB'), 15)
$Canvas.FillRectangle($Gradient, 0, 0, 1000, 330)
$Sun = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#EEE2CF'))
$Canvas.FillEllipse($Sun, 740, 40, 116, 116)
$Mountains = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#536E78'))
$Points = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(0, 290), [System.Drawing.PointF]::new(230, 130), [System.Drawing.PointF]::new(430, 268), [System.Drawing.PointF]::new(650, 100), [System.Drawing.PointF]::new(1000, 300), [System.Drawing.PointF]::new(1000, 330), [System.Drawing.PointF]::new(0, 330))
$Canvas.FillPolygon($Mountains, $Points)
$Preview.Save((Join-Path $AssetDirectory 'preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$Mountains.Dispose(); $Sun.Dispose(); $Gradient.Dispose(); $Canvas.Dispose(); $Preview.Dispose()
Write-Output 'Application icon and preview artwork generated.'
