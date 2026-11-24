# Generates src/DesktopJumpList/Assets/app.ico - the Jump app mark.
#
# Design: a deep blue -> violet squircle holding a 2x2 grid of white app tiles.
# The top-right tile lifts off its slot ("jump"), which is what the dock ball and
# the tray icon show too, so the brand reads the same at 16 px and 256 px.
#
# Usage: pwsh ./tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\DesktopJumpList\Assets\app.ico'
$preview = Join-Path $PSScriptRoot '..\artifacts\app-icon-preview.png'
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $preview) | Out-Null

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Draws the mark on a 256x256 virtual canvas, scaled to $canvas pixels.
function New-Mark([int]$canvas) {
    $bmp = [System.Drawing.Bitmap]::new($canvas, $canvas, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($canvas / 256.0, $canvas / 256.0)

    $pad = 8.0
    $side = 256.0 - 2 * $pad
    $bg = New-RoundRect $pad $pad $side $side 56
    $rect = [System.Drawing.RectangleF]::new($pad, $pad, $side, $side)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect,
        [System.Drawing.Color]::FromArgb(255, 0x3D, 0x6B, 0xFF),
        [System.Drawing.Color]::FromArgb(255, 0x7C, 0x3A, 0xED), 52.0)
    $g.FillPath($brush, $bg)

    # Top-left light source: makes the flat gradient feel like a physical tile.
    $glowRect = New-RoundRect $pad $pad $side 150 54
    $glow = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.RectangleF]::new($pad, $pad, $side, 150),
        [System.Drawing.Color]::FromArgb(44, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255), 90.0)
    $g.FillPath($glow, $glowRect)

    # A touch of depth along the bottom edge.
    $shadeRect = New-RoundRect $pad 150 $side 98 56
    $shade = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.RectangleF]::new($pad, 150, $side, 98),
        [System.Drawing.Color]::FromArgb(0, 18, 8, 52),
        [System.Drawing.Color]::FromArgb(34, 18, 8, 52), 90.0)
    $g.FillPath($shade, $shadeRect)

    $edge = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(34, 255, 255, 255), 2.0)
    $g.DrawPath($edge, $bg)

    # 2x2 app tiles; the top-right one has jumped out of the grid.
    $cell = 62.0
    $gap = 12.0
    $ox = 128.0 - ($cell * 2 + $gap) / 2
    $oy = 128.0 - ($cell * 2 + $gap) / 2
    $radius = $cell * 0.30
    for ($row = 0; $row -lt 2; $row++) {
        for ($col = 0; $col -lt 2; $col++) {
            $x = $ox + $col * ($cell + $gap)
            $y = $oy + $row * ($cell + $gap)
            $leap = ($row -eq 0 -and $col -eq 1)
            if ($leap) { $x += 5; $y -= 5 }
            if ($leap) {
                # Soft contact shadow under the lifted tile.
                $shadow = New-RoundRect ($x + 1) ($y + 8) $cell $cell $radius
                $shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(52, 18, 8, 52))
                $g.FillPath($shadowBrush, $shadow)
                $shadowBrush.Dispose()
                $g.FillPath([System.Drawing.Brushes]::White, (New-RoundRect $x $y $cell $cell $radius))
            } else {
                $tile = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(228, 255, 255, 255))
                $g.FillPath($tile, (New-RoundRect $x $y $cell $cell $radius))
                $tile.Dispose()
            }
        }
    }

    $g.Dispose()
    return $bmp
}

function New-Frame([int]$size) {
    # Render 4x and downsample: tiny 16/20/24 px frames stay crisp and evenly weighted.
    $big = New-Mark ($size * 4)
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($big, 0, 0, $size, $size)
    $g.Dispose()
    $big.Dispose()
    return $bmp
}

# Large preview next to real taskbar-sized versions, on a neutral background.
$previewBmp = [System.Drawing.Bitmap]::new(560, 300, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$pg = [System.Drawing.Graphics]::FromImage($previewBmp)
$pg.Clear([System.Drawing.Color]::FromArgb(255, 30, 32, 38))
$pg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$large = New-Frame 256
$pg.DrawImage($large, 20, 20, 256, 256)
$large.Dispose()
$px = 310
foreach ($s in 64, 48, 32, 24, 16) {
    $f = New-Frame $s
    $pg.DrawImage($f, $px, 40, $s, $s)
    $px += $s + 14
    $f.Dispose()
}
$pg.Dispose()
$previewBmp.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
$previewBmp.Dispose()

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
