using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ComptaAepby.Core.Import;

/// <summary>Cellule lue dans un classeur .xlsx : on ne garde que la valeur mise en cache par Excel
/// (le résultat des formules), pas les formules.</summary>
public sealed record XlsxCell(string? Text, double? Number, bool IsDate, bool HasFormula)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && Number is null;

    /// <summary>Date lue d'une cellule au format date (numéro de série Excel).</summary>
    public DateOnly? AsDate() =>
        Number is { } n && IsDate ? DateOnly.FromDateTime(new DateTime(1899, 12, 30).AddDays(Math.Floor(n))) : null;
}

public sealed class XlsxSheet
{
    private readonly Dictionary<(int Row, int Col), XlsxCell> _cells = new();
    public string Name { get; }
    public int MaxRow { get; private set; }

    public XlsxSheet(string name) => Name = name;

    internal void Set(int row, int col, XlsxCell cell)
    {
        _cells[(row, col)] = cell;
        if (row > MaxRow) MaxRow = row;
    }

    public XlsxCell? Get(int row, int col) => _cells.TryGetValue((row, col), out var c) ? c : null;

    /// <summary>Texte d'une cellule (nombre formaté sans décimales inutiles), « » si vide.</summary>
    public string Text(int row, int col)
    {
        var c = Get(row, col);
        if (c is null) return string.Empty;
        if (c.Text is not null) return c.Text.Trim();
        return c.Number is { } n ? n.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
    }

    public double? Number(int row, int col) => Get(row, col)?.Number;

    /// <summary>Numéro de colonne (A=1) depuis des lettres.</summary>
    public static int Col(string letters)
    {
        var n = 0;
        foreach (var ch in letters.ToUpperInvariant()) n = n * 26 + (ch - 'A' + 1);
        return n;
    }

    public static string Letters(int col)
    {
        var s = string.Empty;
        while (col > 0) { col--; s = (char)('A' + col % 26) + s; }
        return s;
    }
}

/// <summary>Lecteur minimal de .xlsx (zip de XML) : feuilles, textes partagés, dates. Pas de dépendance externe.</summary>
public sealed class XlsxWorkbook
{
    private readonly Dictionary<string, XlsxSheet> _sheets = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> SheetNames => _sheets.Keys;

    public XlsxSheet? Sheet(string name) => _sheets.GetValueOrDefault(name);

    public static XlsxWorkbook Load(string path)
    {
        // FileShare.ReadWrite : le fichier peut être ouvert dans Excel ou synchronisé par Google Drive.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var wb = new XlsxWorkbook();

        var shared = ReadSharedStrings(zip);
        var dateStyles = ReadDateStyles(zip);

        var rels = new Dictionary<string, string>();
        var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (relsEntry is not null)
        {
            using var rs = relsEntry.Open();
            var doc = new XmlDocument();
            doc.Load(rs);
            foreach (XmlElement r in doc.GetElementsByTagName("Relationship"))
                rels[r.GetAttribute("Id")] = r.GetAttribute("Target");
        }

        var wbEntry = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("Classeur Excel invalide (workbook.xml).");
        var sheets = new List<(string Name, string Target)>();
        using (var ws = wbEntry.Open())
        {
            var doc = new XmlDocument();
            doc.Load(ws);
            foreach (XmlElement s in doc.GetElementsByTagName("sheet"))
            {
                var rid = s.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                if (rels.TryGetValue(rid, out var target))
                    sheets.Add((s.GetAttribute("name"), target.TrimStart('/').StartsWith("xl/") ? target.TrimStart('/') : "xl/" + target.TrimStart('/')));
            }
        }

        foreach (var (name, target) in sheets)
        {
            var entry = zip.GetEntry(target);
            if (entry is null) continue;
            var sheet = new XlsxSheet(name);
            using var es = entry.Open();
            ReadSheet(es, sheet, shared, dateStyles);
            wb._sheets[name] = sheet;
        }
        return wb;
    }

    private static readonly System.Xml.Linq.XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var e = zip.GetEntry("xl/sharedStrings.xml");
        if (e is null) return list;
        using var s = e.Open();
        var doc = System.Xml.Linq.XDocument.Load(s);
        foreach (var si in doc.Root!.Elements(Ns + "si"))
        {
            var sb = new StringBuilder();
            // <t> direct, ou <r><t> (texte enrichi) ; on ignore les annotations phonétiques <rPh>.
            foreach (var t in si.Descendants(Ns + "t"))
                if (t.Parent is { } p && p.Name != Ns + "rPh") sb.Append(t.Value);
            list.Add(sb.ToString());
        }
        return list;
    }
    private static HashSet<int> ReadDateStyles(ZipArchive zip)
    {
        var result = new HashSet<int>();
        var e = zip.GetEntry("xl/styles.xml");
        if (e is null) return result;
        var doc = new XmlDocument();
        using (var s = e.Open()) doc.Load(s);

        var customDate = new HashSet<int>();
        foreach (XmlElement nf in doc.GetElementsByTagName("numFmt"))
            if (int.TryParse(nf.GetAttribute("numFmtId"), out var id) && LooksLikeDateFormat(nf.GetAttribute("formatCode")))
                customDate.Add(id);

        bool IsBuiltInDate(int id) => id is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58;

        var xfs = doc.GetElementsByTagName("cellXfs");
        if (xfs.Count == 0) return result;
        var i = 0;
        foreach (XmlElement xf in xfs[0]!.ChildNodes.OfType<XmlElement>().Where(x => x.LocalName == "xf"))
        {
            if (int.TryParse(xf.GetAttribute("numFmtId"), out var id) && (IsBuiltInDate(id) || customDate.Contains(id)))
                result.Add(i);
            i++;
        }
        return result;
    }

    private static bool LooksLikeDateFormat(string code)
    {
        // On retire les parties entre guillemets / crochets / échappées avant de chercher d, m, y, h, s.
        var sb = new StringBuilder();
        var inQuote = false; var inBracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (inQuote) continue;
            if (ch == '[') { inBracket = true; continue; }
            if (ch == ']') { inBracket = false; continue; }
            if (inBracket) continue;
            if (ch == '\\') { i++; continue; }
            sb.Append(char.ToLowerInvariant(ch));
        }
        var s = sb.ToString();
        return s.Contains('d') || s.Contains('y') || (s.Contains('m') && !s.Contains('0'));
    }

    private static void ReadSheet(Stream stream, XlsxSheet sheet, List<string> shared, HashSet<int> dateStyles)
    {
        var doc = System.Xml.Linq.XDocument.Load(stream);
        foreach (var c in doc.Descendants(Ns + "c"))
        {
            var refAttr = (string?)c.Attribute("r") ?? string.Empty;
            if (refAttr.Length == 0) continue;
            var type = (string?)c.Attribute("t");
            var style = int.TryParse((string?)c.Attribute("s"), out var s) ? s : 0;
            var (row, col) = ParseRef(refAttr);

            var hasFormula = c.Element(Ns + "f") is not null;
            var v = c.Element(Ns + "v")?.Value;
            var inline = c.Element(Ns + "is") is { } isEl
                ? string.Concat(isEl.Descendants(Ns + "t").Select(t => t.Value))
                : null;

            XlsxCell? cell = null;
            switch (type)
            {
                case "s":
                    if (int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count)
                        cell = new XlsxCell(shared[idx], null, false, hasFormula);
                    break;
                case "inlineStr":
                    cell = new XlsxCell(inline ?? v, null, false, hasFormula);
                    break;
                case "str":
                case "e":
                    cell = new XlsxCell(v, null, false, hasFormula);
                    break;
                case "b":
                    cell = new XlsxCell(null, v == "1" ? 1 : 0, false, hasFormula);
                    break;
                default:
                    if (v is not null && double.TryParse(v, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var n))
                        cell = new XlsxCell(null, n, dateStyles.Contains(style), hasFormula);
                    else if (hasFormula)
                        cell = new XlsxCell(null, null, false, true);
                    break;
            }
            if (cell is not null) sheet.Set(row, col, cell);
        }
    }
    private static (int Row, int Col) ParseRef(string a1)
    {
        var i = 0;
        var col = 0;
        while (i < a1.Length && char.IsLetter(a1[i])) { col = col * 26 + (char.ToUpperInvariant(a1[i]) - 'A' + 1); i++; }
        var row = int.Parse(a1.AsSpan(i));
        return (row, col);
    }
}

