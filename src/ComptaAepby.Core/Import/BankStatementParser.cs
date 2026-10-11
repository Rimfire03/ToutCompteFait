using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ComptaAepby.Core.Import;

/// <summary>Une ligne d'un relevé bancaire : montant signé (négatif = sortie d'argent).</summary>
public sealed record BankLine(DateOnly Date, string Label, long AmountCents, string? ExternalId = null);

/// <summary>Tableau lu dans un fichier CSV de relevé, avant interprétation des colonnes.</summary>
public sealed class BankCsvTable
{
    public required IReadOnlyList<string[]> Rows { get; init; }
    public char Delimiter { get; init; }
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Length);
}

/// <summary>Colonnes (indices à partir de 0) d'un relevé CSV. Soit une colonne de montant signé, soit deux colonnes
/// débit / crédit.</summary>
public sealed record BankCsvMapping(int DateColumn, int LabelColumn, int? AmountColumn, int? DebitColumn, int? CreditColumn,
    int FirstDataRow);

/// <summary>Lecture des relevés téléchargés depuis une banque : CSV (séparateur « ; », « , » ou tabulation, dates et
/// montants à la française ou internationale) et OFX. Aucune devinette silencieuse : les lignes illisibles sont
/// signalées.</summary>
public static class BankStatementParser
{
    // ------------------------------------------------------------------ fichier -> texte

    /// <summary>Lit le fichier en détectant l'encodage (UTF-8 avec ou sans BOM, sinon ISO-8859-1 / Windows-1252).</summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return DecodeBytes(bytes);
    }

    public static string DecodeBytes(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    public static bool LooksLikeOfx(string text) =>
        text.Contains("<OFX", StringComparison.OrdinalIgnoreCase) || text.Contains("<STMTTRN>", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ OFX

    public static List<BankLine> ParseOfx(string text, List<string>? warnings = null)
    {
        var lines = new List<BankLine>();
        var blocks = Regex.Split(text, "<STMTTRN>", RegexOptions.IgnoreCase).Skip(1);
        foreach (var raw in blocks)
        {
            var end = raw.IndexOf("</STMTTRN>", StringComparison.OrdinalIgnoreCase);
            var block = end >= 0 ? raw[..end] : raw;
            string? Tag(string name)
            {
                var m = Regex.Match(block, $@"<{name}>\s*([^<\r\n]*)", RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }
            var dt = Tag("DTPOSTED");
            var amt = Tag("TRNAMT");
            if (dt is null || amt is null || dt.Length < 8
                || !DateOnly.TryParseExact(dt[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || !decimal.TryParse(amt.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || decimal.Round(value, 2) != value)
            {
                warnings?.Add("Une opération OFX illisible a été ignorée.");
                continue;
            }
            var name = Tag("NAME");
            var memo = Tag("MEMO");
            var label = string.Join(" ", new[] { name, memo }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
            lines.Add(new BankLine(date, Clean(label.Length == 0 ? "(sans libellé)" : label), Money.FromDecimal(value), Tag("FITID")));
        }
        return lines;
    }

    // ------------------------------------------------------------------ CSV

    public static BankCsvTable ReadCsv(string text)
    {
        var sample = text.Split('\n').Take(30).ToList();
        char best = ';';
        var bestScore = -1;
        foreach (var d in new[] { ';', '\t', ',' })
        {
            // Un bon séparateur donne le même nombre de colonnes (> 1) sur la plupart des lignes.
            var counts = sample.Where(l => l.Trim().Length > 0).Select(l => Split(l, d).Length).ToList();
            if (counts.Count == 0) continue;
            var mode = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).First();
            var score = mode.Key > 1 ? mode.Count() * 10 + mode.Key : 0;
            if (score > bestScore) { bestScore = score; best = d; }
        }

        var rows = new List<string[]>();
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Trim().Length == 0) continue;
            rows.Add(Split(l, best).Select(c => c.Trim()).ToArray());
        }
        return new BankCsvTable { Rows = rows, Delimiter = best };
    }

    private static string[] Split(string line, char delimiter)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == delimiter) { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }

    private static readonly string[] DateKeys = { "date" };
    private static readonly string[] LabelKeys = { "libell", "intitul", "opération", "operation", "description", "label", "détail", "detail", "objet", "nature" };
    private static readonly string[] DebitKeys = { "débit", "debit", "sortie", "retrait" };
    private static readonly string[] CreditKeys = { "crédit", "credit", "entrée", "entree", "dépôt", "depot" };
    private static readonly string[] AmountKeys = { "montant", "amount", "somme", "valeur" };

    private static bool HasKey(string cell, string[] keys) =>
        keys.Any(k => cell.Contains(k, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Repère la ligne d'en-tête et les colonnes (par les titres, sinon par le contenu). Retourne null si
    /// rien d'exploitable n'est reconnu : l'utilisateur choisit alors les colonnes lui-même.</summary>
    public static BankCsvMapping? DetectMapping(BankCsvTable t)
    {
        var rows = t.Rows;
        for (var r = 0; r < Math.Min(rows.Count, 25); r++)
        {
            var cells = rows[r];
            var date = Array.FindIndex(cells, c => HasKey(c, DateKeys) && !c.Contains("valeur", StringComparison.CurrentCultureIgnoreCase));
            if (date < 0) date = Array.FindIndex(cells, c => HasKey(c, DateKeys));
            var label = Array.FindIndex(cells, c => HasKey(c, LabelKeys));
            var debit = Array.FindIndex(cells, c => HasKey(c, DebitKeys));
            var credit = Array.FindIndex(cells, c => HasKey(c, CreditKeys));
            var amount = Array.FindIndex(cells, c => HasKey(c, AmountKeys));
            if (date < 0 || label < 0) continue;
            if (debit >= 0 && credit >= 0) return new BankCsvMapping(date, label, null, debit, credit, r + 1);
            if (amount >= 0) return new BankCsvMapping(date, label, amount, null, null, r + 1);
        }

        // Sans en-tête exploitable : on cherche par le contenu à partir de la première ligne qui commence par une date.
        var first = rows.ToList().FindIndex(r => r.Any(c => TryParseDate(c, out _)));
        if (first < 0) return null;
        var sample = rows.Skip(first).Take(20).ToList();
        var width = sample.Max(r => r.Length);
        int? dateCol = null;
        for (var c = 0; c < width && dateCol is null; c++)
            if (sample.Count(r => c < r.Length && TryParseDate(r[c], out _)) >= Math.Max(1, sample.Count / 2)) dateCol = c;
        if (dateCol is null) return null;
        var numeric = new List<int>();
        int? labelCol = null;
        var bestLen = 0.0;
        for (var c = 0; c < width; c++)
        {
            if (c == dateCol) continue;
            var cells = sample.Where(r => c < r.Length).Select(r => r[c]).Where(s => s.Length > 0).ToList();
            if (cells.Count == 0) continue;
            if (cells.Count(s => Money.TryParse(s, out _)) >= cells.Count * 0.6) numeric.Add(c);
            else if (cells.Average(s => s.Length) > bestLen) { bestLen = cells.Average(s => s.Length); labelCol = c; }
        }
        if (labelCol is null || numeric.Count == 0) return null;
        return numeric.Count >= 2
            ? new BankCsvMapping(dateCol.Value, labelCol.Value, null, numeric[^2], numeric[^1], first)
            : new BankCsvMapping(dateCol.Value, labelCol.Value, numeric[0], null, null, first);
    }

    private static readonly string[] DateFormats =
    {
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yy", "d/M/yy", "ddMMyyyy", "yyyyMMdd"
    };

    public static bool TryParseDate(string? s, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        // « 2026-01-05T00:00:00 » ou « 05/01/2026 00:00 » : on garde la partie date.
        var space = t.IndexOfAny(new[] { ' ', 'T' });
        if (space > 5) t = t[..space];
        return DateOnly.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>Montant signé d'une cellule : « -12,50 », « 12,50- », « (12,50) », « 1 234,56 € ».</summary>
    public static bool TryParseAmount(string? s, out long cents)
    {
        cents = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')')) { negative = true; t = t[1..^1]; }
        if (t.EndsWith('-')) { negative = !negative; t = t[..^1]; }
        if (t.StartsWith('+')) t = t[1..];
        if (!Money.TryParse(t, out cents)) return false;
        if (negative) cents = -Math.Abs(cents);
        return true;
    }

    public static List<BankLine> ParseCsv(BankCsvTable t, BankCsvMapping m, List<string> warnings)
    {
        var lines = new List<BankLine>();
        for (var r = m.FirstDataRow; r < t.Rows.Count; r++)
        {
            var row = t.Rows[r];
            string Cell(int? c) => c is { } i && i >= 0 && i < row.Length ? row[i] : "";

            var dateText = Cell(m.DateColumn);
            if (!TryParseDate(dateText, out var date))
            {
                // Pied de fichier, solde, ligne vide : ignoré sans bruit si aucune cellule ne ressemble à un montant.
                if (row.Any(c => TryParseAmount(c, out _) && c.Length > 0) && dateText.Length > 0)
                    warnings.Add($"Ligne {r + 1} ignorée : date illisible (« {dateText} »).");
                continue;
            }

            long cents;
            if (m.AmountColumn is not null)
            {
                if (!TryParseAmount(Cell(m.AmountColumn), out cents) || cents == 0)
                {
                    if (Cell(m.AmountColumn).Length > 0) warnings.Add($"Ligne {r + 1} ignorée : montant illisible (« {Cell(m.AmountColumn)} »).");
                    continue;
                }
            }
            else
            {
                var hasDebit = TryParseAmount(Cell(m.DebitColumn), out var debit) && debit != 0;
                var hasCredit = TryParseAmount(Cell(m.CreditColumn), out var credit) && credit != 0;
                if (hasDebit == hasCredit)
                {
                    if (hasDebit) warnings.Add($"Ligne {r + 1} ignorée : débit et crédit renseignés ensemble.");
                    continue;
                }
                cents = hasDebit ? -Math.Abs(debit) : Math.Abs(credit);
            }

            var label = Clean(Cell(m.LabelColumn));
            lines.Add(new BankLine(date, label.Length == 0 ? "(sans libellé)" : label, cents));
        }
        return lines;
    }

    /// <summary>Espaces multiples et caractères de contrôle supprimés (les relevés alignent souvent en colonnes).</summary>
    public static string Clean(string s)
    {
        var t = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return t.Length > 500 ? t[..500] : t;
    }
}

