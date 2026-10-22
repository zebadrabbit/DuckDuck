# Renders the brand mark (assets/brand/duckduck-icon.svg, transcribed below) with GDI+ into
# duck.ico (one PNG entry per size) and assets/brand/duckduck-icon-512.png. Run once; outputs are committed.
Add-Type -AssemblyName System.Drawing

$tile  = [System.Drawing.ColorTranslator]::FromHtml('#1A1B1E')
$amber = [System.Drawing.ColorTranslator]::FromHtml('#F5A623')
$beak  = [System.Drawing.ColorTranslator]::FromHtml('#E0731D')

# SVG quadratic (P0, Q, P2) → GDI+ cubic control points
function Quad($path, $x0, $y0, $qx, $qy, $x2, $y2) {
  $c1x = $x0 + 2/3 * ($qx - $x0); $c1y = $y0 + 2/3 * ($qy - $y0)
  $c2x = $x2 + 2/3 * ($qx - $x2); $c2y = $y2 + 2/3 * ($qy - $y2)
  $path.AddBezier([float]$x0, [float]$y0, [float]$c1x, [float]$c1y, [float]$c2x, [float]$c2y, [float]$x2, [float]$y2)
}

function Render([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.ScaleTransform([float]($s / 256), [float]($s / 256))

  # <rect width=256 height=256 rx=56>
  $r = 56; $tp = New-Object System.Drawing.Drawing2D.GraphicsPath
  $tp.AddArc(0, 0, $r*2, $r*2, 180, 90); $tp.AddArc(256-$r*2, 0, $r*2, $r*2, 270, 90)
  $tp.AddArc(256-$r*2, 256-$r*2, $r*2, $r*2, 0, 90); $tp.AddArc(0, 256-$r*2, $r*2, $r*2, 90, 90); $tp.CloseFigure()
  $g.FillPath((New-Object System.Drawing.SolidBrush $tile), $tp)

  # <g transform="translate(128 132) scale(0.86) translate(-124 -140)">  (GDI+ prepends, so same call order as SVG)
  $g.TranslateTransform(128, 132); $g.ScaleTransform(0.86, 0.86); $g.TranslateTransform(-124, -140)
  $a = New-Object System.Drawing.SolidBrush $amber

  $g.FillPolygon($a, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(46,143), [System.Drawing.PointF]::new(10,113), [System.Drawing.PointF]::new(51,179)))   # tail
  $g.FillEllipse($a, [float](115-80), [float](164-46), [float]160, [float]92)                                                                                      # body
  $neck = New-Object System.Drawing.Drawing2D.GraphicsPath                                                                                                           # M136 128 Q150 118 146 104 L186 104 Q188 140 176 150 Z
  Quad $neck 136 128 150 118 146 104; $neck.AddLine([float]146, [float]104, [float]186, [float]104); Quad $neck 186 104 188 140 176 150; $neck.CloseFigure()
  $g.FillPath($a, $neck)
  $g.FillEllipse($a, [float](166-40), [float](98-40), [float]80, [float]80)                                                                                         # head
  $bk = New-Object System.Drawing.Drawing2D.GraphicsPath                                                                                                             # M200 82 L242 100 Q246 102 242 104 L200 121 Z
  $bk.AddLine([float]200, [float]82, [float]242, [float]100); Quad $bk 242 100 246 102 242 104; $bk.AddLine([float]242, [float]104, [float]200, [float]121); $bk.CloseFigure()
  $g.FillPath((New-Object System.Drawing.SolidBrush $beak), $bk)
  $g.FillEllipse((New-Object System.Drawing.SolidBrush $tile), [float](178-8), [float](88-8), [float]16, [float]16)                                                 # eye
  $g.Dispose()
  $bmp
}

function Png($bmp) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); ,$ms.ToArray() }

$big = Render 512
[System.IO.File]::WriteAllBytes("$PSScriptRoot\assets\brand\duckduck-icon-512.png", (Png $big))

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = New-Object System.Collections.ArrayList
foreach ($s in $sizes) { [void]$pngs.Add((Png (Render $s))) }

# ICO container with PNG-compressed entries (Vista+)
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $len = $pngs[$i].Length
  $dim = if ($s -ge 256) { 0 } else { $s }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
  $offset += $len
}
foreach ($p in $pngs) { $w.Write([byte[]]$p) }
$w.Flush()
[System.IO.File]::WriteAllBytes("$PSScriptRoot\duck.ico", $out.ToArray())
Write-Host "wrote duck.ico ($($out.Length) bytes) and assets\brand\duckduck-icon-512.png"
