<#
.SYNOPSIS
    Genere src\ToutCompteFait\Assets\app.ico (logo « camembert » de Tout Compte Fait, assets\logos\logo-12-camembert.svg)
    et src\ToutCompteFait\Assets\logo.png (256 px) avec System.Drawing.
#>
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "src\ToutCompteFait\Assets"

function Col([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

# $label : « ASSO » / « EI » (pastille de version en bas centré, comme dans l'application) ; vide = logo seul.
function Draw-Logo([int]$size, [string]$label = "") {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)
    if ($label) {
        # La pastille dépasse sous le carré (152 unités de haut au total) : le tout est mis à l'échelle et centré.
        $g.ScaleTransform($size / 152.0, $size / 152.0)
        $g.TranslateTransform(12, 0)
    } else {
        $g.ScaleTransform($size / 128.0, $size / 128.0)
    }

    # fond arrondi (rx 28)
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 28; $d = $r * 2
    $bg.AddArc(0, 0, $d, $d, 180, 90); $bg.AddArc(128 - $d, 0, $d, $d, 270, 90)
    $bg.AddArc(128 - $d, 128 - $d, $d, $d, 0, 90); $bg.AddArc(0, 128 - $d, $d, $d, 90, 90); $bg.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush (Col "#EFF6FF")), $bg)

    # anneau : cercle r=34, épaisseur 24, départ en haut, sens horaire
    $rect = New-Object System.Drawing.RectangleF 30, 30, 68, 68
    foreach ($seg in @(@("#2563EB", -90.0, 141.6), @("#14B8A6", 61.7, 87.6), @("#F59E0B", 159.4, 57.3))) {
        $pen = New-Object System.Drawing.Pen (Col $seg[0]), 24
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Miter
        $g.DrawArc($pen, $rect, [single]$seg[1], [single]$seg[2])
    }

    # centre blanc + symbole euro
    $g.FillEllipse([System.Drawing.Brushes]::White, 42, 42, 44, 44)
    $pen = New-Object System.Drawing.Pen (Col "#1E3A8A"), 4.5
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round; $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($pen, [single]53.86, [single]54, [single]20, [single]20, [single]44.4, [single]271.2)
    $g.DrawLine($pen, 54, 61, 67, 61)
    $g.DrawLine($pen, 54, 67, 67, 67)

    # étincelle
    $star = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF 104, 20), (New-Object System.Drawing.PointF 106.5, 26.5),
        (New-Object System.Drawing.PointF 113, 29), (New-Object System.Drawing.PointF 106.5, 31.5),
        (New-Object System.Drawing.PointF 104, 38), (New-Object System.Drawing.PointF 101.5, 31.5),
        (New-Object System.Drawing.PointF 95, 29), (New-Object System.Drawing.PointF 101.5, 26.5))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush (Col "#F59E0B")), $star)

    # pastille de version, bas centré (mêmes dimensions que le contrôle AppLogo de l'application)
    if ($label) {
        $pw = if ($label -eq "EI") { 70.0 } else { 108.0 }; $ph = 44.0; $px = (128 - $pw) / 2; $py = 108.0
        $fill = if ($label -eq "EI") { "#C2410C" } else { "#1E3A8A" }
        $pill = New-Object System.Drawing.Drawing2D.GraphicsPath
        $rr = 14.0; $dd = $rr * 2
        $pill.AddArc($px, $py, $dd, $dd, 180, 90); $pill.AddArc($px + $pw - $dd, $py, $dd, $dd, 270, 90)
        $pill.AddArc($px + $pw - $dd, $py + $ph - $dd, $dd, $dd, 0, 90); $pill.AddArc($px, $py + $ph - $dd, $dd, $dd, 90, 90); $pill.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush (Col $fill)), $pill)
        $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 3.5), $pill)
        # Texte seulement quand la taille le permet (illisible sous 32 px : la couleur de la pastille suffit).
        if ($size -ge 32) {
            $font = New-Object System.Drawing.Font("Bahnschrift", 32, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            $sf = New-Object System.Drawing.StringFormat
            $sf.Alignment = [System.Drawing.StringAlignment]::Center; $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
            $g.DrawString($label, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF $px, $py, $pw, $ph), $sf)
        }
    }
    $g.Dispose()
    return $bmp
}

function Write-Ico([string]$path, [string]$label) {
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $pngs = @{}
    foreach ($s in $sizes) {
        $bmp = Draw-Logo $s $label
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs[$s] = $ms.ToArray()
        if ($s -eq 256 -and -not $label) { [IO.File]::WriteAllBytes((Join-Path $assets "logo.png"), $pngs[$s]) }
        $bmp.Dispose()
    }
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    foreach ($s in $sizes) {
        $b = $pngs[$s]
        $dim = if ($s -ge 256) { 0 } else { $s }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$b.Length); $w.Write([uint32]$offset)
        $offset += $b.Length
    }
    foreach ($s in $sizes) { $w.Write($pngs[$s]) }
    [IO.File]::WriteAllBytes($path, $out.ToArray())
}

# logo.png : logo seul. Icônes par version : app.ico (historique, ancienne icône sans pastille),
# app-asso.ico et app-autoentreprise.ico (avec pastille « ASSO » / « EI »).
Write-Ico (Join-Path $assets "app.ico") ""
Write-Ico (Join-Path $assets "app-asso.ico") "ASSO"
Write-Ico (Join-Path $assets "app-autoentreprise.ico") "EI"
Write-Host "app.ico, app-asso.ico, app-autoentreprise.ico + logo.png ecrits dans $assets"
