
Add-Type -AssemblyName System.Drawing
$sizes = @(16,32,48,64,128,256)
$bmp = New-Object System.Drawing.Bitmap(256,256)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::Transparent)
# 圆角方块
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 56; $w = 256; $h = 256
$path.AddArc(0,0,$r*2,$r*2,180,90)
$path.AddArc($w-$r*2,0,$r*2,$r*2,270,90)
$path.AddArc($w-$r*2,$h-$r*2,$r*2,$r*2,0,90)
$path.AddArc(0,$h-$r*2,$r*2,$r*2,90,90)
$path.CloseFigure()
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
  (New-Object System.Drawing.Point(0,0)),
  (New-Object System.Drawing.Point(256,256)),
  [System.Drawing.Color]::FromArgb(255,46,60,55),
  [System.Drawing.Color]::FromArgb(255,12,18,16))
$g.FillPath($brush, $path)
# 对勾
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255,79,191,149), 26)
$pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
$g.DrawLines($pen, @(
  (New-Object System.Drawing.Point(76,132)),
  (New-Object System.Drawing.Point(116,172)),
  (New-Object System.Drawing.Point(184,88))
))
$g.Dispose()

# 手写 ICO（多尺寸 PNG 内嵌）
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$pngs = @()
foreach ($s in $sizes) {
  $b2 = New-Object System.Drawing.Bitmap($s,$s)
  $g2 = [System.Drawing.Graphics]::FromImage($b2)
  $g2.InterpolationMode = 'HighQualityBicubic'
  $g2.DrawImage($bmp, 0, 0, $s, $s)
  $g2.Dispose()
  $m2 = New-Object System.IO.MemoryStream
  $b2.Save($m2, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += ,$m2.ToArray()
  $b2.Dispose()
}
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $len = $pngs[$i].Length
  $bw.Write([Byte]($s % 256)); $bw.Write([Byte]($s % 256))
  $bw.Write([Byte]0); $bw.Write([Byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
  $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
$out = Join-Path $PSScriptRoot 'app.ico'
[System.IO.File]::WriteAllBytes($out, $ms.ToArray())
$bw.Close(); $ms.Close(); $bmp.Dispose()
Write-Output ('ico written: ' + (Get-Item $out).Length + ' bytes')
