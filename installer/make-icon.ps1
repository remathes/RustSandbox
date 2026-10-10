<#
Draws the Rust Sandbox app icon (rusty metal foundation block on sand, with a build hammer)
and writes a multi-size Windows .ico plus a 256px preview PNG.

  powershell -NoProfile -ExecutionPolicy Bypass -File installer\make-icon.ps1 -Ico src\RustSandbox\app.ico [-Preview icon.png]
#>
param(
    [Parameter(Mandatory = $true)][string]$Ico,
    [string]$Preview
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function C([string]$hex, [double]$a = 1.0) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
    [System.Drawing.Color]::FromArgb([int](255 * $a), $c.R, $c.G, $c.B)
}
function Pts([float[]]$xy) {
    $arr = New-Object 'System.Drawing.PointF[]' ($xy.Length / 2)
    for ($i = 0; $i -lt $xy.Length; $i += 2) { $arr[$i / 2] = New-Object System.Drawing.PointF($xy[$i], $xy[$i + 1]) }
    , $arr
}
function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    , $p
}
function Grad([float]$x1, [float]$y1, [float]$x2, [float]$y2, [string]$c1, [string]$c2) {
    $b = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF($x1, $y1)), (New-Object System.Drawing.PointF($x2, $y2)), (C $c1), (C $c2))
    $b.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY
    , $b
}
function Solid([string]$hex, [double]$a = 1.0) { , (New-Object System.Drawing.SolidBrush((C $hex $a))) }
function NewPen([string]$hex, [double]$a, [float]$w) {
    $p = New-Object System.Drawing.Pen((C $hex $a), $w)
    $p.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    , $p
}

# detail 1 = 16-24 px (no rivets/hammer, thicker lines), 2 = 32-48 px, 3 = 64+ px
function Render([int]$detail, [int]$size) {
    $big = 1024
    $bmp = New-Object System.Drawing.Bitmap($big, $big, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($big / 256.0, $big / 256.0)

    # dark tile with rust-orange rim
    $tile = RoundRect 8 8 240 240 52
    $g.FillPath((Grad 0 8 0 248 '#2b2420' '#14110f'), $tile)
    $rim = if ($detail -ge 2) { 6 } else { 10 }
    $g.DrawPath((NewPen '#d5602a' 0.9 $rim), $tile)

    # sand ground
    $sand = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sand.AddPolygon((Pts @(128, 132, 222, 179, 128, 226, 34, 179)))
    $sb = New-Object System.Drawing.Drawing2D.PathGradientBrush($sand)
    $sb.CenterColor = C '#e9c98f'
    $sb.SurroundColors = [System.Drawing.Color[]]@((C '#c79d5e'))
    $g.FillPath($sb, $sand)

    # block faces
    $g.FillPolygon((Grad 63 83 193 149 '#f08a4b' '#d5602a'), (Pts @(128, 84, 192, 116, 128, 148, 64, 116)))
    $g.FillPolygon((Grad 0 115 0 217 '#c24e1f' '#9c3a15'), (Pts @(64, 116, 128, 148, 128, 216, 64, 184)))
    $g.FillPolygon((Grad 0 115 0 217 '#8f3313' '#6a240c'), (Pts @(192, 116, 128, 148, 128, 216, 192, 184)))
    $edge = if ($detail -ge 2) { 3 } else { 5 }
    $g.DrawPolygon((NewPen '#ffb27f' 0.8 $edge), (Pts @(64, 116, 128, 84, 192, 116, 128, 148)))
    $seam = if ($detail -ge 2) { 2 } else { 4 }
    $g.DrawLine((NewPen '#3a1206' 0.6 $seam), [float]128, [float]148, [float]128, [float]216)

    if ($detail -ge 2) {
        # plank seams
        $pl = NewPen '#5e1f0c' 0.55 3
        $g.DrawLine($pl, [float]64, [float]139, [float]128, [float]171); $g.DrawLine($pl, [float]64, [float]162, [float]128, [float]194)
        $pr = NewPen '#4a170a' 0.55 3
        $g.DrawLine($pr, [float]192, [float]139, [float]128, [float]171); $g.DrawLine($pr, [float]192, [float]162, [float]128, [float]194)
        # rivets
        $dark = Solid '#2a0f06' 0.7; $shine = Solid '#f0a070' 0.55
        foreach ($r in @(@(72, 127), @(72, 181), @(120, 152), @(120, 205), @(184, 127), @(184, 181), @(136, 152), @(136, 205))) {
            $g.FillEllipse($dark, [float]($r[0] - 3.6), [float]($r[1] - 3.6), [float]7.2, [float]7.2)
            $g.FillEllipse($shine, [float]($r[0] - 2.8), [float]($r[1] - 2.8), [float]4, [float]4)
        }
    }
    if ($detail -ge 3) {
        # rust stains on the top face
        $st = $g.Save()
        $g.TranslateTransform(112, 98); $g.RotateTransform(-8)
        $g.FillEllipse((Solid '#7a2a10' 0.35), [float]-16, [float]-6, [float]32, [float]12)
        $g.Restore($st)
        $g.FillEllipse((Solid '#7a2a10' 0.3), [float]140, [float]108, [float]20, [float]8)
    }
    if ($detail -ge 2) {
        # build hammer resting on the block
        $st = $g.Save()
        $g.TranslateTransform(150, 70); $g.RotateTransform(-28); $g.TranslateTransform(-150, -70)
        $g.TranslateTransform(4, 12)
        $g.FillPath((Solid '#6b4a2b'), (RoundRect 146 58 9 62 3))
        $g.FillPath((Solid '#8a6239'), (RoundRect 146 58 4 62 1.9))
        $g.FillPath((Solid '#c9ccd1'), (RoundRect 126 46 50 20 4))
        $g.FillPath((Solid '#eef0f2'), (RoundRect 126 46 50 7 3))
        $g.FillPath((Solid '#8e939b'), (RoundRect 126 60 50 6 2.9))
        $g.Restore($st)
    }
    $g.Dispose()

    $out = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g2.Clear([System.Drawing.Color]::Transparent)
    $g2.DrawImage($bmp, 0, 0, $size, $size)
    $g2.Dispose(); $bmp.Dispose()
    , $out
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
    $detail = if ($s -le 24) { 1 } elseif ($s -le 48) { 2 } else { 3 }
    $img = Render $detail $s
    if ($s -eq 256 -and $Preview) {
        $pv = [System.IO.Path]::GetFullPath($Preview)
        $pvDir = Split-Path -Parent $pv
        if (-not (Test-Path $pvDir)) { New-Item -ItemType Directory -Path $pvDir | Out-Null }
        $img.Save($pv, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $ms = New-Object System.IO.MemoryStream
    $img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $img.Dispose()
    $pngs.Add($ms.ToArray())
}

# ICO container with PNG-compressed entries (Windows Vista and later)
$path = [System.IO.Path]::GetFullPath($Ico)
$dir = Split-Path -Parent $path
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$fs = [System.IO.File]::Create($path)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
Write-Host "Wrote $path ($((Get-Item $path).Length) bytes)"
