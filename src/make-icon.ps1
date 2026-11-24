# Generates app\icon.ico (16/24/32/48/64/128/256 px): three MIDI-fader bars on a dark round badge.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Transparent)
    $bg = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 28, 30, 40))
    $g.FillEllipse($bg, 0, 0, $s - 1, $s - 1)
    $c = [Drawing.Color]::FromArgb(255, 0, 200, 255)
    $br = New-Object Drawing.SolidBrush $c
    $u = $s / 32.0
    foreach ($bar in @(@(8, 9, 14), @(14, 13, 10), @(20, 7, 16))) {
        $g.FillRectangle($br, [float]($bar[0] * $u), [float]($bar[1] * $u), [float](5 * $u), [float]($bar[2] * $u))
    }
    $g.Dispose()
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
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
