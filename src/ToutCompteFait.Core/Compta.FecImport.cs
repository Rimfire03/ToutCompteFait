using System.Globalization;
using System.Text;
using ToutCompteFait.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

/// <summary>Part de ventilation d'une écriture lue dans un FEC (produit ou charge), montant déjà rapproché du net encaissé.</summary>
public sealed record FecPart(CategoryKind Kind, string Number, string Name, long Cents);

/// <summary>Écriture de trésorerie à créer depuis un FEC. <see cref="NetCents"/> : mouvement du compte (positif = entrée).
/// Un virement entre deux comptes porte <see cref="ToBankNumber"/> (compte qui reçoit).</summary>
public sealed record FecPlanEntry(string Ref, string Journal, string Num, DateOnly Date, string Label,
    string BankNumber, string BankName, string? ToBankNumber, string? ToBankName, long NetCents, IReadOnlyList<FecPart> Parts);

public sealed record FecImportPlan(long YearId, IReadOnlyList<FecPlanEntry> Entries, IReadOnlyDictionary<string, long> Openings,
    IReadOnlyDictionary<string, string> BankNames, int Lines, int Groups, int NoCash, int OutOfYear, int Already, int Unsupported,
    IReadOnlyList<string> Warnings);

public sealed record FecImportResult(int Created, int AccountsCreated, int CategoriesCreated, int OpeningsSet, IReadOnlyList<string> Failed);

public sealed partial class Compta
{
    // ------------------------------------------------------------------ import d'un FEC

    /// <summary>Lit un fichier FEC : UTF-8 s'il est valide, sinon Windows-1252 (cas courant des logiciels comptables).</summary>
    public static string ReadFecFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static long FecCents(string s)
    {
        s = new string(s.Where(c => !char.IsWhiteSpace(c) && c != ' ').ToArray()).Replace(',', '.');
        if (s.Length == 0) return 0;
        var d = decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        return (long)Math.Round(d * 100m, MidpointRounding.AwayFromZero);
    }

    private static bool FecDate(string s, out DateOnly d)
    {
        s = s.Trim();
        return DateOnly.TryParseExact(s, new[] { "yyyyMMdd", "dd/MM/yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
    }

    private static bool IsTreasury(string number) => number.StartsWith("51", StringComparison.Ordinal) || number.StartsWith("53", StringComparison.Ordinal);

    /// <summary>Analyse un FEC sans rien modifier. Le logiciel tient une comptabilité de trésorerie : seules les écritures
    /// qui touchent un compte de classe 51 (banque) ou 53 (caisse) sont reprises ; les écritures d'achats, de ventes ou
    /// d'opérations diverses (sans mouvement d'argent) sont comptées et ignorées. Les à-nouveaux (journal AN) donnent
    /// les soldes d'ouverture. Les écritures déjà importées (même journal, numéro et date) sont ignorées.</summary>
    public FecImportPlan PlanFecImport(long yearId, string text)
    {
        Require(Permission.WriteLedger);
        var year = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        if (year.Validated) throw new ComptaException("L'exercice est validé : aucune écriture ne peut y être importée.");

        var rawLines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (rawLines.Count < 2) throw new ComptaException("Le fichier est vide ou ne contient pas d'écritures.");
        var header = rawLines[0].TrimStart('﻿');
        var delimiter = new[] { '\t', ';', '|' }.OrderByDescending(c => header.Count(h => h == c)).First();
        var names = header.Split(delimiter).Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();
        int Col(string n) => names.IndexOf(n);
        int iJournal = Col("journalcode"), iNum = Col("ecriturenum"), iDate = Col("ecrituredate"), iAcc = Col("comptenum"),
            iAccLib = Col("comptelib"), iLib = Col("ecriturelib"), iDeb = Col("debit"), iCre = Col("credit"), iJLib = Col("journallib");
        if (iJournal < 0 || iNum < 0 || iDate < 0 || iAcc < 0 || iDeb < 0 || iCre < 0)
            throw new ComptaException("Ce fichier n'est pas un FEC : colonnes JournalCode, EcritureNum, EcritureDate, CompteNum, Debit et Credit introuvables.");

        string Cell(string[] f, int i) => i >= 0 && i < f.Length ? f[i].Trim().Trim('"') : "";

        var warnings = new List<string>();
        var groups = new Dictionary<(string Journal, string Num), List<(DateOnly Date, string Acc, string AccLib, string Lib, long Net)>>();
        var journalLabels = new Dictionary<string, string>();
        var order = new List<(string, string)>();
        var parsed = 0;
        for (var i = 1; i < rawLines.Count; i++)
        {
            var f = rawLines[i].Split(delimiter);
            if (f.Length < Math.Max(iDeb, iCre) + 1) { if (warnings.Count < 5) warnings.Add($"Ligne {i + 1} ignorée : colonnes manquantes."); continue; }
            if (!FecDate(Cell(f, iDate), out var date)) { if (warnings.Count < 5) warnings.Add($"Ligne {i + 1} ignorée : date illisible « {Cell(f, iDate)} »."); continue; }
            long net;
            try { net = FecCents(Cell(f, iDeb)) - FecCents(Cell(f, iCre)); }
            catch (FormatException) { if (warnings.Count < 5) warnings.Add($"Ligne {i + 1} ignorée : montant illisible."); continue; }
            var key = (Cell(f, iJournal), Cell(f, iNum));
            if (!groups.TryGetValue(key, out var list)) { groups[key] = list = new(); order.Add(key); }
            list.Add((date, Cell(f, iAcc), Cell(f, iAccLib), Cell(f, iLib), net));
            if (iJLib >= 0 && !journalLabels.ContainsKey(key.Item1)) journalLabels[key.Item1] = Cell(f, iJLib);
            parsed++;
        }

        var existing = new HashSet<string>();
        using (var l = Open())
            foreach (var r in l.Ctx.Entries.AsNoTracking().Where(e => e.ImportRef != null && e.ImportRef.StartsWith("fec:")).Select(e => e.ImportRef!))
                existing.Add(r);

        var entries = new List<FecPlanEntry>();
        var openings = new Dictionary<string, long>();
        var bankNames = new Dictionary<string, string>();
        int noCash = 0, outOfYear = 0, already = 0, unsupported = 0;

        foreach (var key in order)
        {
            var ls = groups[key];
            var isOpening = key.Item1.Equals("AN", StringComparison.OrdinalIgnoreCase)
                            || (journalLabels.TryGetValue(key.Item1, out var jl) && jl.Contains("nouveau", StringComparison.OrdinalIgnoreCase));
            foreach (var x in ls.Where(x => IsTreasury(x.Acc)))
                if (!bankNames.ContainsKey(x.Acc) || bankNames[x.Acc].Length == 0) bankNames[x.Acc] = x.AccLib;

            if (isOpening)
            {
                foreach (var g in ls.Where(x => IsTreasury(x.Acc)).GroupBy(x => x.Acc))
                    openings[g.Key] = openings.GetValueOrDefault(g.Key) + g.Sum(x => x.Net);
                continue;
            }

            var bank = ls.Where(x => IsTreasury(x.Acc)).ToList();
            if (bank.Count == 0) { noCash++; continue; }
            var date = ls.Min(x => x.Date);
            if (!year.Contains(date)) { outOfYear++; continue; }
            var reference = $"fec:{key.Item1}:{key.Item2}:{date:yyyyMMdd}";
            if (existing.Contains(reference)) { already++; continue; }

            var label = ls.Select(x => x.Lib).FirstOrDefault(s => s.Length > 0) ?? $"Écriture {key.Item1} {key.Item2}";
            var perBank = bank.GroupBy(x => x.Acc).Select(g => (Acc: g.Key, Net: g.Sum(x => x.Net))).Where(x => x.Net != 0).ToList();
            var others = ls.Where(x => !IsTreasury(x.Acc)).ToList();

            if (perBank.Count == 0) { noCash++; continue; }
            if (perBank.Count == 2 && perBank.Sum(x => x.Net) == 0 && !others.Any(x => x.Acc.StartsWith('6') || x.Acc.StartsWith('7')))
            {
                var from = perBank.First(x => x.Net < 0);
                var to = perBank.First(x => x.Net > 0);
                entries.Add(new FecPlanEntry(reference, key.Item1, key.Item2, date, label, from.Acc, bankNames.GetValueOrDefault(from.Acc, ""),
                    to.Acc, bankNames.GetValueOrDefault(to.Acc, ""), -from.Net, Array.Empty<FecPart>()));
                continue;
            }
            if (perBank.Count != 1) { unsupported++; if (warnings.Count < 5) warnings.Add($"Écriture {key.Item1} n° {key.Item2} ignorée : plusieurs comptes de trésorerie."); continue; }

            var net = perBank[0].Net;
            // Produits (classe 7) : crédit − débit ; charges (classe 6) : débit − crédit, par compte.
            var parts = new List<FecPart>();
            foreach (var g in others.Where(x => x.Acc.StartsWith('7') || x.Acc.StartsWith('6')).GroupBy(x => x.Acc))
            {
                var product = g.Key.StartsWith('7');
                var cents = product ? -g.Sum(x => x.Net) : g.Sum(x => x.Net);
                if (cents == 0) continue;
                var name = g.Select(x => x.AccLib).FirstOrDefault(s => s.Length > 0) ?? "Compte " + g.Key;
                parts.Add(new FecPart(product ? CategoryKind.Product : CategoryKind.Charge, g.Key, name, cents));
            }
            // TVA, tiers : reste porté par la plus grosse part pour que la ventilation égale le montant encaissé / décaissé.
            var gap = net - parts.Sum(p => p.Kind == CategoryKind.Product ? p.Cents : -p.Cents);
            if (parts.Count > 0 && gap != 0)
            {
                var big = parts.IndexOf(parts.OrderByDescending(p => Math.Abs(p.Cents)).First());
                var p = parts[big];
                parts[big] = p with { Cents = p.Cents + (p.Kind == CategoryKind.Product ? gap : -gap) };
                if (parts[big].Cents == 0) parts.RemoveAt(big);
            }
            entries.Add(new FecPlanEntry(reference, key.Item1, key.Item2, date, label, perBank[0].Acc,
                bankNames.GetValueOrDefault(perBank[0].Acc, ""), null, null, net, parts));
        }
        return new FecImportPlan(yearId, entries, openings, bankNames, parsed, order.Count, noCash, outOfYear, already, unsupported, warnings);
    }

    /// <summary>Crée les comptes et catégories manquants, les soldes d'ouverture (premier exercice, solde encore nul) et les
    /// écritures du plan. Une écriture refusée est signalée sans arrêter les autres.</summary>
    public FecImportResult ApplyFecImport(FecImportPlan plan)
    {
        Require(Permission.WriteLedger);
        RequireWritable();
        var failed = new List<string>();
        int accountsCreated = 0, catsCreated = 0, openingsSet = 0, created = 0;

        // Comptes de trésorerie : par numéro comptable, puis par nom, sinon créés.
        var accounts = Accounts(true);
        var accountIds = new Dictionary<string, long>();
        var needed = plan.Entries.SelectMany(e => new[] { e.BankNumber, e.ToBankNumber }).Where(n => n is not null).Select(n => n!)
            .Concat(plan.Openings.Keys).Distinct().ToList();
        foreach (var number in needed)
        {
            var name = plan.BankNames.GetValueOrDefault(number, "");
            var found = accounts.FirstOrDefault(a => a.AccountNumber == number)
                        ?? (name.Length > 0 ? accounts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.CurrentCultureIgnoreCase)) : null);
            if (found is null)
            {
                var kind = number.StartsWith("53", StringComparison.Ordinal) ? AccountKind.Cash : AccountKind.Bank;
                found = AddAccount(name.Length > 0 ? name : (kind == AccountKind.Cash ? "Caisse " : "Banque ") + number, kind);
                accountsCreated++;
                accounts.Add(found);
            }
            if (found.AccountNumber != number && string.IsNullOrEmpty(found.AccountNumber)) SetAccountNumber(found.Id, number);
            accountIds[number] = found.Id;
        }

        // Soldes d'ouverture : seulement au premier exercice et si le compte est encore à zéro (jamais d'écrasement).
        if (!OpeningIsAutomatic(plan.YearId))
            foreach (var (number, net) in plan.Openings)
            {
                if (net == 0 || !accountIds.TryGetValue(number, out var id) || Opening(plan.YearId, id) != 0) continue;
                SetOpening(plan.YearId, id, net);
                openingsSet++;
            }

        // Catégories : par nom et nature dans l'exercice, sinon créées (avec leur numéro de compte pour le FEC).
        var cats = Categories(plan.YearId);
        var catIds = new Dictionary<(CategoryKind, string), long>();
        foreach (var part in plan.Entries.SelectMany(e => e.Parts).DistinctBy(p => (p.Kind, p.Number)))
        {
            var found = cats.FirstOrDefault(c => c.Kind == part.Kind && string.Equals(c.Name, part.Name, StringComparison.CurrentCultureIgnoreCase));
            if (found is null)
            {
                found = AddCategory(plan.YearId, part.Kind, part.Name);
                catsCreated++;
                cats.Add(found);
                try { SetCategoryAccountNumber(part.Kind, part.Name, part.Number); } catch (ComptaException) { /* numéro facultatif */ }
            }
            catIds[(part.Kind, part.Number)] = found.Id;
        }

        foreach (var e in plan.Entries)
        {
            try
            {
                var note = $"FEC {e.Journal} n° {e.Num}";
                if (e.ToBankNumber is not null)
                    SaveEntry(new Entry(0, plan.YearId, e.Date, e.Label, false, EntryKind.Transfer, accountIds[e.BankNumber], Direction.Out,
                        accountIds[e.ToBankNumber], Math.Abs(e.NetCents), null, note), Array.Empty<Allocation>(), strict: true, importRef: e.Ref);
                else
                    SaveEntry(new Entry(0, plan.YearId, e.Date, e.Label, false, EntryKind.Normal, accountIds[e.BankNumber],
                        e.NetCents >= 0 ? Direction.In : Direction.Out, null, Math.Abs(e.NetCents), null, note),
                        e.Parts.Select(p => new Allocation(catIds[(p.Kind, p.Number)], p.Cents)).ToList(),
                        strict: e.Parts.Count > 0,   // sans produit ni charge : « à ventiler », comme un import bancaire
                        importRef: e.Ref);
                created++;
            }
            catch (ComptaException ex)
            {
                failed.Add($"{e.Journal} n° {e.Num} ({e.Date:dd/MM/yyyy}, {e.Label}) : {ex.Message}");
            }
        }
        return new FecImportResult(created, accountsCreated, catsCreated, openingsSet, failed);
    }
}
