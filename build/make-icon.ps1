param(
    [string]$Source = "C:\Users\YossiYadgar\Desktop\Untitled.jpg",
    [string]$Destination = "C:\Projects\driverUpdater\src\DriverUpdater.App\Assets\app.ico"
)

Add-Type -AssemblyName System.Drawing

$bmp = [System.Drawing.Bitmap]::FromFile($Source)

$tol = 240
$minX = $bmp.Width
$minY = $bmp.Height
$maxX = 0
$maxY = 0

$rect = New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stride = $data.Stride
$bytes = New-Object byte[] ($stride * $bmp.Height)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
$bmp.UnlockBits($data)

for ($y = 0; $y -lt $bmp.Height; $y++) {
    $rowStart = $y * $stride
    for ($x = 0; $x -lt $bmp.Width; $x++) {
        $off = $rowStart + ($x * 4)
        $b = $bytes[$off]
        $g = $bytes[$off + 1]
        $r = $bytes[$off + 2]
        if ($r -lt $tol -or $g -lt $tol -or $b -lt $tol) {
            if ($x -lt $minX) { $minX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}

Write-Output "Bounds: ($minX,$minY) - ($maxX,$maxY) of ($($bmp.Width)x$($bmp.Height))"

$w = $maxX - $minX + 1
$h = $maxY - $minY + 1
$side = [Math]::Max($w, $h)
Write-Output "Crop: ${w}x${h}, square side: $side"

$square = New-Object System.Drawing.Bitmap $side, $side, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gfx = [System.Drawing.Graphics]::FromImage($square)
$gfx.Clear([System.Drawing.Color]::Transparent)
$gfx.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$gfx.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$gfx.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$gfx.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

$dstX = [int](($side - $w) / 2)
$dstY = [int](($side - $h) / 2)
$srcRect = New-Object System.Drawing.Rectangle $minX, $minY, $w, $h
$dstRect = New-Object System.Drawing.Rectangle $dstX, $dstY, $w, $h
$gfx.DrawImage($bmp, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
$gfx.Dispose()
$bmp.Dispose()

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngBytes = @{}
foreach ($s in $sizes) {
    $resized = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rg = [System.Drawing.Graphics]::FromImage($resized)
    $rg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $rg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $rg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $rg.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $rg.DrawImage($square, 0, 0, $s, $s)
    $rg.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $resized.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes[$s] = $ms.ToArray()
    $resized.Dispose()
    $ms.Dispose()
}
$square.Dispose()

$fs = New-Object System.IO.FileStream $Destination, ([System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter $fs

$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

$headerSize = 6 + (16 * $sizes.Count)
$offset = $headerSize

foreach ($s in $sizes) {
    $imgBytes = $pngBytes[$s]
    $dim = if ($s -eq 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$imgBytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $imgBytes.Length
}

foreach ($s in $sizes) {
    $bw.Write($pngBytes[$s])
}

$bw.Close()
$fs.Close()

$info = Get-Item $Destination
Write-Output "Wrote $($info.FullName) ($($info.Length) bytes)"
