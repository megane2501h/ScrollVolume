# ScrollVolume のアプリアイコン (src\app.ico) を生成する。
# 青い角丸四角＋白いスピーカー＋音波。小さいサイズほど音波を減らして潰れないようにする。
param([string]$Out = (Join-Path $PSScriptRoot 'src\app.ico'), [string]$PreviewDir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Drawing.Primitives -ErrorAction SilentlyContinue

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # 背景（角丸＋縦グラデーション）
    $pad = [Math]::Max(0.5, $size * 0.03)
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $r = $size * 0.22; $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $top = [System.Drawing.Color]::FromArgb(255, 58, 150, 255)
    $bot = [System.Drawing.Color]::FromArgb(255, 0, 92, 200)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $top, $bot, 90
    $g.FillPath($grad, $path)

    # スピーカー本体（左寄せ）
    $s = $size
    $white = [System.Drawing.Brushes]::White
    $cx = $s * 0.20; $cy = $s * 0.5
    $boxW = $s * 0.13; $boxH = $s * 0.22
    $coneW = $s * 0.20; $coneH = $s * 0.50
    $pts = @(
        (New-Object System.Drawing.PointF $cx, ($cy - $boxH / 2)),
        (New-Object System.Drawing.PointF ($cx + $boxW), ($cy - $boxH / 2)),
        (New-Object System.Drawing.PointF ($cx + $boxW + $coneW), ($cy - $coneH / 2)),
        (New-Object System.Drawing.PointF ($cx + $boxW + $coneW), ($cy + $coneH / 2)),
        (New-Object System.Drawing.PointF ($cx + $boxW), ($cy + $boxH / 2)),
        (New-Object System.Drawing.PointF $cx, ($cy + $boxH / 2))
    )
    $g.FillPolygon($white, [System.Drawing.PointF[]]$pts)

    # 音波
    $waves = if ($size -le 20) { 1 } elseif ($size -le 32) { 2 } else { 3 }
    $pw = [Math]::Max(1.3, $s * 0.065)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $pw
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $ox = $cx + $boxW + $coneW - $s * 0.06
    for ($i = 1; $i -le $waves; $i++) {
        $rr = $s * (0.10 + 0.115 * $i)
        if ($waves -eq 1) { $rr = $s * 0.24 }
        $g.DrawArc($pen, $ox - $rr, $cy - $rr, 2 * $rr, 2 * $rr, -45, 90)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $b = New-IconBitmap $sz
    if ($PreviewDir) { New-Item -ItemType Directory -Force $PreviewDir | Out-Null; $b.Save((Join-Path $PreviewDir "appicon_$sz.png")) }
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    , $ms.ToArray()
}

# ICO 形式で書き出す（各サイズ PNG 埋め込み。Vista 以降対応）
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Dispose()
"OK: $Out"
