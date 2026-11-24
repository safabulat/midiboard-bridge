# Generates src\icon.ico (16-256 px) from the Midiboard app icon (midiboard-site img/appicon.svg,
# same as the iOS AppIcon): a dark rounded square with four rounded pads in the editor's colours.
#   <rect width=100 height=100 rx=22 fill=#0b0d11/>
#   pads 30x30 rx=8 at (17,17) #e14cc4, (53,17) #f0a02c, (17,53) #33bff0, (53,53) #2fdd9b
Add-Type -AssemblyName System.Drawing

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pads = @(
    @(17, 17, '#e14cc4'), @(53, 17, '#f0a02c'),
    @(17, 53, '#33bff0'), @(53, 53, '#2fdd9b')
)
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)
    $k = $s / 100.0
    $bg = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#0b0d11'))
    $g.FillPath($bg, (RoundRect 0 0 ($s - 0.01) ($s - 0.01) (22 * $k)))
    foreach ($p in $pads) {
        $b = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($p[2]))
        $g.FillPath($b, (RoundRect ($p[0] * $k) ($p[1] * $k) (30 * $k) (30 * $k) (8 * $k)))
    }
    $g.Dispose()
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $PSScriptRoot 'icon-256.png')) }
    $bmp.Dispose()
    ,$ms.ToArray()
}
$out = Join-Path $PSScriptRoot 'icon.ico'
$fs = [IO.File]::Create($out); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"icon.ico: $((Get-Item $out).Length) bytes"
