using System.Globalization;
using System.Text;

namespace ComptaAepby.Core.Import;

public sealed class ImportReport
{
    public List<string> Info { get; } = new();
    public List<string> Warnings { get; } = new();
    public int YearsImported { get; set; }
    public int EntriesImported { get; set; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var i in Info) sb.AppendLine(i);
        if (Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("À vérifier :");
            foreach (var w in Warnings) sb.AppendLine("  • " + w);
        }
        return sb.ToString();
    }
}

/// <summary>Importe les classeurs « livre » Excel de l'association (onglets Config, Livre, Report …) dans la base.
/// Principes : on reprend exactement ce qui est saisi (aucune correction silencieuse) ; tout ce qui est
/// incohérent est signalé dans le rapport et reste visible dans le logiciel (écritures non ventilées).</summary>
public static class ExcelImporter
{
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["janvier"] = 1, ["février"] = 2, ["fevrier"] = 2, ["mars"] = 3, ["avril"] = 4, ["mai"] = 5, ["juin"] = 6,
        ["juillet"] = 7, ["août"] = 8, ["aout"] = 8, ["septembre"] = 9, ["octobre"] = 10, ["novembre"] = 11, ["décembre"] = 12,
        ["decembre"] = 12
    };

    private sealed record Source(string Path, XlsxWorkbook Book, DateOnly Start, DateOnly ConfigEnd);

    public static ImportReport Import(Compta compta, IEnumerable<string> paths)
    {
        compta.Require(ComptaAepby.Core.Security.Permission.ManageDatabase);
        var report = new ImportReport();
        var sources = new List<Source>();
        foreach (var p in paths)
        {
            var wb = XlsxWorkbook.Load(p);
            var (start, end) = ReadPeriod(wb, p);
            sources.Add(new Source(p, wb, start, end));
        }
        sources = sources.OrderBy(s => s.Start).ToList();

        using var tx = compta.Transaction();
        for (var i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            // Continuité : un exercice se termine la veille du début du suivant (un classeur 2024-2025 peut contenir
            // des mouvements de juillet-août alors que sa config annonce juin).
            var end = i + 1 < sources.Count ? sources[i + 1].Start.AddDays(-1) : s.ConfigEnd;
            ImportYear(compta, s, end, report);
        }
        tx.Commit();
        return report;
    }

    private static (DateOnly Start, DateOnly End) ReadPeriod(XlsxWorkbook wb, string path)
    {
        var cfg = wb.Sheet("Config") ?? throw new ComptaException($"{System.IO.Path.GetFileName(path)} : onglet « Config » introuvable.");
        // B2 « de septembre », C2 2025 ; B3 « à aout », C3 2026.
        int Month(string text)
        {
            var t = text.Trim().ToLowerInvariant();
            foreach (var w in t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (Months.TryGetValue(w, out var m)) return m;
            throw new ComptaException($"{System.IO.Path.GetFileName(path)} : mois illisible dans l'onglet Config (« {text} »).");
        }
        var m1 = Month(cfg.Text(2, 2));
        var y1 = (int)(cfg.Number(2, 3) ?? throw new ComptaException("Année de début illisible dans Config."));
        var m2 = Month(cfg.Text(3, 2));
        var y2 = (int)(cfg.Number(3, 3) ?? throw new ComptaException("Année de fin illisible dans Config."));
        var start = new DateOnly(y1, m1, 1);
        var end = new DateOnly(y2, m2, DateTime.DaysInMonth(y2, m2));
        return (start, end);
    }

    private static string Norm(string s) =>
        s.Trim().ToLowerInvariant().TrimEnd('s');

    private static void ImportYear(Compta compta, Source src, DateOnly end, ImportReport report)
    {
        var wb = src.Book;
        var file = System.IO.Path.GetFileName(src.Path);
        var livre = wb.Sheet("Livre") ?? throw new ComptaException($"{file} : onglet « Livre » introuvable.");

        var year = compta.CreateYear(src.Start, end, cloneStructure: false);
        report.Info.Add($"Exercice {year.Label} ({year.Start:dd/MM/yyyy} → {year.End:dd/MM/yyyy}) importé depuis {file}.");
        if (end != src.ConfigEnd)
            report.Info.Add($"  Fin d'exercice ajustée au {end:dd/MM/yyyy} (veille du début de l'exercice suivant) ; le classeur annonçait le {src.ConfigEnd:dd/MM/yyyy}.");

        // Comptes
        var accounts = compta.Accounts(includeInactive: true);
        var bank = accounts.FirstOrDefault(a => a.Name.Equals("Banque", StringComparison.CurrentCultureIgnoreCase))
                   ?? compta.AddAccount("Banque", AccountKind.Bank);
        var livret = accounts.FirstOrDefault(a => a.Name.Equals("Livret A", StringComparison.CurrentCultureIgnoreCase))
                     ?? compta.AddAccount("Livret A", AccountKind.Savings);

        // Fin des données : première ligne dont une colonne de produits ou de charges contient une formule SUM(...).
        var firstCol = XlsxSheet.Col("K");
        var lastCol = XlsxSheet.Col("AO");
        var totalRow = livre.MaxRow + 1;
        for (var r = 3; r <= livre.MaxRow; r++)
        {
            var c = livre.Get(r, firstCol);
            if (c is { HasFormula: true } && c.Number is not null && IsTotalRow(livre, r)) { totalRow = r; break; }
        }

        // Catégories : une colonne = une catégorie, nommée par l'en-tête (ligne 2, valeur calculée).
        var catByCol = new Dictionary<int, Category>();
        var usedNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        for (var col = firstCol; col <= lastCol; col++)
        {
            var kind = col <= XlsxSheet.Col("Y") ? CategoryKind.Product : CategoryKind.Charge;
            var name = livre.Text(2, col);
            if (name == "0") name = string.Empty; // en-tête =Config!Bxx d'une case vide : Excel affiche 0
            var hasData = false;
            for (var r = 4; r < totalRow; r++)
                if (livre.Number(r, col) is { } n && n != 0) { hasData = true; break; }
            if (name.Length == 0)
            {
                if (!hasData) continue;
                name = $"Colonne {XlsxSheet.Letters(col)} (sans nom)";
                report.Warnings.Add($"{year.Label} : la colonne {XlsxSheet.Letters(col)} contient des montants mais n'a pas de nom ; catégorie « {name} » créée.");
            }
            var key = (kind == CategoryKind.Product ? "P:" : "C:") + name;
            if (!usedNames.Add(key))
            {
                var k = 2;
                while (!usedNames.Add(key + " " + k)) k++;
                name = $"{name} ({k})";
                report.Warnings.Add($"{year.Label} : deux colonnes portent le même nom, la seconde est renommée « {name} ».");
            }
            catByCol[col] = compta.AddCategory(year.Id, kind, name);
        }

        // Opérations : un produit et une charge de même nom (au pluriel près) sont liés automatiquement.
        var cats = catByCol.Values.ToList();
        foreach (var p in cats.Where(c => c.Kind == CategoryKind.Product))
        {
            var partners = cats.Where(c => c.Kind == CategoryKind.Charge && Norm(c.Name) == Norm(p.Name)).ToList();
            if (partners.Count == 0) continue;
            var op = compta.AddOperation(year.Id, p.Name);
            compta.SetCategoryOperation(p.Id, op.Id);
            foreach (var c in partners) compta.SetCategoryOperation(c.Id, op.Id);
        }

        // Solde d'ouverture (G3 banque, J3 livret) : sert au premier exercice, sinon contrôle du report.
        var openBank = Money.FromDouble(livre.Number(3, XlsxSheet.Col("G")) ?? 0);
        var openLivret = Money.FromDouble(livre.Number(3, XlsxSheet.Col("J")) ?? 0);
        if (compta.OpeningIsAutomatic(year.Id))
        {
            var cb = compta.Opening(year.Id, bank.Id);
            var cl = compta.Opening(year.Id, livret.Id);
            if (cb != openBank || cl != openLivret)
                report.Warnings.Add($"{year.Label} : soldes d'ouverture du classeur (banque {Money.Format(openBank)} €, livret {Money.Format(openLivret)} €) " +
                                    $"différents du report automatique (banque {Money.Format(cb)} €, livret {Money.Format(cl)} €).");
        }
        else
        {
            compta.SetOpening(year.Id, bank.Id, openBank);
            compta.SetOpening(year.Id, livret.Id, openLivret);
        }

        // Écritures
        var colE = XlsxSheet.Col("E"); var colF = XlsxSheet.Col("F");
        var colH = XlsxSheet.Col("H"); var colI = XlsxSheet.Col("I");
        DateOnly lastDate = src.Start;
        var imported = 0;
        for (var r = 4; r < totalRow; r++)
        {
            long E = Cents(livre, r, colE), F = Cents(livre, r, colF), H = Cents(livre, r, colH), I = Cents(livre, r, colI);
            var allocs = new List<Allocation>();
            foreach (var (col, cat) in catByCol)
            {
                var c = Cents(livre, r, col);
                if (c == 0) continue;
                allocs.Add(new Allocation(cat.Id, c));
                if (livre.Get(r, col) is { Number: null })
                    report.Warnings.Add($"{year.Label} ligne {r} : montant {Money.Format(c)} € saisi comme du texte dans « {cat.Name} » " +
                                        "(Excel l'ignorait dans ses totaux) ; repris comme un montant.");
            }
            var label = livre.Text(r, 3);
            var flows = new[] { E, F, H, I };
            if (flows.All(x => x == 0) && allocs.Count == 0)
            {
                if (label.Length > 0)
                    report.Warnings.Add($"{year.Label} ligne {r} « {label} » : ligne sans montant, ignorée.");
                continue;
            }

            var dateCell = livre.Get(r, 2);
            DateOnly date;
            if (dateCell?.AsDate() is { } d) { date = d; lastDate = d; }
            else
            {
                date = lastDate;
                report.Warnings.Add($"{year.Label} ligne {r} « {label} » : date manquante, date de la ligne précédente utilisée ({date:dd/MM/yyyy}).");
            }

            if (flows.All(x => x == 0))
            {
                // Aucune entrée ni sortie d'argent mais une ventilation (ex. refacturation interne : produit et charge
                // de même montant) : importée telle quelle comme opération sans mouvement d'argent.
                var amount = allocs.Where(a => cats.First(c => c.Id == a.CategoryId).Kind == CategoryKind.Product).Sum(a => Math.Abs(a.AmountCents));
                if (amount == 0) amount = allocs.Sum(a => Math.Abs(a.AmountCents));
                compta.SaveEntry(new Entry(0, year.Id, date, label.Length == 0 ? "(sans libellé)" : label, livre.Text(r, 4).Equals("ok", StringComparison.OrdinalIgnoreCase),
                    EntryKind.NoCash, null, Direction.In, null, amount, null, null), allocs, strict: false, importRef: $"{file}!ligne {r}");
                imported++;
                continue;
            }
            var pointed = livre.Text(r, 4).Equals("ok", StringComparison.OrdinalIgnoreCase);
            var refText = $"{file}!ligne {r}";
            if (label.Length == 0) label = "(sans libellé)";

            // Virement interne : sortie d'un compte = entrée de l'autre, même montant.
            var transfer = (F > 0 && H > 0 && F == H && E == 0 && I == 0 ? (bank.Id, livret.Id, F) : (0L, 0L, 0L));
            if (transfer.Item3 == 0 && E > 0 && I > 0 && E == I && F == 0 && H == 0) transfer = (livret.Id, bank.Id, E);

            if (transfer.Item3 > 0)
            {
                compta.SaveEntry(new Entry(0, year.Id, date, label, pointed, EntryKind.Transfer, transfer.Item1, Direction.Out,
                    transfer.Item2, transfer.Item3, null, null), allocs, strict: false, importRef: refText);
                imported++;
                continue;
            }

            // Mouvements ordinaires : une écriture par jambe (en général une seule). La ventilation est portée par la 1re.
            var legs = new List<(long Account, Direction Dir, long Cents)>();
            if (E > 0) legs.Add((bank.Id, Direction.In, E));
            if (F > 0) legs.Add((bank.Id, Direction.Out, F));
            if (H > 0) legs.Add((livret.Id, Direction.In, H));
            if (I > 0) legs.Add((livret.Id, Direction.Out, I));
            if (legs.Count > 1)
                report.Warnings.Add($"{year.Label} ligne {r} « {label} » : plusieurs mouvements sur la même ligne qui ne forment pas un virement simple ; " +
                                    $"importés en {legs.Count} écritures (ventilation sur la première).");
            for (var k = 0; k < legs.Count; k++)
            {
                var (acc, dir, cents) = legs[k];
                compta.SaveEntry(new Entry(0, year.Id, date, label, pointed, EntryKind.Normal, acc, dir, null, cents, null, null),
                    k == 0 ? allocs : Array.Empty<Allocation>(), strict: false, importRef: refText);
                imported++;
            }
        }
        report.EntriesImported += imported;
        report.YearsImported++;
        report.Info.Add($"  {imported} écriture(s) importée(s).");

        ImportAdjustments(compta, wb, year, report);
    }

    private static bool IsTotalRow(XlsxSheet livre, int row)
    {
        // Ligne de totaux : plusieurs colonnes de catégories contiennent des formules et la colonne A est vide.
        var formulas = 0;
        for (var c = XlsxSheet.Col("K"); c <= XlsxSheet.Col("AO"); c++)
            if (livre.Get(row, c) is { HasFormula: true }) formulas++;
        return formulas >= 5 && livre.Text(row, 3).Length == 0;
    }

    private static long Cents(XlsxSheet sheet, int row, int col, bool typedOnly = false)
    {
        var c = sheet.Get(row, col);
        if (c is null || (typedOnly && c.HasFormula)) return 0;
        if (c.Number is { } n) return Money.FromDouble(n);
        return c.Text is { } t && Money.TryParse(t, out var v) ? v : 0;
    }

    private static void ImportAdjustments(Compta compta, XlsxWorkbook wb, FiscalYear year, ImportReport report)
    {
        var cats = compta.Categories(year.Id);

        // (nom d'onglet, type, produits ?)
        var defs = new[]
        {
            ("Report Charge n-1", AdjustmentType.FromPrev, CategoryKind.Charge),
            ("Report Charge n+1", AdjustmentType.ToNext, CategoryKind.Charge),
            ("Report Produit n-1", AdjustmentType.FromPrev, CategoryKind.Product),
        };
        var prevYear = compta.PreviousYear(year);
        var prevCarries = prevYear is null
            ? new List<Adjustment>()
            : compta.Adjustments(prevYear.Id).Where(a => a.Type is AdjustmentType.ToNext or AdjustmentType.Due).ToList();

        foreach (var (sheetName, type, kind) in defs)
        {
            var sh = wb.Sheet(sheetName);
            if (sh is null) continue;

            // Une ligne : intitulé en A, montant « libre » en C, ou montants dans les colonnes de catégories D..S.
            for (var r = 4; r <= 19; r++)
            {
                var label = sh.Text(r, 1);
                var free = Cents(sh, r, 3, typedOnly: true);
                var perCat = new List<(string CatName, long Cents)>();
                for (var c = 4; c <= 26; c++)
                {
                    var v = Cents(sh, r, c, typedOnly: true);
                    if (v == 0) continue;
                    var name = sh.Text(3, c);
                    perCat.Add((name, v));
                }
                if (label.Length == 0 && free == 0 && perCat.Count == 0) continue;
                if (free == 0 && perCat.Count == 0) continue; // modèle vide : intitulé seul
                if (label.Length == 0) label = "(sans intitulé)";

                if (type == AdjustmentType.FromPrev && perCat.Count > 0 && prevCarries.Any(a => a.Kind == kind))
                {
                    report.Info.Add($"  {year.Label} : report « {label} » ({Money.Format(perCat.Sum(x => x.Cents))} €) non importé, " +
                                    "il est repris automatiquement de l'exercice précédent.");
                    continue;
                }

                // Format ancien (2024-2025) : montant seul, sans colonne de catégorie.
                // Ces montants étaient réglés pendant l'exercice mais concernaient l'exercice d'avant.
                if (perCat.Count == 0)
                {
                    var adjType = AdjustmentType.ForPast;
                    var guess = GuessCategory(cats, kind, label);
                    compta.AddAdjustment(year.Id, adjType, kind, guess?.Id, label, Math.Abs(free));
                    report.Warnings.Add($"{year.Label} : report « {label} » ({Money.Format(free)} €) " +
                        (guess is null ? "importé sans catégorie : à affecter dans « Reports »." : $"affecté à « {guess.Name} » (déduit du libellé), à confirmer."));
                    continue;
                }

                foreach (var (catName, cents) in perCat)
                {
                    var cat = cats.FirstOrDefault(c => c.Kind == kind && c.Name.Equals(catName, StringComparison.CurrentCultureIgnoreCase));
                    if (cents < 0)
                    {
                        report.Warnings.Add($"{year.Label} : report « {label} » négatif ignoré ({Money.Format(cents)} €).");
                        continue;
                    }
                    compta.AddAdjustment(year.Id, type, kind, cat?.Id, label, cents,
                        targetName: null);
                    if (cat is null)
                        report.Warnings.Add($"{year.Label} : report « {label} » : catégorie « {catName} » introuvable, à affecter.");
                    else if (type == AdjustmentType.ToNext)
                        report.Warnings.Add($"{year.Label} : report « {label} » ({Money.Format(cents)} €) vers l'exercice suivant : " +
                                            "catégorie de destination à choisir dans « Reports » (l'Excel ne la précisait pas).");
                }
            }
        }
    }

    private static Category? GuessCategory(IEnumerable<Category> cats, CategoryKind kind, string label)
    {
        var l = label.ToLowerInvariant();
        var hits = cats.Where(c => c.Kind == kind && l.Contains(Norm(c.Name))).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }
}




