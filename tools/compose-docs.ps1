Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$preview = Join-Path $root 'artifacts\preview'
$docs = Join-Path $root 'docs'
$shots = Join-Path $docs 'screenshots'
New-Item -ItemType Directory -Force -Path $shots | Out-Null

function Color([int]$a, [int]$r, [int]$g, [int]$b) {
  return [System.Drawing.Color]::FromArgb($a, $r, $g, $b)
}
function HexColor([string]$hex, [int]$alpha = 255) {
  $hex = $hex.TrimStart('#')
  $r = [Convert]::ToInt32($hex.Substring(0,2),16)
  $g = [Convert]::ToInt32($hex.Substring(2,2),16)
  $b = [Convert]::ToInt32($hex.Substring(4,2),16)
  return [System.Drawing.Color]::FromArgb($alpha,$r,$g,$b)
}
function RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  if ($d -gt $w) { $d = $w }
  if ($d -gt $h) { $d = $h }
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  return $p
}
function FillRound($g, $color, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
  $p = RoundedPath $x $y $w $h $r
  $b = New-Object System.Drawing.SolidBrush $color
  $g.FillPath($b, $p)
  $b.Dispose(); $p.Dispose()
}
function StrokeRound($g, $color, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r, [single]$t = 1) {
  $p = RoundedPath $x $y $w $h $r
  $pen = New-Object System.Drawing.Pen $color, $t
  $g.DrawPath($pen, $p)
  $pen.Dispose(); $p.Dispose()
}
function Font([string]$name, [single]$size, [string]$style = 'Regular') {
  $s = [System.Drawing.FontStyle]::$style
  return New-Object System.Drawing.Font($name, $size, $s, [System.Drawing.GraphicsUnit]::Pixel)
}
function DrawText($g, [string]$text, $font, $color, [single]$x, [single]$y) {
  $b = New-Object System.Drawing.SolidBrush $color
  $g.DrawString($text, $font, $b, $x, $y)
  $b.Dispose()
}
function DrawGlow($g, [int]$x, [int]$y, [int]$w, [int]$h, $center, $edge) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddEllipse($x, $y, $w, $h)
  $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $p
  $pg.CenterColor = $center
  $pg.SurroundColors = [System.Drawing.Color[]]@($edge)
  $g.FillPath($pg, $p)
  $pg.Dispose(); $p.Dispose()
}
function NewCanopy([int]$w, [int]$h, [bool]$dark) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
  $rect = New-Object System.Drawing.Rectangle 0,0,$w,$h
  if ($dark) {
    $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (HexColor '#0A0D14'), (HexColor '#161C2B'), 38
  } else {
    $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (HexColor '#FAFBFF'), (HexColor '#E7EDFB'), 38
  }
  $g.FillRectangle($lg, $rect)
  $lg.Dispose()
  if ($dark) {
    DrawGlow $g -160 -120 900 900 (Color 44 76 126 255) (Color 0 76 126 255)
    DrawGlow $g 950 420 900 700 (Color 34 154 122 255) (Color 0 154 122 255)
    DrawGlow $g 600 -200 700 700 (Color 26 126 162 255) (Color 0 126 162 255)
  } else {
    DrawGlow $g -220 -160 900 900 (Color 38 47 99 232) (Color 0 47 99 232)
    DrawGlow $g 980 430 820 700 (Color 30 154 122 255) (Color 0 154 122 255)
    DrawGlow $g 700 -220 700 700 (Color 28 116 220 255) (Color 0 116 220 255)
  }
  # subtle dot grid
  $dot = if ($dark) { Color 16 255 255 255 } else { Color 13 47 99 232 }
  $db = New-Object System.Drawing.SolidBrush $dot
  for ($x = 24; $x -lt $w; $x += 44) {
    for ($y = 24; $y -lt $h; $y += 44) {
      $g.FillEllipse($db, [single]$x, [single]$y, 1.2, 1.2)
    }
  }
  $db.Dispose()
  return @{ Bitmap = $bmp; Graphics = $g }
}
function AddPanel($g, [string]$path, [single]$x, [single]$y, [single]$scale) {
  $img = [System.Drawing.Image]::FromFile($path)
  $w = [single]($img.Width * $scale)
  $h = [single]($img.Height * $scale)
  $g.DrawImage($img, (New-Object System.Drawing.RectangleF($x,$y,$w,$h)))
  $img.Dispose()
  return @{ Width = $w; Height = $h }
}
function SavePng($bmp, [string]$path) {
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}
function DrawPill($g, [string]$text, [single]$x, [single]$y, [bool]$dark) {
  $font = Font 'Segoe UI Semibold' 10
  $size = $g.MeasureString($text, $font)
  $w = $size.Width + 26
  $bg = if ($dark) { Color 28 255 255 255 } else { Color 210 255 255 255 }
  FillRound $g $bg $x $y $w 28 14
  $fg = if ($dark) { HexColor '#9DBBFF' } else { HexColor '#2F63E8' }
  DrawText $g $text $font $fg ($x + 13) ($y + 7)
  $font.Dispose()
  return $w
}
function DrawChip($g, [string]$text, [single]$x, [single]$y, [bool]$dark) {
  $font = Font 'Microsoft YaHei UI' 12
  $size = $g.MeasureString($text, $font)
  $w = $size.Width + 24
  $bg = if ($dark) { Color 25 255 255 255 } else { Color 180 255 255 255 }
  $border = if ($dark) { Color 34 255 255 255 } else { Color 130 255 255 255 }
  FillRound $g $bg $x $y $w 34 17
  StrokeRound $g $border $x $y $w 34 17 1
  $fg = if ($dark) { HexColor '#C9D2E3' } else { HexColor '#4F596B' }
  DrawText $g $text $font $fg ($x + 12) ($y + 9)
  $font.Dispose()
  return $w
}
function RenderHero([string]$panelFile, [string]$outFile, [bool]$dark, [string[]]$headline, [string]$sub1, [string]$sub2, [string[]]$chips) {
  $c = NewCanopy 1600 1000 $dark
  $g = $c.Graphics
  DrawPill $g 'DESKTOP LAUNCHER' 110 150 $dark | Out-Null
  $ink = if ($dark) { HexColor '#F3F5FA' } else { HexColor '#141821' }
  $muted = if ($dark) { HexColor '#A5ADBB' } else { HexColor '#5C6472' }
  $fHead = Font 'Microsoft YaHei UI' 46 'Bold'
  DrawText $g $headline[0] $fHead $ink 106 220
  DrawText $g $headline[1] $fHead $ink 106 286
  # accent underscore
  $accentRect = New-Object System.Drawing.RectangleF 110, 372, 96, 5
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $accentRect, (HexColor '#3D74FF'), (HexColor '#8A5CFF'), 0
  FillRound $g (HexColor '#3D74FF') 110 372 96 5 2.5
  $grad.Dispose()
  $fSub = Font 'Microsoft YaHei UI' 17
  DrawText $g $sub1 $fSub $muted 108 408
  DrawText $g $sub2 $fSub $muted 108 438
  $x = 110
  foreach ($chip in $chips) {
    $w = DrawChip $g $chip $x 520 $dark
    $x += $w + 10
  }
  $fSmall = Font 'Segoe UI' 11
  $foot = if ($dark) { HexColor '#6D7684' } else { HexColor '#8A93A3' }
  DrawText $g 'Windows 10 / 11   ·   .NET 8   ·   v2.0.1' $fSmall $foot 110 900
  AddPanel $g (Join-Path $preview $panelFile) 1032 138 0.5 | Out-Null
  SavePng $c.Bitmap $outFile
  $fHead.Dispose(); $fSub.Dispose(); $fSmall.Dispose()
  $g.Dispose(); $c.Bitmap.Dispose()
}

RenderHero 'final-shortcuts-light.png' (Join-Path $shots 'shortcuts-light.png') $false @('两步之内，','抵达任何地方。') `
  '把常用文件夹、应用和网页，收进一个轻盈的桌面快捷面板。' '拖一下、点一下，就到了。' `
  @('拖动贴边','全局快捷键','拖入即添加','浅色 / 深色')

RenderHero 'final-shortcuts-dark.png' (Join-Path $shots 'shortcuts-dark.png') $true @('两步之内，','抵达任何地方。') `
  '把常用文件夹、应用和网页，收进一个轻盈的桌面快捷面板。' '拖一下、点一下，就到了。' `
  @('拖动贴边','全局快捷键','拖入即添加','浅色 / 深色')

RenderHero 'final-insights-light.png' (Join-Path $shots 'insights-light.png') $false @('看见节奏，','再快一点。') `
  '7 天启动曲线、类型构成和常用排行，' '让快捷面板越用越顺手。' `
  @('7 天趋势','类型构成','常用排行','本地统计')

RenderHero 'final-editor-light.png' (Join-Path $shots 'editor-light.png') $false @('不离开面板，','就完成编辑。') `
  '添加、修改、分组都在原地完成，' '少一次跳转，就快一步。' `
  @('面板内编辑','拖入文件','智能图标','撤销删除')

RenderHero 'final-insights-bottom-light.png' (Join-Path $shots 'insights-detail-light.png') $false @('数据不是冷的，','它让习惯可见。') `
  '类型构成、常用排行和累计次数，' '安静地记录你每天的高频路径。' `
  @('环形构成','常用排行','累计次数','本地保存')

# --- Design board -----------------------------------------------------------
$board = New-Object System.Drawing.Bitmap 1800, 1100
$bg = [System.Drawing.Graphics]::FromImage($board)
$bg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$bg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$bg.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$bg.Clear((HexColor '#F4F6FB'))
DrawGlow $bg 1100 -260 900 700 (Color 34 61 116 255) (Color 0 61 116 255)
DrawGlow $bg -260 700 800 700 (Color 24 154 122 255) (Color 0 154 122 255)

# icon
$iconImg = [System.Drawing.Image]::FromFile((Join-Path $docs 'app-icon.png'))
$bg.DrawImage($iconImg, (New-Object System.Drawing.RectangleF(64,64,64,64)))
$iconImg.Dispose()
$fBrand = Font 'Segoe UI Semibold' 32
$fTitle = Font 'Microsoft YaHei UI' 28 'Bold'
$fBody = Font 'Microsoft YaHei UI' 12
$fSmall = Font 'Segoe UI' 10
$ink = HexColor '#141821'
$muted = HexColor '#5C6472'
$tertiary = HexColor '#8A93A3'
DrawText $bg 'Jump' $fBrand $ink 144 64
DrawText $bg '桌面快捷启动器 · 设计系统 v2.0.1' $fBody $muted 144 106

# palette
DrawText $bg '01  色彩' $fTitle $ink 64 178
$palette = @(
  @('墨色','#141821'), @('画布','#F6F7FA'), @('表面','#FFFFFF'), @('分隔','#E7EAF0'),
  @('主色','#2F63E8'), @('紫罗兰','#7A5CFF'), @('珊瑚','#FF7A59'), @('青绿','#0FAF9F')
)
for ($i = 0; $i -lt $palette.Count; $i++) {
  $col = $i % 4
  $row = [math]::Floor($i / 4)
  $x = 64 + $col * 172
  $y = 220 + $row * 100
  $name = $palette[$i][0]
  $hex = $palette[$i][1]
  FillRound $bg (HexColor $hex) $x $y 64 52 10
  StrokeRound $bg (Color 30 20 30 50) $x $y 64 52 10 1
  DrawText $bg $name $fBody $ink ($x + 76) ($y + 4)
  DrawText $bg $hex $fSmall $tertiary ($x + 76) ($y + 25)
}

# typography
DrawText $bg '02  排版' $fTitle $ink 64 430
$fDisplay = Font 'Microsoft YaHei UI' 30 'Bold'
$fH = Font 'Microsoft YaHei UI' 18 'Bold'
$fCap = Font 'Microsoft YaHei UI' 10
DrawText $bg '快捷启动' $fDisplay $ink 64 474
DrawText $bg 'Display 30 / 600' $fSmall $tertiary 320 482
DrawText $bg '看见节奏，再快一点。' $fH $ink 64 528
DrawText $bg 'Title 18 / 600' $fSmall $tertiary 320 532
DrawText $bg '把常用目标收进一个轻盈的面板。' $fBody $muted 64 564
DrawText $bg 'Body 13 / 400' $fSmall $tertiary 320 566
DrawText $bg '文件夹 · 已用 12 次' $fCap $tertiary 64 592
DrawText $bg 'Caption 10 / 400' $fSmall $tertiary 320 592

# components
DrawText $bg '03  组件' $fTitle $ink 64 646
$btnRect = New-Object System.Drawing.RectangleF 64, 692, 168, 42
$btnGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $btnRect, (HexColor '#356BF2'), (HexColor '#6E5BF6'), 0
$btnPath = RoundedPath 64 692 168 42 12
$bg.FillPath($btnGrad, $btnPath)
$btnGrad.Dispose(); $btnPath.Dispose()
DrawText $bg '＋  添加快捷项' (Font 'Microsoft YaHei UI' 12 'Bold') (HexColor '#FFFFFF') 88 704
FillRound $bg (HexColor '#FFFFFF') 248 692 108 42 12
StrokeRound $bg (HexColor '#E7EAF0') 248 692 108 42 12 1
DrawText $bg '使用概览' (Font 'Microsoft YaHei UI' 12) (HexColor '#5C6472') 270 704
FillRound $bg (HexColor '#2F63E8') 372 692 112 42 21
DrawText $bg '全部  10' (Font 'Microsoft YaHei UI' 12 'Bold') (HexColor '#FFFFFF') 396 704
DrawText $bg '＋  添加快捷项' $fSmall $tertiary 64 746
DrawText $bg '主按钮 / 次按钮 / 筛选 chip' $fSmall $tertiary 248 746

# shortcut card mock
FillRound $bg (HexColor '#FFFFFF') 64 790 356 76 16
StrokeRound $bg (HexColor '#E7EAF0') 64 790 356 76 16 1
FillRound $bg (HexColor '#EAF0FF') 78 804 46 46 14
$folderPen = New-Object System.Drawing.Pen (HexColor '#2F63E8'), 2
$bg.DrawArc($folderPen, 90, 815, 10, 8, 180, 180)
$bg.DrawLine($folderPen, 95, 815, 101, 815)
$bg.DrawArc($folderPen, 101, 815, 8, 8, 270, 90)
$bg.DrawRectangle($folderPen, 92, 820, 18, 14)
$folderPen.Dispose()
DrawText $bg '项目工作区' (Font 'Microsoft YaHei UI' 13 'Bold') (HexColor '#141821') 136 802
DrawText $bg '开发 · 刚刚用过' (Font 'Microsoft YaHei UI' 10) (HexColor '#8A93A3') 136 826
DrawText $bg '已选中 / 悬停浮起' $fSmall $tertiary 64 874

# toggle + search
FillRound $bg (HexColor '#2F63E8') 64 920 42 24 12
FillRound $bg (HexColor '#FFFFFF') 82 923 18 18 9
DrawText $bg '开机启动' $fBody $muted 118 920
FillRound $bg (HexColor '#F2F4F8') 248 918 172 34 12
$searchPen = New-Object System.Drawing.Pen (HexColor '#5C6472'), 1.5
$bg.DrawEllipse($searchPen, 264, 928, 9, 9)
$bg.DrawLine($searchPen, 272, 936, 276, 940)
$searchPen.Dispose()
DrawText $bg '搜索名称、分组或路径…' (Font 'Microsoft YaHei UI' 10) (HexColor '#8A93A3') 286 927
DrawText $bg '开关 / 搜索框' $fSmall $tertiary 64 958

# interaction principles
DrawText $bg '04  交互原则' $fTitle $ink 64 1004
DrawText $bg '拖动贴边  ·  单击打开  ·  Ctrl K 搜索  ·  Ctrl Alt J 呼出  ·  拖入文件即添加  ·  删除可撤销' $fBody $muted 64 1044

# screenshots
$p1 = AddPanel $bg (Join-Path $preview 'final-shortcuts-light.png') 800 96 0.5
$p2 = AddPanel $bg (Join-Path $preview 'final-insights-light.png') 1294 96 0.5
DrawText $bg '快捷面板 / 搜索与分组' $fBody $muted 824 744
DrawText $bg '使用概览 / 数据图表' $fBody $muted 1318 744
$fNote = Font 'Microsoft YaHei UI' 10
DrawText $bg '中央展开，面板内完成全部编辑。' $fNote $tertiary 824 772
DrawText $bg '近 7 天趋势、类型构成与常用排行。' $fNote $tertiary 1318 772

$board.Save((Join-Path $docs 'design-board.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bg.Dispose(); $board.Dispose()
$fBrand.Dispose(); $fTitle.Dispose(); $fBody.Dispose(); $fSmall.Dispose(); $fDisplay.Dispose(); $fH.Dispose(); $fCap.Dispose(); $fNote.Dispose()

Write-Host 'Wrote docs/screenshots and docs/design-board.png'
