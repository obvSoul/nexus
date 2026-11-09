# Generates installer\nexus.ico (Onyx background, blurple N). Run once; the .ico is kept in the project.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot 'nexus.ico'
$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([Drawing.Color]::Transparent)
    $r = [math]::Max(2, [int]($s * 0.18))
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90); $path.AddArc($s - 2 * $r - 1, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($s - 2 * $r - 1, $s - 2 * $r - 1, 2 * $r, 2 * $r, 0, 90); $path.AddArc(0, $s - 2 * $r - 1, 2 * $r, 2 * $r, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 88, 101, 242))), $path)
    $font = New-Object Drawing.Font 'Segoe UI', ($s * 0.62), ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $g.DrawString('N', $font, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF 0, 0, $s, $s), $fmt)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [IO.File]::Create($out); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $b = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset); $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Output "Icone: $out"
