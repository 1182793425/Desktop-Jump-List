# Generates src/DesktopJumpList/Assets/app.ico — a light rounded tile with a blue
# paper-plane glyph ("quick arrival"/send), readable on both light and dark taskbars.
# Usage: pwsh ./tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\DesktopJumpList\Assets\app.ico'
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Frame([int]$s) {
    $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [single]([Math]::Max(0.5, $s * 0.02))
    $rect = [System.Drawing.RectangleF]::new($pad, $pad, $s - 2 * $pad, $s - 2 * $pad)
    $radius = [single]($s * 0.22)
    $bg = New-RoundRect $pad $pad ([single]($s - 2 * $pad)) ([single]($s - 2 * $pad)) $radius
    $bgBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect,
        [System.Drawing.Color]::White,
        [System.Drawing.Color]::FromArgb(255, 0xEC, 0xF0, 0xF7), 90.0)
    $g.FillPath($bgBrush, $bg)

    # subtle border so the white tile reads on a white background
    $border = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(28, 0x1A, 0x1D, 0x24), [single]([Math]::Max(0.5, $s * 0.008)))
    $g.DrawPath($border, $bg)

    # paper-plane (send) glyph: polygon in a 24x24 box, scaled/centred
    $pts = @(
        @(2.0, 21.0), @(23.0, 12.0), @(2.0, 3.0), @(2.0, 10.0), @(17.0, 12.0), @(2.0, 14.0)
    )
    $box = $s * 0.60
    $ox = ($s - $box) / 2
    $oy = ($s - $box) / 2
    $scale = $box / 24.0
    $planePts = foreach ($p in $pts) {
        [System.Drawing.PointF]::new([single]($ox + $p[0] * $scale), [single]($oy + $p[1] * $scale))
    }
    $planePath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $planePath.AddPolygon([System.Drawing.PointF[]]$planePts)

    $gBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.RectangleF]::new([single]$ox, [single]$oy, [single]$box, [single]$box),
        [System.Drawing.Color]::FromArgb(255, 0x4F, 0x80, 0xFF),
        [System.Drawing.Color]::FromArgb(255, 0x6A, 0x5C, 0xFF), 45.0)
    $g.FillPath($gBrush, $planePath)

    $g.Dispose()
    return $bmp
}

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
