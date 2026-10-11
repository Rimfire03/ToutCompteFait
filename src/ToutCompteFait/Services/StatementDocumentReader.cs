using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using ToutCompteFait.Core.Import;
using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ToutCompteFait.Services;

/// <summary>Une page lue : mots positionnés + image à afficher pour la vérification.</summary>
public sealed record ReadPage(StatementPage Page, System.Windows.Media.ImageSource? Image, bool UsedOcr);

public sealed class ReadDocument
{
    public List<ReadPage> Pages { get; } = new();
    public string Method { get; set; } = "";
    public IReadOnlyList<StatementPage> StatementPages => Pages.Select(p => p.Page).ToList();
}

public sealed class OcrUnavailableException : Exception
{
    public OcrUnavailableException(string message) : base(message) { }
}

/// <summary>
/// Lit un relevé PDF ou une image :
/// <list type="number">
/// <item>PDF « numérique » (texte sélectionnable) : le texte et sa position sont lus directement, sans OCR, donc sans
/// erreur de reconnaissance ;</item>
/// <item>PDF scanné ou image : rendu des pages puis OCR intégré à Windows (hors ligne, rien n'est envoyé sur Internet).</item>
/// </list>
/// Rien n'est installé en plus : Windows.Data.Pdf et Windows.Media.Ocr font partie de Windows 10/11 (le pack de langue
/// français doit être présent pour l'OCR).
/// </summary>
public static class StatementDocumentReader
{
    public static readonly string[] SupportedExtensions = { ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif" };

    /// <summary>Largeur de rendu des pages PDF pour l'OCR (≈ 300 dpi pour une page A4).</summary>
    private const uint PdfRenderWidth = 2480;
    private const int MinWordsForDigitalPage = 12;

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static async Task<ReadDocument> ReadAsync(string path, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Fichier introuvable.", path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".pdf"
            ? await ReadPdfAsync(path, progress, ct)
            : await ReadImageAsync(path, progress, ct);
    }

    // ------------------------------------------------------------------ PDF

    private static async Task<ReadDocument> ReadPdfAsync(string path, IProgress<string>? progress, CancellationToken ct)
    {
        var doc = new ReadDocument();

        // 1) texte numérique éventuel
        progress?.Report("Lecture du texte du PDF…");
        var digital = await Task.Run(() => ReadDigitalText(path), ct);

        // 2) rendu des pages (image de contrôle, et entrée de l'OCR si la page n'a pas de texte)
        StorageFile file;
        PdfDocument pdf;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            pdf = await PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (digital is { Count: > 0 } && digital.All(p => p.Words.Count >= MinWordsForDigitalPage))
            {
                // Lecture possible sans images (affichage de contrôle indisponible).
                foreach (var p in digital) doc.Pages.Add(new ReadPage(p, null, false));
                doc.Method = "texte du PDF (sans image de contrôle)";
                return doc;
            }
            throw new IOException("Ce PDF ne peut pas être ouvert (protégé par mot de passe ou corrompu) : " + ex.Message, ex);
        }

        OcrEngine? engine = null;
        var usedOcr = false;
        for (uint i = 0; i < pdf.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Page {i + 1} / {pdf.PageCount}…");
            using var page = pdf.GetPage(i);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = PdfRenderWidth });
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var image = await ToDisplayImageAsync(bitmap);

            var text = digital is not null && i < digital.Count ? digital[(int)i] : null;
            if (text is not null && text.Words.Count >= MinWordsForDigitalPage)
            {
                doc.Pages.Add(new ReadPage(text, image, false));
                continue;
            }
            engine ??= CreateEngine();
            progress?.Report($"Page {i + 1} / {pdf.PageCount} : reconnaissance du texte (OCR)…");
            var sp = await RecognizeAsync(engine, bitmap, (int)i + 1);
            doc.Pages.Add(new ReadPage(sp, image, true));
            usedOcr = true;
        }
        doc.Method = usedOcr
            ? (doc.Pages.Any(p => !p.UsedOcr) ? "texte du PDF et OCR Windows" : "OCR Windows")
            : "texte du PDF (sans OCR)";
        return doc;
    }

    private static List<StatementPage>? ReadDigitalText(string path)
    {
        try
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
            var pages = new List<StatementPage>();
            foreach (var page in pdf.GetPages())
            {
                var words = new List<StatementWord>();
                foreach (var w in page.GetWords())
                {
                    if (string.IsNullOrWhiteSpace(w.Text)) continue;
                    var b = w.BoundingBox;
                    var h = Math.Max(b.Top - b.Bottom, 1);
                    words.Add(new StatementWord(w.Text, b.Left, page.Height - b.Top, Math.Max(b.Right - b.Left, 1), h));
                }
                pages.Add(new StatementPage(page.Number, page.Width, page.Height, words));
            }
            return pages;
        }
        catch (Exception)
        {
            // PDF illisible pour PdfPig : l'OCR prendra le relais si Windows sait le rendre.
            return null;
        }
    }

    // ------------------------------------------------------------------ images

    private static async Task<ReadDocument> ReadImageAsync(string path, IProgress<string>? progress, CancellationToken ct)
    {
        var doc = new ReadDocument { Method = "OCR Windows" };
        var engine = CreateEngine();
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Image {i + 1} / {decoder.FrameCount} : reconnaissance du texte (OCR)…");
            var frame = await decoder.GetFrameAsync(i);
            var transform = new BitmapTransform();
            var w = frame.PixelWidth;
            var h = frame.PixelHeight;
            // Windows OCR donne de meilleurs résultats avec du texte d'au moins ~20 px de haut ; il refuse les images trop grandes.
            var scale = 1.0;
            if (w < 1500) scale = Math.Min(2.0, 3000.0 / w);
            var max = (double)OcrEngine.MaxImageDimension;
            if (Math.Max(w, h) * scale > max) scale = max / Math.Max(w, h);
            if (Math.Abs(scale - 1.0) > 0.01)
            {
                transform.ScaledWidth = (uint)Math.Round(w * scale);
                transform.ScaledHeight = (uint)Math.Round(h * scale);
                transform.InterpolationMode = BitmapInterpolationMode.Fant;
            }
            using var bitmap = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            var image = await ToDisplayImageAsync(bitmap);
            var sp = await RecognizeAsync(engine, bitmap, (int)i + 1);
            doc.Pages.Add(new ReadPage(sp, image, true));
        }
        return doc;
    }

    // ------------------------------------------------------------------ OCR

    private static OcrEngine CreateEngine()
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Language("fr-FR"))
                     ?? OcrEngine.TryCreateFromLanguage(new Language("fr"))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            throw new OcrUnavailableException(
                "La reconnaissance de texte de Windows n'est pas disponible en français sur ce poste.\n\n" +
                "Installez-la : Paramètres Windows → Heure et langue → Langue et région → Français → Options de langue → " +
                "« Reconnaissance optique de caractères » (OCR), puis relancez la lecture.");
        return engine;
    }

    private static async Task<StatementPage> RecognizeAsync(OcrEngine engine, SoftwareBitmap bitmap, int number)
    {
        var result = await engine.RecognizeAsync(bitmap);
        var words = new List<StatementWord>();
        foreach (var line in result.Lines)
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                if (string.IsNullOrWhiteSpace(w.Text) || r.Height <= 0) continue;
                words.Add(new StatementWord(w.Text, r.X, r.Y, Math.Max(r.Width, 1), r.Height));
            }
        return new StatementPage(number, bitmap.PixelWidth, bitmap.PixelHeight, words);
    }

    /// <summary>Image d'affichage (PNG décodé à ~1 500 px de large : la mémoire reste raisonnable pour 10 pages).</summary>
    private static async Task<System.Windows.Media.ImageSource> ToDisplayImageAsync(SoftwareBitmap bitmap)
    {
        using var ms = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
        encoder.SetSoftwareBitmap(SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore));
        await encoder.FlushAsync();
        ms.Seek(0);
        var bytes = new byte[ms.Size];
        await ms.ReadAsync(bytes.AsBuffer(), (uint)ms.Size, InputStreamOptions.None);
        var img = new System.Windows.Media.Imaging.BitmapImage();
        img.BeginInit();
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = 1500;
        img.StreamSource = new MemoryStream(bytes);
        img.EndInit();
        img.Freeze();
        return img;
    }
}
