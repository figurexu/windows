Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [float]$size
    $u = $s / 256.0

    # background: rounded square, blue-purple gradient
    $bgRect = New-Object System.Drawing.RectangleF((2 * $u), (2 * $u), (252 * $u), (252 * $u))
    $bgPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = (56 * $u) * 2
    $bgPath.AddArc($bgRect.X, $bgRect.Y, $d, $d, 180, 90)
    $bgPath.AddArc($bgRect.Right - $d, $bgRect.Y, $d, $d, 270, 90)
    $bgPath.AddArc($bgRect.Right - $d, $bgRect.Bottom - $d, $d, $d, 0, 90)
    $bgPath.AddArc($bgRect.X, $bgRect.Bottom - $d, $d, $d, 90, 90)
    $bgPath.CloseFigure()
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $bgRect,
        [System.Drawing.Color]::FromArgb(255, 0x4F, 0xC3, 0xF7),
        [System.Drawing.Color]::FromArgb(255, 0x8B, 0x5C, 0xF6),
        45.0)
    $g.FillPath($bgBrush, $bgPath)

    # soft inner glow: slightly lighter rounded rect inside (subtle)
    $innerRect = New-Object System.Drawing.RectangleF((20 * $u), (20 * $u), (216 * $u), (216 * $u))
    $innerPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $id = (48 * $u) * 2
    $innerPath.AddArc($innerRect.X, $innerRect.Y, $id, $id, 180, 90)
    $innerPath.AddArc($innerRect.Right - $id, $innerRect.Y, $id, $id, 270, 90)
    $innerPath.AddArc($innerRect.Right - $id, $innerRect.Bottom - $id, $id, $id, 0, 90)
    $innerPath.AddArc($innerRect.X, $innerRect.Bottom - $id, $id, $id, 90, 90)
    $innerPath.CloseFigure()
    $glowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(26, 255, 255, 255))
    $g.FillPath($glowBrush, $innerPath)

    # gauge: translucent white pie fill (225..495 = 270 deg, gap at bottom)
    $pieRect = New-Object System.Drawing.Rectangle([int](52 * $u), [int](52 * $u), [int](152 * $u), [int](152 * $u))
    $pieBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(52, 255, 255, 255))
    $g.FillPie($pieBrush, $pieRect, 225.0, 270.0)

    # gauge ring: white arc
    $ringPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(235, 255, 255, 255), (14 * $u))
    $ringPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $ringPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($ringPen, $pieRect, 225.0, 270.0)

    # needle: from center toward 0 deg (right), rounded
    $cx = 128 * $u; $cy = 128 * $u
    $needleLen = 62 * $u
    $nx = $cx + $needleLen; $ny = $cy
    $needlePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(240, 255, 255, 255), (10 * $u))
    $needlePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $needlePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($needlePen, $cx, $cy, $nx, $ny)

    # needle tip: accent dot
    $tipBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xFF, 0xD6, 0x6E))
    $g.FillEllipse($tipBrush, ($nx - 8 * $u), ($ny - 8 * $u), (16 * $u), (16 * $u))

    # center hub
    $hubBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $g.FillEllipse($hubBrush, ($cx - 13 * $u), ($cy - 13 * $u), (26 * $u), (26 * $u))

    $g.Dispose()
    return $bmp
}

$sizes = @(256, 64, 48, 32, 16)
$pngData = @{}
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngData[$s] = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()
}

$count = $sizes.Count
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$count)
$offset = 6 + 16 * $count
foreach ($s in $sizes) {
    $data = $pngData[$s]
    if ($s -ge 256) { $bw.Write([Byte]0) } else { $bw.Write([Byte]$s) }
    if ($s -ge 256) { $bw.Write([Byte]0) } else { $bw.Write([Byte]$s) }
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($pngData[$s]) }
$bw.Flush()
[IO.File]::WriteAllBytes("C:\Users\figur\Desktop\Windowsmonitor\app.ico", $out.ToArray())
$bw.Dispose()
$out.Dispose()

$bmp256 = New-IconBitmap 256
$bmp256.Save("C:\Users\figur\Desktop\Windowsmonitor\app_icon_256.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp256.Dispose()
Write-Output ("ICO written: " + (Get-Item "C:\Users\figur\Desktop\Windowsmonitor\app.ico").Length + " bytes")
