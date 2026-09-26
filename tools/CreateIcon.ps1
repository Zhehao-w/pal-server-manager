param(
    [string]$Source = 'PalServerManager.WinUI\Assets\ManagerIconCircular.png',
    [string]$Destination = 'PalServerManager.WinUI\Assets\ManagerIcon.ico'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$bitmap = [System.Drawing.Bitmap]::FromFile($sourcePath)
$entries = [System.Collections.Generic.List[byte[]]]::new()
try {
    foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
        $resized = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($resized)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.DrawImage($bitmap, 0, 0, $size, $size)
            } finally { $graphics.Dispose() }
            # Enforce a true circular alpha mask during ICO packaging. This also
            # discards any isolated exterior pixels in the generated PNG source.
            $center = $size / 2.0
            $radius = $size / 2.0
            for ($y = 0; $y -lt $size; $y++) {
                for ($x = 0; $x -lt $size; $x++) {
                    $distance = [Math]::Sqrt(([Math]::Pow($x + 0.5 - $center, 2)) + ([Math]::Pow($y + 0.5 - $center, 2)))
                    $coverage = [Math]::Clamp($radius - $distance + 0.5, 0.0, 1.0)
                    $color = $resized.GetPixel($x, $y)
                    $alpha = [int][Math]::Round($color.A * $coverage)
                    if ($alpha -ne $color.A) {
                        $resized.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($alpha, $color.R, $color.G, $color.B))
                    }
                }
            }
            $stream = [System.IO.MemoryStream]::new()
            try {
                $resized.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                $entries.Add($stream.ToArray())
            } finally { $stream.Dispose() }
        } finally { $resized.Dispose() }
    }
} finally { $bitmap.Dispose() }

$stream = [System.IO.File]::Create($destinationPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    for ($index = 0; $index -lt $entries.Count; $index++) {
        $size = $sizes[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$entries[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $entries[$index].Length
    }
    foreach ($entry in $entries) { $writer.Write($entry) }
} finally { $writer.Dispose() }

Write-Output "Created $destinationPath with $($entries.Count) PNG icon sizes."
