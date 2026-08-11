# Generates src/Lighthouse/lighthouse.ico — a walnut-and-gilt lighthouse drawn at
# every size Windows asks for, packed as PNG-compressed icon entries.
#
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1

Add-Type -AssemblyName System.Drawing

$root   = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outIco = Join-Path $root 'src\Lighthouse\lighthouse.ico'
$sizes  = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

$walnut     = [System.Drawing.Color]::FromArgb(255, 107, 79, 53)
$walnutDeep = [System.Drawing.Color]::FromArgb(255, 74, 53, 36)
$cream      = [System.Drawing.Color]::FromArgb(255, 244, 238, 226)
$gilt       = [System.Drawing.Color]::FromArgb(255, 168, 132, 63)

function New-LighthousePng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # Everything below is authored on a 48x48 grid and scaled to the target.
    $k = $size / 48.0
    $g.ScaleTransform($k, $k)

    $brWalnut = New-Object System.Drawing.SolidBrush($walnut)
    $brDeep   = New-Object System.Drawing.SolidBrush($walnutDeep)
    $brCream  = New-Object System.Drawing.SolidBrush($cream)
    $brGilt   = New-Object System.Drawing.SolidBrush($gilt)

    # Light beams, only where there are enough pixels to read them.
    if ($size -ge 32) {
        $beam = New-Object System.Drawing.Pen((
            [System.Drawing.Color]::FromArgb(150, $gilt.R, $gilt.G, $gilt.B)), (1.6))
        $beam.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $beam.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($beam, 17.5, 11.5, 4.0, 7.5)
        $g.DrawLine($beam, 30.5, 11.5, 44.0, 7.5)
        $g.DrawLine($beam, 17.5, 14.5, 5.0, 18.5)
        $g.DrawLine($beam, 30.5, 14.5, 43.0, 18.5)
        $beam.Dispose()
    }

    # Tower.
    $tower = New-Object 'System.Drawing.Drawing2D.GraphicsPath'
    $tower.AddPolygon(@(
        (New-Object System.Drawing.PointF(18.5, 16.5)),
        (New-Object System.Drawing.PointF(29.5, 16.5)),
        (New-Object System.Drawing.PointF(33.0, 41.5)),
        (New-Object System.Drawing.PointF(15.0, 41.5))
    ))
    $g.FillPath($brWalnut, $tower)

    # Two cream bands, clipped to the tower so they follow its taper.
    $saved = $g.Save()
    $g.SetClip($tower)
    $g.FillRectangle($brCream, 14.0, 23.0, 20.0, 3.2)
    $g.FillRectangle($brCream, 14.0, 32.0, 20.0, 3.2)
    $g.Restore($saved)
    $tower.Dispose()

    # Lantern room, roof and the light itself.
    $g.FillRectangle($brDeep, 17.0, 10.0, 14.0, 6.5)
    $roof = New-Object 'System.Drawing.Drawing2D.GraphicsPath'
    $roof.AddPolygon(@(
        (New-Object System.Drawing.PointF(15.5, 10.0)),
        (New-Object System.Drawing.PointF(32.5, 10.0)),
        (New-Object System.Drawing.PointF(24.0, 4.0))
    ))
    $g.FillPath($brDeep, $roof)
    $roof.Dispose()
    $g.FillEllipse($brGilt, 20.8, 10.4, 6.4, 5.6)

    # Base.
    $baseR = New-Object 'System.Drawing.Drawing2D.GraphicsPath'
    $baseR.AddRectangle((New-Object System.Drawing.RectangleF(11.5, 41.0, 25.0, 4.0)))
    $g.FillPath($brDeep, $baseR)
    $baseR.Dispose()

    $brWalnut.Dispose(); $brDeep.Dispose(); $brCream.Dispose(); $brGilt.Dispose()
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    # Comma-wrap: without it PowerShell unrolls the array into loose bytes.
    [byte[]]$bytes = $ms.ToArray()
    return ,$bytes
}

$images = New-Object System.Collections.ArrayList
foreach ($s in $sizes) { [void]$images.Add([byte[]](New-LighthousePng $s)) }

# ICO container: 6-byte header, then one 16-byte directory entry per image.
$fs = [System.IO.File]::Create($outIco)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type: icon
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = 0
    if ($s -lt 256) { $dim = $s }    # 0 means 256 in the ICO directory
    $bw.Write([Byte]$dim)            # width
    $bw.Write([Byte]$dim)            # height
    $bw.Write([Byte]0)               # palette size
    $bw.Write([Byte]0)               # reserved
    $bw.Write([UInt16]1)             # colour planes
    $bw.Write([UInt16]32)            # bits per pixel
    $bw.Write([UInt32]$images[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $images[$i].Length
}

foreach ($img in $images) { $bw.Write([byte[]]$img, 0, $img.Length) }

$bw.Flush(); $bw.Close(); $fs.Close()
Write-Output "Wrote $outIco ($([Math]::Round((Get-Item $outIco).Length / 1KB, 1)) KB, $($sizes.Count) sizes)"
