<#
    E-mreLogo.jpg -> E-mreLogo.ico
    Logonun rengi / şekli değiştirilmez; yalnızca kare boyutlara yeniden örneklenir
    ve her boyut PNG sıkıştırmalı bir ICO girdisi olarak yazılır (Windows Vista+ standardı).
    Boyutlar: 16, 32, 48, 64, 128, 256
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\assets\E-mreLogo.jpg'),
    [string]$Target = (Join-Path $PSScriptRoot '..\assets\E-mreLogo.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Source = (Resolve-Path $Source).Path
$sizes = 16, 32, 48, 64, 128, 256
$src = [System.Drawing.Image]::FromFile($Source)
$frames = @()
try {
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.DrawImage($src, 0, 0, $s, $s)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $frames += , @{ Size = $s; Bytes = $ms.ToArray() }
        $ms.Dispose()
    }
}
finally { $src.Dispose() }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
# ICONDIR
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    # ICONDIRENTRY (256 is stored as 0)
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([Byte]$dim); $w.Write([Byte]$dim); $w.Write([Byte]0); $w.Write([Byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$f.Bytes.Length); $w.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes($Target, $out.ToArray())
$w.Dispose()

Write-Host "ICO oluşturuldu: $Target ($($sizes -join ', ') px)"
