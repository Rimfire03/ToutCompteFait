using System.Text;

namespace ToutCompteFait.Core.Export;

/// <summary>Générateur PDF minimal (texte et traits, polices standard Helvetica) : suffisant pour un livre-journal
/// imprimable et archivable, sans dépendance externe. PDF 1.4, encodage WinAnsi (accents français et « € » rendus).</summary>
internal sealed class SimplePdf
{
    public const double PageWidth = 842;    // A4 paysage, en points
    public const double PageHeight = 595;

    private readonly List<StringBuilder> _pages = new();
    private StringBuilder _page = new();
    private static readonly Encoding Win1252 = CreateEncoding();

    // Largeurs Helvetica (AFM, 1/1000 em) des caractères 32 à 126.
    private static readonly int[] Widths =
    {
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,            // 32-47
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,            // 48-63
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,           // 64-79
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,            // 80-95
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,            // 96-111
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584                  // 112-126
    };

    public SimplePdf() => NewPage();

    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    public int PageCount => _pages.Count + 1;

    public void NewPage()
    {
        if (_pages.Count > 0 || _page.Length > 0) _pages.Add(_page);
        _page = new StringBuilder();
    }

    /// <summary>Largeur d'un texte en points.</summary>
    public static double Width(string text, double size)
    {
        double w = 0;
        foreach (var c in text)
            w += c is >= (char)32 and <= (char)126 ? Widths[c - 32] : 556;
        return w * size / 1000.0;
    }

    /// <summary>Texte tronqué (avec « … ») pour tenir dans <paramref name="maxWidth"/>.</summary>
    public static string Fit(string text, double size, double maxWidth)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        if (Width(text, size) <= maxWidth) return text;
        while (text.Length > 1 && Width(text + "…", size) > maxWidth) text = text[..^1];
        return text + "…";
    }

    public void Text(double x, double y, string text, double size = 8, bool bold = false)
    {
        _page.Append("BT /").Append(bold ? "F2" : "F1").Append(' ').Append(Num(size)).Append(" Tf ")
            .Append(Num(x)).Append(' ').Append(Num(y)).Append(" Td (").Append(Escape(text)).Append(") Tj ET\n");
    }

    public void TextRight(double xRight, double y, string text, double size = 8, bool bold = false) =>
        Text(xRight - Width(text, size), y, text, size, bold);

    public void Line(double x1, double y1, double x2, double y2, double width = 0.5)
    {
        _page.Append(Num(width)).Append(" w ").Append(Num(x1)).Append(' ').Append(Num(y1)).Append(" m ")
            .Append(Num(x2)).Append(' ').Append(Num(y2)).Append(" l S\n");
    }

    private static string Num(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Chaîne littérale PDF : octets Windows-1252, parenthèses et barre oblique échappées.</summary>
    private static string Escape(string text)
    {
        var bytes = Win1252.GetBytes(text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '));
        var sb = new StringBuilder(bytes.Length + 8);
        foreach (var b in bytes)
        {
            var c = (char)b;
            if (c is '(' or ')' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Document complet. <paramref name="footer"/> : texte de pied de page (le numéro « Page i / n » est ajouté).</summary>
    public byte[] Save(string footer)
    {
        var pages = new List<StringBuilder>(_pages) { _page };
        var n = pages.Count;
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        sb.Append("%PDF-1.4\n%âãÏÓ\n");
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{5 + 2 * i} 0 R"));
        Obj($"<< /Type /Pages /Kids [{kids}] /Count {n} >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (var i = 0; i < n; i++)
        {
            Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(PageWidth)} {Num(PageHeight)}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {6 + 2 * i} 0 R >>");
            var content = new StringBuilder(pages[i].ToString());
            var pageText = $"{footer}  -  Page {i + 1} / {n}";
            content.Append("BT /F1 7 Tf 30 18 Td (").Append(Escape(pageText)).Append(") Tj ET\n");
            Obj($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        }

        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
