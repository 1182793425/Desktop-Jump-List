# Generates src/DesktopJumpList/Assets/app.ico - a plain white circle.
#
# No gradients, no accent colour: a white disc with a thin neutral outline so it
# stays visible on both light and dark taskbars. The dock uses the same circle.
#
# Usage: pwsh ./tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\DesktopJumpList\Assets\app.ico'
$preview = Join-Path $PSScriptRoot '..\artifacts\app-icon-preview.png'
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $preview) | Out-Null

function New-Frame([int]$size) {
    # Draw at 4x and downsample for clean edges at 16/20/24 px.
    $bigSize = $size * 4
    $big = [System.Drawing.Bitmap]::new($bigSize, $bigSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $margin = [single]($bigSize * 0.08)
    $diameter = [single]($bigSize - 2 * $margin)
    $fill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $g.FillEllipse($fill, $margin, $margin, $diameter, $diameter)
    $fill.Dispose()

    # Keep the white disc legible on a white background: a hairline neutral rim.
    $stroke = [single](4 * [Math]::Max(1.0, $size * 0.012))
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(44, 0, 0, 0), $stroke)
    $g.DrawEllipse($pen, $margin, $margin, $diameter, $diameter)
    $pen.Dispose()
    $g.Dispose()

    $frame = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $fg = [System.Drawing.Graphics]::FromImage($frame)
    $fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $fg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $fg.Clear([System.Drawing.Color]::Transparent)
    $fg.DrawImage($big, 0, 0, $size, $size)
    $fg.Dispose()
    $big.Dispose()
    return $frame
}

# Preview: the same mark on a dark and a light surface.
$sheet = [System.Drawing.Bitmap]::new(700, 300, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.Clear([System.Drawing.Color]::FromArgb(255, 30, 32, 38))
$darkRect = [System.Drawing.Rectangle]::new(0, 0, 350, 300)
$lightBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 243, 244, 246))
$sg.FillRectangle($lightBrush, 350, 0, 350, 300)
$lightBrush.Dispose()
$large = New-Frame 256
$sg.DrawImage($large, 20, 20, 256, 256)
$sg.DrawImage($large, 374, 20, 256, 256)
$large.Dispose()
$px = 470
foreach ($s in 64, 48, 32, 24, 16) {
    $f = New-Frame $s
    $sg.DrawImage($f, $px, 220, $s, $s)
    $px += $s + 12
    $f.Dispose()
}
$sg.Dispose()
$sheet.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Frame $s
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$fs = [System.IO.File]::Create($out)
$w = [System.IO.BinaryWriter]::new($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Dispose()
Write-Host "Wrote $out"
Write-Host "Preview $preview"
