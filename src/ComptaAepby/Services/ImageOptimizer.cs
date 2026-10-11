using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ComptaAepby;

/// <summary>Réduit les photos et scans volumineux avant de les joindre comme justificatif : redimensionnement (côté le
/// plus long limité) et recompression JPEG. Perte de qualité maîtrisée mais réelle (la lisibilité d'un ticket ou d'une
/// facture est conservée) ; le fichier d'origine n'est jamais modifié. Sans effet si le gain est faible.</summary>
public static class ImageOptimizer
{
    private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    /// <summary>En dessous de cette taille, on ne touche à rien.</summary>
    private const int MinBytes = 300 * 1024;
    private const int MaxSide = 2000;
    private const int JpegQuality = 80;

    public static bool IsOptimizable(string fileName) =>
        Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Renvoie le contenu optimisé (nom en .jpg) si le gain est d'au moins 15 %, sinon l'original.</summary>
    public static (string Name, byte[] Data, bool Optimized) Optimize(string fileName, byte[] data)
    {
        if (!IsOptimizable(fileName) || data.Length < MinBytes) return (fileName, data, false);
        try
        {
            BitmapSource source;
            using (var input = new MemoryStream(data))
            {
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                source = frame;
                var orientation = ReadOrientation(frame);
                if (orientation is 3 or 6 or 8)
                {
                    var angle = orientation == 3 ? 180 : orientation == 6 ? 90 : 270;
                    source = new TransformedBitmap(source, new RotateTransform(angle));
                }
            }

            var longest = Math.Max(source.PixelWidth, source.PixelHeight);
            if (longest > MaxSide)
            {
                var scale = (double)MaxSide / longest;
                source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }
            // JPEG sans transparence : on aplatit sur du blanc.
            source = Flatten(source);

            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            var result = output.ToArray();
            if (result.Length > data.Length * 0.85) return (fileName, data, false);
            return (Path.ChangeExtension(fileName, ".jpg"), result, true);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException
                                       or ArgumentException or IOException)
        {
            return (fileName, data, false);
        }
    }

    private static BitmapSource Flatten(BitmapSource source)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }
        var target = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var converted = new FormatConvertedBitmap(target, PixelFormats.Bgr24, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>Orientation EXIF (1 = normale ; 3, 6, 8 = rotations) ; 1 si absente ou illisible.</summary>
    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("/app1/ifd/{ushort=274}") is ushort o) return o;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // pas de métadonnées exploitables
        }
        return 1;
    }
}
