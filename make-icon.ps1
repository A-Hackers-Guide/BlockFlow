# Draws the BlockFlow icon (stacked coloured blocks) and writes a multi-size PNG .ico
param([string]$Out = "$PSScriptRoot\src\app.ico")
Add-Type -AssemblyName System.Drawing

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $k = $s / 256.0
    $bg = RoundRect (8*$k) (8*$k) (240*$k) (240*$k) (52*$k)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(44,52,72)), ([System.Drawing.Color]::FromArgb(24,28,40)), 90
    $g.FillPath($br, $bg)
    $blocks = @(
        @{ x = 40;  y = 44;  w = 176; c = [System.Drawing.Color]::FromArgb(52,120,220) },
        @{ x = 72;  y = 104; w = 144; c = [System.Drawing.Color]::FromArgb(38,170,96) },
        @{ x = 40;  y = 164; w = 176; c = [System.Drawing.Color]::FromArgb(255,160,30) }
    )
    foreach ($b in $blocks) {
        $p = RoundRect ($b.x*$k) ($b.y*$k) ($b.w*$k) (48*$k) (12*$k)
        $g.FillPath((New-Object System.Drawing.SolidBrush $b.c), $p)
    }
    # the C-arm connecting the first and last block
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(52,120,220))), [float](40*$k), [float](80*$k), [float](24*$k), [float](92*$k))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = $sizes | ForEach-Object { ,(Frame $_) }
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write([byte[]]$p) }
$w.Close()
"Wrote $Out"
