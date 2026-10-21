# Draws the Tanjelo icon: a red-orange tangelo, with the fruit's characteristic knob at the stem, and a leaf.
# Writes the app icon (multi-size .ico) and the README logo (SVG) from the same geometry.
# usage: powershell -File tools\make-icon.ps1
param(
    [string]$Ico = (Join-Path $PSScriptRoot "..\src\Tanjelo\Assets\app.ico"),
    [string]$Svg = (Join-Path $PSScriptRoot "..\docs\logo.svg")
)

Add-Type -AssemblyName System.Drawing

# Everything is designed on a 32x32 grid.
# Fruit outline as cubic Bezier segments: knob, right shoulder, bottom, left shoulder, back up the knob.
$start = 16, 3.6
$curves = @(
    @(18.4, 3.6, 19.6, 5.2, 19.8, 7.6),
    @(25.8, 8.2, 29.5, 12.8, 29.5, 18.6),
    @(29.5, 25.6, 23.6, 31, 16, 31),
    @(8.4, 31, 2.5, 25.6, 2.5, 18.6),
    @(2.5, 12.8, 6.2, 8.2, 12.2, 7.6),
    @(12.4, 5.2, 13.6, 3.6, 16, 3.6)
)
$fruitLight = "#FFB36B"; $fruitDeep = "#E4500E"
$leafLight = "#7CC44C"; $leafDeep = "#2E7D32"
$calyx = @{ X = 16; Y = 4.1; R = 1.5; Color = "#3E7D2A" }   # the little green star citrus has where a stem would be
$shine = @{ X = 7.2; Y = 12; W = 6.5; H = 4.2; Alpha = 0.28 }
# Pointed citrus leaf (a lens between two curves), in its own coordinates, then rotated into place.
$leaf = @{ X = 21.6; Y = 3.4; Angle = -24; Half = 6.2; Bulge = 3.3 }
# Peel dimples, only drawn where there are enough pixels to show them.
$dimples = @(@(20, 13), @(24, 17.5), @(22.5, 24), @(17, 27.5), @(10.5, 25), @(6.5, 20.5), @(13, 20), @(18.5, 19), @(25.5, 22), @(9, 16.5))

function Color([string]$hex, [int]$alpha = 255) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
    [System.Drawing.Color]::FromArgb($alpha, $c)
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $u = $size / 32.0

    # Fruit: light where the light hits, deepening to red-orange at the edges.
    $fruit = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x0 = $start[0]; $y0 = $start[1]
    foreach ($c in $curves) {
        $fruit.AddBezier([single]($x0 * $u), [single]($y0 * $u), [single]($c[0] * $u), [single]($c[1] * $u),
            [single]($c[2] * $u), [single]($c[3] * $u), [single]($c[4] * $u), [single]($c[5] * $u))
        $x0 = $c[4]; $y0 = $c[5]
    }
    $fruit.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.PathGradientBrush $fruit
    $fill.CenterPoint = New-Object System.Drawing.PointF ([single](12.5 * $u)), ([single](15 * $u))
    $fill.CenterColor = Color $fruitLight
    $fill.SurroundColors = [System.Drawing.Color[]]@(Color $fruitDeep)
    $g.FillPath($fill, $fruit)

    if ($size -ge 64) {
        $dot = New-Object System.Drawing.SolidBrush (Color "#FFFFFF" 34)
        foreach ($p in $dimples) { $g.FillEllipse($dot, [single](($p[0] - 0.35) * $u), [single](($p[1] - 0.35) * $u), [single](0.7 * $u), [single](0.7 * $u)) }
    }
    if ($size -ge 24) {
        $brush = New-Object System.Drawing.SolidBrush (Color "#FFFFFF" ([int](255 * $shine.Alpha)))
        $g.FillEllipse($brush, [single]($shine.X * $u), [single]($shine.Y * $u), [single]($shine.W * $u), [single]($shine.H * $u))
    }

    $state = $g.Save()
    $g.TranslateTransform([single]($leaf.X * $u), [single]($leaf.Y * $u))
    $g.RotateTransform($leaf.Angle)
    $h = $leaf.Half * $u; $b = $leaf.Bulge * $u
    $leafPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $leafPath.AddBezier([single](-$h), 0, [single](-$h / 3), [single](-$b), [single]($h / 3), [single](-$b), [single]$h, 0)
    $leafPath.AddBezier([single]$h, 0, [single]($h / 3), [single]$b, [single](-$h / 3), [single]$b, [single](-$h), 0)
    $leafRect = New-Object System.Drawing.RectangleF ([single](-$h)), ([single](-$b)), ([single](2 * $h)), ([single](2 * $b))
    $leafBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $leafRect, (Color $leafLight), (Color $leafDeep), 90
    $g.FillPath($leafBrush, $leafPath)
    if ($size -ge 48) {
        $vein = New-Object System.Drawing.Pen (Color "#FFFFFF" 64), ([single](0.5 * $u))
        $g.DrawLine($vein, [single](-$h * 0.8), 0, [single]($h * 0.85), 0)
    }
    $g.Restore($state)

    $calyxBrush = New-Object System.Drawing.SolidBrush (Color $calyx.Color)
    $r = [Math]::Max(0.8, $calyx.R * $u)
    $g.FillEllipse($calyxBrush, [single]($calyx.X * $u - $r), [single]($calyx.Y * $u - $r), [single](2 * $r), [single](2 * $r))

    $g.Dispose()
    $bytes = if ($size -ge 256) { Encode-Png $bmp } else { Encode-Dib $bmp }
    $bmp.Dispose()
    return ,$bytes
}

# 256 px frames are stored as PNG (the Vista+ convention); smaller ones as classic DIBs, which every reader understands.
function Encode-Png($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

function Encode-Dib($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $w, $h), [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)
    $maskStride = [int][Math]::Ceiling($w / 32.0) * 4
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER; height is doubled because the AND mask follows the colour data.
    $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]0); $bw.Write([uint32]($w * $h * 4 + $maskStride * $h)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $data.Stride, $w * 4) }  # bottom-up BGRA rows
    $bw.Write((New-Object byte[] ($maskStride * $h)))                                    # empty AND mask: alpha does the work
    $bw.Flush()
    return ,$ms.ToArray()
}

function Write-Ico([string]$path) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $frames = foreach ($s in $sizes) { ,(Draw-Icon $s) }
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $stream = [System.IO.File]::Create($path)
    $w = New-Object System.IO.BinaryWriter $stream
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)   # ICONDIR
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {                                 # ICONDIRENTRY per frame
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($f in $frames) { $w.Write([byte[]]$f) }
    $w.Close()
}

function Write-Svg([string]$path) {
    $inv = [System.Globalization.CultureInfo]::InvariantCulture
    $n = { param($v) ([double]$v).ToString("0.##", $inv) }
    $d = "M$(& $n $start[0]) $(& $n $start[1])" + (($curves | ForEach-Object { "C" + (($_ | ForEach-Object { & $n $_ }) -join " ") }) -join "")
    $cx = & $n ($shine.X + $shine.W / 2); $cy = & $n ($shine.Y + $shine.H / 2)
    $h = $leaf.Half; $b = $leaf.Bulge
    $leafD = "M$(& $n (-$h)) 0C$(& $n (-$h / 3)) $(& $n (-$b)) $(& $n ($h / 3)) $(& $n (-$b)) $(& $n $h) 0C$(& $n ($h / 3)) $(& $n $b) $(& $n (-$h / 3)) $(& $n $b) $(& $n (-$h)) 0Z"
    $dots = ($dimples | ForEach-Object { "    <circle cx=`"$(& $n $_[0])`" cy=`"$(& $n $_[1])`" r=`"0.35`"/>" }) -join "`n"
    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" width="256" height="256">
  <defs>
    <radialGradient id="fruit" cx="12.5" cy="15" r="17" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="$fruitLight"/>
      <stop offset="1" stop-color="$fruitDeep"/>
    </radialGradient>
    <linearGradient id="leaf" x1="0" y1="$(& $n (-$b))" x2="0" y2="$(& $n $b)" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="$leafLight"/>
      <stop offset="1" stop-color="$leafDeep"/>
    </linearGradient>
  </defs>
  <path d="$d" fill="url(#fruit)"/>
  <g fill="#fff" fill-opacity="0.13">
$dots
  </g>
  <ellipse cx="$cx" cy="$cy" rx="$(& $n ($shine.W / 2))" ry="$(& $n ($shine.H / 2))" fill="#fff" fill-opacity="$(& $n $shine.Alpha)"/>
  <g transform="translate($(& $n $leaf.X) $(& $n $leaf.Y)) rotate($($leaf.Angle))">
    <path d="$leafD" fill="url(#leaf)"/>
    <path d="M$(& $n (-$h * 0.8)) 0H$(& $n ($h * 0.85))" stroke="#fff" stroke-opacity="0.25" stroke-width="0.5"/>
  </g>
  <circle cx="$(& $n $calyx.X)" cy="$(& $n $calyx.Y)" r="$(& $n $calyx.R)" fill="$($calyx.Color)"/>
</svg>
"@
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    [System.IO.File]::WriteAllText($path, $svg, (New-Object System.Text.UTF8Encoding($false)))
}

Write-Ico $Ico
Write-Svg $Svg
Write-Host "wrote $Ico"
Write-Host "wrote $Svg"
