using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToutCompteFait.Core.Import;

namespace ToutCompteFait.Services;

/// <summary>
/// Diagnostic de bout en bout (<c>--ocrtest &lt;dossier&gt;</c>) : fabrique un relevé factice dont on connaît la bonne réponse,
/// l'écrit en PDF numérique puis le dessine en image, et le fait lire par le vrai chemin de l'application
/// (texte du PDF, puis rendu + OCR Windows). Écrit un rapport <c>ocrtest.txt</c>. Aucune donnée réelle.
/// </summary>
public static class OcrSelfTest
{
    private static readonly SampleOp[] Ops =
    {
        new(new DateOnly(2026, 9, 1), "VIR SEPA DUPONT COTISATION", 2500),
        new(new DateOnly(2026, 9, 2), "PRLV EDF FACTURE SEPTEMBRE", -12345, "REF 987654 CONTRAT 12"),
        new(new DateOnly(2026, 9, 3), "CB LECLERC 03/09", -4580),
        new(new DateOnly(2026, 9, 4), "REMISE CHEQUES 0123456", 123456),
        new(new DateOnly(2026, 9, 7), "VIR LOTO RECETTES", 1234567),
        new(new DateOnly(2026, 9, 8), "FRAIS TENUE DE COMPTE", -1290),
        new(new DateOnly(2026, 9, 9), "CHEQUE 0001234", -30000),
        new(new DateOnly(2026, 9, 10), "VIR SEPA MARTIN LOCATION SALLE", 15000, "SAMEDI 12 SEPTEMBRE"),
        new(new DateOnly(2026, 9, 14), "CB AMAZON", -2999),
        new(new DateOnly(2026, 9, 15), "PRLV ASSURANCE MAIF", -9876),
        new(new DateOnly(2026, 9, 16), "VIR PAYPAL", 4550),
        new(new DateOnly(2026, 9, 22), "RETRAIT DAB", -6000),
        new(new DateOnly(2026, 9, 25), "VIR SUBVENTION MAIRIE", 100000),
        new(new DateOnly(2026, 9, 30), "AGIOS", -123)
    };

    public static async Task<int> RunAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var report = new StringBuilder();
        var failures = 0;
        void Line(string s) => report.AppendLine(s);
        void Check(bool ok, string name, string? detail = null)
        {
            if (!ok) failures++;
            Line((ok ? "  ok    " : "  ECHEC ") + name + (ok || detail is null ? "" : " -> " + detail));
        }

        try
        {
            var pages = StatementSample.Build(Ops, 150075, new SampleOptions { RunningBalance = true, RowsPerPage = 8 });
            var pdf = Path.Combine(dir, "releve-exemple.pdf");
            File.WriteAllBytes(pdf, WritePdf(pages));
            Line($"PDF numérique : {pdf} ({pages.Count} pages)");

            // 1) PDF numérique : lecture directe du texte
            var doc = await StatementDocumentReader.ReadAsync(pdf);
            Line("Méthode : " + doc.Method);
            Check(doc.Method.Contains("sans OCR"), "PDF numérique lu sans OCR", doc.Method);
            Compare(StatementLayoutParser.Parse(doc.StatementPages), "PDF numérique", Check, Line);

            // 2) image : dessin → OCR Windows
            var png = Path.Combine(dir, "releve-exemple.png");
            RenderPng(pages[0], png);
            Line($"Image : {png}");
            var imgDoc = await StatementDocumentReader.ReadAsync(png);
            var words = imgDoc.Pages[0].Page.Words;
            Line($"OCR : {words.Count} mots reconnus sur la page 1");
            foreach (var w in words.OrderBy(w => w.CenterY).ThenBy(w => w.X).Take(70))
                Line($"      [{w.X:0},{w.Y:0} {w.Width:0}x{w.Height:0}] {w.Text}");
            var ocr = StatementLayoutParser.Parse(imgDoc.StatementPages);
            foreach (var m in ocr.Messages) Line("    message : " + m);
            // Une seule page dessinée : on ne compare que les opérations de cette page.
            var expectedPage1 = Ops.Take(8).ToList();
            Line($"Lignes lues : {ocr.Rows.Count} (attendu {expectedPage1.Count} sur la page 1)");
            var ok = 0;
            for (var i = 0; i < Math.Min(ocr.Rows.Count, expectedPage1.Count); i++)
            {
                var r = ocr.Rows[i];
                var e = expectedPage1[i];
                var same = r.Date == e.Date && r.AmountCents == e.Cents;
                if (same) ok++;
                else Line($"    ligne {i + 1} : lu {r.Date:dd/MM/yyyy} « {r.Label} » {r.AmountCents} ; attendu {e.Date:dd/MM/yyyy} {e.Cents}");
            }
            Check(ocr.Rows.Count == expectedPage1.Count && ok == expectedPage1.Count,
                "OCR Windows : dates et montants exacts sur la page dessinée", $"{ok}/{expectedPage1.Count} exacts");
            var check = StatementChecker.Evaluate(ocr);
            Line("Contrôle : " + check.State + " — " + string.Join(" | ", check.Details));
        }
        catch (OcrUnavailableException ex)
        {
            Line("OCR INDISPONIBLE : " + ex.Message);
            failures++;
        }
        catch (Exception ex)
        {
            Line("ERREUR : " + ex);
            failures++;
        }
        Line(failures == 0 ? "TOUT EST OK" : $"{failures} ECHEC(S)");
        File.WriteAllText(Path.Combine(dir, "ocrtest.txt"), report.ToString(), new UTF8Encoding(true));
        return failures;
    }

    /// <summary>Diagnostic <c>--ocrui &lt;dossier de PDF&gt; &lt;capture.png&gt;</c> : ouvre l'écran de lecture sur tous les PDF du
    /// dossier, attend la fin de la lecture, écrit une capture et l'état dans un .txt à côté.</summary>
    public static async Task<int> UiShotAsync(string dir, string png)
    {
        var files = Directory.GetFiles(dir, "*.pdf").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var win = new Dialogs.StatementOcrWindow(files) { Width = 1400, Height = 860 };
        win.Show();
        for (var i = 0; i < 600 && (win.StatusTitle.StartsWith("Lecture en cours") || win.StatusTitle.Length == 0); i++)
            await Task.Delay(100);
        await Task.Delay(500);
        win.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)win.ActualWidth, (int)win.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(win);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(png)) enc.Save(fs);
        File.WriteAllText(Path.ChangeExtension(png, ".txt"),
            $"TITRE: {win.StatusTitle}\nIMPORT POSSIBLE: {win.CanUse}\n{win.StatusDetails}\n", new UTF8Encoding(true));
        win.Close();
        return 0;
    }

    /// <summary>Diagnostic <c>--readstatement &lt;fichier&gt; &lt;sortie.txt&gt;</c> : lit un vrai relevé et écrit les lignes de
    /// texte positionnées puis le résultat de la lecture (pour affiner un profil).</summary>
    public static async Task<int> DumpAsync(string path, string outFile, string? profileName = null)
    {
        var sb = new StringBuilder();
        try
        {
            var doc = await StatementDocumentReader.ReadAsync(path);
            sb.AppendLine($"Méthode : {doc.Method} ; {doc.Pages.Count} page(s)");
            foreach (var rp in doc.Pages)
            {
                sb.AppendLine($"--- page {rp.Page.Number} ({rp.Page.Width:0}x{rp.Page.Height:0}) OCR={rp.UsedOcr} mots={rp.Page.Words.Count}");
                var words = rp.Page.Words.OrderBy(w => w.CenterY).ToList();
                var lineWords = new List<StatementWord>();
                double cy = double.NaN;
                void Flush()
                {
                    if (lineWords.Count == 0) return;
                    sb.AppendLine(string.Join("  ", lineWords.OrderBy(w => w.X).Select(w => $"{w.Text}@{w.X:0}-{w.Right:0}")));
                    lineWords.Clear();
                }
                foreach (var w in words)
                {
                    if (!double.IsNaN(cy) && Math.Abs(w.CenterY - cy) > Math.Max(w.Height * 0.55, 2)) Flush();
                    if (lineWords.Count == 0) cy = w.CenterY;
                    lineWords.Add(w);
                }
                Flush();
            }
            var profile = StatementProfiles.BuiltIn().FirstOrDefault(p => profileName is not null && p.Name.StartsWith(profileName, StringComparison.OrdinalIgnoreCase))
                          ?? StatementProfiles.Generic();
            var sections = StatementLayoutParser.ParseSections(doc.StatementPages, profile);
            foreach (var res in sections)
            {
                sb.AppendLine($"=== SECTION compte={res.AccountNumber} « {res.AccountLabel} » lignes={res.Rows.Count}");
                sb.AppendLine($"profil={profile.Name} ouverture={res.OpeningCents} clôture={res.ClosingCents} totalDébits={res.TotalDebitsCents} totalCrédits={res.TotalCreditsCents} période={res.PeriodStart}..{res.PeriodEnd}");
                foreach (var m in res.Messages) sb.AppendLine("message : " + m);
                foreach (var row in res.Rows)
                    sb.AppendLine($"p{row.Page} {row.Date:dd/MM/yyyy} [{row.DateText}] {row.AmountCents,12} bal={row.BalanceAfterCents} {(row.Repaired ? "R " : "")}{string.Join(";", row.Notes)} | {row.Label}");
                var chk = StatementChecker.Evaluate(res);
                sb.AppendLine($"=== CONTRÔLE {chk.State} incomplètes={chk.IncompleteRows} somme={chk.SumCents}");
                foreach (var d in chk.Details) sb.AppendLine("  " + d);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("ERREUR : " + ex);
        }
        File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true));
        return 0;
    }

    private static void Compare(StatementReadResult r, string name, Action<bool, string, string?> check, Action<string> line)
    {
        var okRows = r.Rows.Count == Ops.Length;
        var allSame = okRows;
        for (var i = 0; okRows && i < Ops.Length; i++)
            if (r.Rows[i].Date != Ops[i].Date || r.Rows[i].AmountCents != Ops[i].Cents)
            {
                allSame = false;
                line($"    ligne {i + 1} : lu {r.Rows[i].Date:dd/MM/yyyy} « {r.Rows[i].Label} » {r.Rows[i].AmountCents} ; attendu {Ops[i].Date:dd/MM/yyyy} {Ops[i].Cents}");
            }
        check(allSame, name + " : toutes les opérations exactes", $"{r.Rows.Count} lignes lues / {Ops.Length}");
        var c = StatementChecker.Evaluate(r);
        check(c.CanImport, name + " : contrôle arithmétique vérifié", string.Join(" | ", c.Details));
    }

    // ------------------------------------------------------------------ dessin PNG

    private static void RenderPng(StatementPage page, string path)
    {
        const double scale = 2.0;
        var w = (int)(page.Width * scale);
        var h = (int)(page.Height * scale);
        var visual = new DrawingVisual();
        var typeface = new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            foreach (var word in page.Words)
            {
                // Arial : chiffres de 0,556 em ; 24 px à l'échelle 2 ≈ 8 unités de page par caractère.
                var size = 12.0 * scale;
                var ft = new FormattedText(word.Text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, size, Brushes.Black, 1.0);
                dc.DrawText(ft, new Point(word.X * scale, word.Y * scale));
            }
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ------------------------------------------------------------------ PDF minimal avec couche texte

    private static byte[] WritePdf(IReadOnlyList<StatementPage> pages)
    {
        var latin1 = Encoding.Latin1;
        var ms = new MemoryStream();
        var offsets = new List<long>();
        void Raw(string s) { var b = latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(int id, string body)
        {
            offsets.Add(ms.Position);
            Raw($"{id} 0 obj\n{body}\nendobj\n");
        }
        string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

        Raw("%PDF-1.4\n");
        var pageIds = pages.Select((_, i) => 4 + i * 2).ToList();
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, $"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {pages.Count} >>");
        Obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>");
        const double k = 0.5;      // unités de page → points
        const double font = 6.67;  // Courier : avance 0,6 em ≈ 4 pt = 8 unités × 0,5
        for (var i = 0; i < pages.Count; i++)
        {
            var sb = new StringBuilder("BT\n");
            sb.Append($"/F1 {font.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} Tf\n");
            foreach (var w in pages[i].Words)
            {
                var x = w.X * k;
                var y = 842 - (w.Y * k + 12);
                sb.Append(System.FormattableString.Invariant($"1 0 0 1 {x:0.##} {y:0.##} Tm ({Esc(w.Text)}) Tj\n"));
            }
            sb.Append("ET");
            var content = sb.ToString();
            var contentBytes = latin1.GetBytes(content);
            Obj(pageIds[i], $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {pageIds[i] + 1} 0 R >>");
            Obj(pageIds[i] + 1, $"<< /Length {contentBytes.Length} >>\nstream\n{content}\nendstream");
        }
        // les objets ont été écrits dans l'ordre 1,2,3,4,5,... : la table des références suit cet ordre
        var xref = ms.Position;
        var total = offsets.Count + 1;
        Raw($"xref\n0 {total}\n0000000000 65535 f \n");
        foreach (var o in offsets) Raw($"{o:0000000000} 00000 n \n");
        Raw($"trailer\n<< /Size {total} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
