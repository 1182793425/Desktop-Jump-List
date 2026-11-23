# Generates src/DesktopJumpList/Assets/app.ico — the round gradient ball with a 2x2 glyph.
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

    $pad = [single]([Math]::Max(0.5, $s * 0.03))
    $rect = [System.Drawing.RectangleF]::new($pad, $pad, $s - 2 * $pad, $s - 2 * $pad)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect,
        [System.Drawing.Color]::FromArgb(255, 0x4F, 0x80, 0xFF),
        [System.Drawing.Color]::FromArgb(255, 0x8A, 0x5C, 0xFF), 55.0)
    $g.FillEllipse($brush, $rect)

    # soft top highlight
    $hl = [System.Drawing.RectangleF]::new($s * 0.10, $s * 0.06, $s * 0.80, $s * 0.48)
    $hlBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($hl,
        [System.Drawing.Color]::FromArgb(70, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255), 90.0)
    $g.FillEllipse($hlBrush, $hl)

    # 2x2 glyph
    $glyph = $s * 0.38
    $cell = $glyph * 0.41
    $gap = $glyph - 2 * $cell
    $ox = ($s - $glyph) / 2
    $oy = ($s - $glyph) / 2
    $r = [single]([Math]::Max(0.6, $cell * 0.30))
    $cells = @(
        @(0, 0, 255, $r), @(1, 0, 200, $r), @(0, 1, 200, $r), @(1, 1, 255, ($cell / 2))
    )
    foreach ($c in $cells) {
        $x = [single]($ox + $c[0] * ($cell + $gap))
        $y = [single]($oy + $c[1] * ($cell + $gap))
        $path = New-RoundRect $x $y ([single]$cell) ([single]$cell) ([single]$c[3])
        $wb = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb($c[2], 255, 255, 255))
        $g.FillPath($wb, $path)
    }
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
