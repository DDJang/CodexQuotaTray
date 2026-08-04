[CmdletBinding()]
param(
    [string]$Source,
    [string]$Preview,
    [string]$Destination,
    [ValidateRange(0.05, 0.45)]
    [double]$CornerRadiusRatio = 0.20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Source) { $Source = Join-Path $scriptRoot "..\assets\app-icon-source.png" }
if (-not $Preview) { $Preview = Join-Path $scriptRoot "..\assets\app-icon.png" }
if (-not $Destination) { $Destination = Join-Path $scriptRoot "..\assets\app-icon.ico" }

function New-RoundedIconBitmap([Drawing.Image]$Image, [double]$RadiusRatio) {
    $bitmap = [Drawing.Bitmap]::new(
        $Image.Width,
        $Image.Height,
        [Drawing.Imaging.PixelFormat]::Format32bppArgb
    )
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $texture = $null
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias

        $inset = [single]([Math]::Max(1.0, [Math]::Min($Image.Width, $Image.Height) * 0.0125))
        $width = [single]($Image.Width - 2.0 * $inset)
        $height = [single]($Image.Height - 2.0 * $inset)
        $radius = [single]([Math]::Min($width, $height) * $RadiusRatio)
        $diameter = [single](2.0 * $radius)
        $right = [single]($inset + $width)
        $bottom = [single]($inset + $height)

        $path.AddArc($inset, $inset, $diameter, $diameter, 180, 90)
        $path.AddArc($right - $diameter, $inset, $diameter, $diameter, 270, 90)
        $path.AddArc($right - $diameter, $bottom - $diameter, $diameter, $diameter, 0, 90)
        $path.AddArc($inset, $bottom - $diameter, $diameter, $diameter, 90, 90)
        $path.CloseFigure()

        $texture = [Drawing.TextureBrush]::new(
            $Image,
            [Drawing.Drawing2D.WrapMode]::Clamp
        )
        $graphics.FillPath($texture, $path)
        return $bitmap
    } catch {
        $bitmap.Dispose()
        throw
    } finally {
        if ($null -ne $texture) {
            $texture.Dispose()
        }
        $path.Dispose()
        $graphics.Dispose()
    }
}

function Assert-IconContainer([string]$Path, [int[]]$ExpectedSizes) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1) {
            throw "generated ICO has an invalid header"
        }
        $count = $reader.ReadUInt16()
        if ($count -ne $ExpectedSizes.Count) {
            throw "generated ICO contains $count frames; expected $($ExpectedSizes.Count)"
        }
        for ($index = 0; $index -lt $count; $index++) {
            $width = [int]$reader.ReadByte()
            $height = [int]$reader.ReadByte()
            if ($width -eq 0) { $width = 256 }
            if ($height -eq 0) { $height = 256 }
            $reader.ReadByte() | Out-Null
            $reader.ReadByte() | Out-Null
            $planes = $reader.ReadUInt16()
            $bits = $reader.ReadUInt16()
            $bytes = $reader.ReadUInt32()
            $offset = $reader.ReadUInt32()
            if ($width -ne $ExpectedSizes[$index] -or $height -ne $ExpectedSizes[$index]) {
                throw "ICO frame $index has unexpected dimensions ${width}x${height}"
            }
            if ($planes -ne 1 -or $bits -ne 32 -or $bytes -eq 0 -or $offset -ge $stream.Length) {
                throw "ICO frame $index has invalid metadata"
            }
        }
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$sourcePath = [IO.Path]::GetFullPath($Source)
$previewPath = [IO.Path]::GetFullPath($Preview)
$destinationPath = [IO.Path]::GetFullPath($Destination)
$sourceImage = [Drawing.Image]::FromFile($sourcePath)
$roundedImage = $null
$frames = [Collections.Generic.List[byte[]]]::new()
try {
    $roundedImage = New-RoundedIconBitmap $sourceImage $CornerRadiusRatio
    $roundedImage.Save($previewPath, [Drawing.Imaging.ImageFormat]::Png)

    $corner = $roundedImage.GetPixel(0, 0)
    $center = $roundedImage.GetPixel(
        [int]($roundedImage.Width / 2),
        [int]($roundedImage.Height / 2)
    )
    if ($corner.A -ne 0) {
        throw "rounded preview corner must be fully transparent"
    }
    # Transparent icon sources may have a transparent or antialiased center.
    # The rounded clipping invariant is the corner alpha check above; do not
    # require the geometric center to be opaque.

    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new(
            $size,
            $size,
            [Drawing.Imaging.PixelFormat]::Format32bppArgb
        )
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([Drawing.Color]::Transparent)
                $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.DrawImage($roundedImage, 0, 0, $size, $size)
            } finally {
                $graphics.Dispose()
            }
            $stream = [IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
                $frames.Add($stream.ToArray())
            } finally {
                $stream.Dispose()
            }
        } finally {
            $bitmap.Dispose()
        }
    }
} finally {
    if ($null -ne $roundedImage) {
        $roundedImage.Dispose()
    }
    $sourceImage.Dispose()
}

$output = [IO.File]::Create($destinationPath)
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $sizeByte = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$sizeByte)
        $writer.Write([byte]$sizeByte)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) {
        $writer.Write($frame)
    }
} finally {
    $writer.Dispose()
    $output.Dispose()
}

Assert-IconContainer $destinationPath $sizes
[pscustomobject]@{
    Source = $sourcePath
    Preview = $previewPath
    Destination = $destinationPath
    CornerAlpha = 0
    CenterAlpha = $center.A
    CornerRadiusPercent = [Math]::Round($CornerRadiusRatio * 100, 1)
    Sizes = ($sizes -join ",")
    Bytes = (Get-Item -LiteralPath $destinationPath).Length
}
