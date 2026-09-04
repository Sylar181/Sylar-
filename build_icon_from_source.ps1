# Converts app_source.png into a transparent-background multi-size app.ico
# - Flood-fills the bright (near-white) background from the borders to transparent,
#   keeping interior highlights (white glints) intact because they are enclosed by the artwork.
# - Crops to the opaque content bounds so the artwork fills the icon.
Add-Type -AssemblyName System.Drawing

$srcPath = Join-Path $PSScriptRoot "app_source.png"
if (-not (Test-Path $srcPath)) {
    Write-Error "app_source.png not found. Please save the image to: $srcPath"
    exit 1
}

$src = [System.Drawing.Bitmap]::new($srcPath)

# Downscale large sources first so pixel processing stays fast.
if ($src.Width -gt 512 -or $src.Height -gt 512) {
    $ratio = 512.0 / [Math]::Max($src.Width, $src.Height)
    $nw = [int]($src.Width * $ratio); $nh = [int]($src.Height * $ratio)
    $small = [System.Drawing.Bitmap]::new($nw, $nh, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($small)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($src, 0, 0, $nw, $nh)
    $g.Dispose(); $src.Dispose()
    $src = $small
    Write-Host "Downscaled source to ${nw}x${nh}"
}

function Make-TransparentBackground([System.Drawing.Bitmap]$bmp, [int]$threshold) {
    $w = $bmp.Width; $h = $bmp.Height
    $visited = [bool[,]]::new($w, $h)
    $queue = [System.Collections.Generic.Queue[int[]]]::new()
    for ($x = 0; $x -lt $w; $x++) { $queue.Enqueue(@($x, 0)); $queue.Enqueue(@($x, ($h - 1))) }
    for ($y = 0; $y -lt $h; $y++) { $queue.Enqueue(@(0, $y)); $queue.Enqueue(@(($w - 1), $y)) }

    while ($queue.Count -gt 0) {
        $p = $queue.Dequeue()
        $x = $p[0]; $y = $p[1]
        if ($visited[$x, $y]) { continue }
        $visited[$x, $y] = $true
        $c = $bmp.GetPixel($x, $y)
        $bright = [Math]::Min([Math]::Min([int]$c.R, [int]$c.G), [int]$c.B)
        if ($bright -lt $threshold) { continue }
        $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, $c.R, $c.G, $c.B))
        foreach ($n in @(@(($x + 1), $y), @(($x - 1), $y), @($x, ($y + 1)), @($x, ($y - 1)))) {
            if ($n[0] -ge 0 -and $n[0] -lt $w -and $n[1] -ge 0 -and $n[1] -lt $h -and -not $visited[$n[0], $n[1]]) {
                $queue.Enqueue(@($n[0], $n[1]))
            }
        }
    }
}

function Crop-OpaqueBounds([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $minX = $w; $minY = $h; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            if ($bmp.GetPixel($x, $y).A -gt 10) {
                if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt 0) { return $bmp }
    $bw = $maxX - $minX + 1; $bh = $maxY - $minY + 1
    $cropped = [System.Drawing.Bitmap]::new($bw, $bh, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($cropped)
    $g.DrawImage($bmp, [System.Drawing.Rectangle]::new(0, 0, $bw, $bh),
        [System.Drawing.Rectangle]::new($minX, $minY, $bw, $bh), [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    return $cropped
}

Write-Host "Removing background..."
Make-TransparentBackground $src 190
Write-Host "Cropping to opaque content..."
$src = Crop-OpaqueBounds $src

function New-IconBitmap([System.Drawing.Bitmap]$src, [int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $target = $size * 0.94
    $ratio = [Math]::Min($target / $src.Width, $target / $src.Height)
    $w = [float]($src.Width * $ratio)
    $h = [float]($src.Height * $ratio)
    $x = [float](($size - $w) / 2)
    $y = [float](($size - $h) / 2)
    $g.DrawImage($src, $x, $y, $w, $h)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 32, 48, 64, 128, 256
$bitmaps = @()
foreach ($sz in $sizes) { $bitmaps += ,(New-IconBitmap $src $sz) }

$outPath = Join-Path $PSScriptRoot "app.ico"
$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$bitmaps.Count)

$pngs = @()
foreach ($bmp in $bitmaps) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,$ms.ToArray()
    $bmp.Dispose()
}
$src.Dispose()

$offset = 6 + 16 * $bitmaps.Count
for ($i = 0; $i -lt $bitmaps.Count; $i++) {
    $sz = $bitmaps[$i].Width
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()

Write-Host "icon generated (transparent bg): $outPath ($($bitmaps.Count) sizes)"
