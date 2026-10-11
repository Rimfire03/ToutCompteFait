using System.Globalization;
using System.Text;
using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Export;
using ToutCompteFait.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

/// <summary>Une ligne du livre-journal exporté.</summary>
public sealed record LedgerLine(long Seq, DateOnly Date, string Label, string Type, string Account, string ToAccount,
    long CashIn, long CashOut, long Transfer, string Allocation, bool Pointed, string Status, long? ReversalOfSeq,
    string? Member, string? Note, int Attachments, string CreatedAt);

/// <summary>Livre-journal d'un exercice : toutes les écritures dans l'ordre de leur numéro (annulations comprises).</summary>
public sealed record LedgerData(FiscalYear Year, IReadOnlyList<LedgerLine> Lines)
{
    /// <summary>Totaux hors écritures annulées et contre-passations (effet net).</summary>
    public long TotalIn => Lines.Where(l => l.Status == "").Sum(l => l.CashIn);
    public long TotalOut => Lines.Where(l => l.Status == "").Sum(l => l.CashOut);
    public int CancelledCount => Lines.Count(l => l.Status == "Annulée");
}

public sealed partial class Compta
{
    // ------------------------------------------------------------------ livre-journal (CSV / PDF)

    /// <summary>Livre-journal d'un exercice, écritures annulées et contre-passations comprises, par numéro d'ordre.</summary>
    public LedgerData BuildLedger(long yearId)
    {
        var year = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        var accounts = Accounts(true).ToDictionary(a => a.Id, a => a.Name);
        string Acc(long? id) => id is { } v && accounts.TryGetValue(v, out var n) ? n : "";
        var cats = Categories(yearId).ToDictionary(c => c.Id, c => c.Name);
        var counts = AttachmentCounts(yearId);
        var rows = Entries(yearId, includeCancelled: true);
        var seqOf = rows.ToDictionary(r => r.Entry.Id, r => r.Entry.Seq);
        var created = new Dictionary<long, string>();
        using (var l = Open())
            foreach (var e in l.Ctx.Entries.AsNoTracking().Where(e => e.YearId == yearId).Select(e => new { e.Id, e.CreatedAt }).ToList())
                created[e.Id] = e.CreatedAt;

        var lines = new List<LedgerLine>();
        foreach (var r in rows.OrderBy(r => r.Entry.Seq))
        {
            var e = r.Entry;
            var alloc = string.Join(" ; ", r.Allocations.Select(a =>
                $"{(cats.TryGetValue(a.CategoryId, out var n) ? n : "?")} {Plain(a.AmountCents)}"));
            var inAmount = e.Kind is EntryKind.Normal or EntryKind.MemberRefund && e.Direction == Direction.In ? e.AmountCents : 0;
            var outAmount = e.Kind is EntryKind.Normal or EntryKind.MemberRefund && e.Direction == Direction.Out ? e.AmountCents : 0;
            var transfer = e.Kind == EntryKind.Transfer ? e.AmountCents : 0;
            var status = e.IsReversal ? "Annulation" : e.Cancelled ? "Annulée" : "";
            lines.Add(new LedgerLine(e.Seq, e.Date, e.Label, Labels.Of(e.Kind), Acc(e.AccountId), Acc(e.ToAccountId),
                inAmount, outAmount, transfer, alloc, e.Pointed, status,
                e.ReversalOfId is { } rid && seqOf.TryGetValue(rid, out var s) ? s : null,
                e.MemberName, e.Note, counts.GetValueOrDefault(e.Id), created.GetValueOrDefault(e.Id, "")));
        }
        return new LedgerData(year, lines);
    }

    private static string Plain(long cents) =>
        (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

    private static string CsvField(string? s)
    {
        s ??= "";
        // Neutralise l'interprétation d'une formule par un tableur.
        if (s.Length > 0 && s[0] is '=' or '+' or '@' || (s.Length > 0 && s[0] == '-' && s.Length > 1 && !char.IsDigit(s[1])))
            s = "'" + s;
        return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>Livre-journal en CSV (séparateur « ; », décimale « , »). À enregistrer en UTF-8 avec BOM pour Excel.</summary>
    public string ExportLedgerCsv(long yearId)
    {
        var data = BuildLedger(yearId);
        var sb = new StringBuilder();
        sb.Append("N°;Date;Libellé;Type;Compte;Compte destination;Entrée;Sortie;Virement;Ventilation;Pointée;Statut;")
          .Append("Annule l'écriture n°;Membre;Note;Justificatifs;Saisie le\r\n");
        foreach (var l in data.Lines)
        {
            sb.Append(l.Seq).Append(';').Append(l.Date.ToString("dd/MM/yyyy")).Append(';').Append(CsvField(l.Label)).Append(';')
              .Append(CsvField(l.Type)).Append(';').Append(CsvField(l.Account)).Append(';').Append(CsvField(l.ToAccount)).Append(';')
              .Append(l.CashIn == 0 ? "" : Plain(l.CashIn)).Append(';').Append(l.CashOut == 0 ? "" : Plain(l.CashOut)).Append(';')
              .Append(l.Transfer == 0 ? "" : Plain(l.Transfer)).Append(';').Append(CsvField(l.Allocation)).Append(';')
              .Append(l.Pointed ? "oui" : "non").Append(';').Append(l.Status).Append(';')
              .Append(l.ReversalOfSeq?.ToString() ?? "").Append(';').Append(CsvField(l.Member)).Append(';')
              .Append(CsvField(l.Note)).Append(';').Append(l.Attachments == 0 ? "" : l.Attachments.ToString()).Append(';')
              .Append(l.CreatedAt).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Livre-journal imprimable (PDF, A4 paysage), avec numéro d'ordre, annulations et totaux.</summary>
    public byte[] ExportLedgerPdf(long yearId, string? organization = null)
    {
        var data = BuildLedger(yearId);
        var y = data.Year;
        var pdf = new SimplePdf();
        const double left = 30, right = SimplePdf.PageWidth - 30, top = SimplePdf.PageHeight - 40, rowH = 11.5, size = 7.5;
        // Colonnes : N°, Date, Libellé, Compte, Entrée, Sortie, Ventilation / statut.
        const double cSeq = left, cDate = left + 26, cLabel = left + 74, cAcc = left + 292, cIn = left + 440, cOut = left + 500, cAlloc = left + 510;

        double yPos = 0;
        void Header()
        {
            pdf.Text(left, top, "Livre-journal" + (string.IsNullOrWhiteSpace(organization) ? "" : " - " + organization.Trim()), 13, true);
            pdf.Text(left, top - 15, $"Exercice {y.Label} du {y.Start:dd/MM/yyyy} au {y.End:dd/MM/yyyy}" +
                (y.Validated ? $" - validé le {y.ValidatedAt:dd/MM/yyyy} par {y.ValidatedBy}" : " - exercice en cours (non validé)"), 9);
            pdf.Text(left, top - 27, "Écritures dans l'ordre de leur numéro ; les écritures annulées et leurs annulations restent visibles.", 7);
            yPos = top - 46;
            pdf.Text(cSeq, yPos, "N°", size, true);
            pdf.Text(cDate, yPos, "Date", size, true);
            pdf.Text(cLabel, yPos, "Libellé", size, true);
            pdf.Text(cAcc, yPos, "Compte", size, true);
            pdf.TextRight(cIn + 52, yPos, "Entrée", size, true);
            pdf.TextRight(cOut + 52 - 2, yPos, "Sortie", size, true);
            pdf.Text(cAlloc + 60, yPos, "Ventilation / statut", size, true);
            pdf.Line(left, yPos - 3, right, yPos - 3);
            yPos -= rowH + 2;
        }
        Header();
        foreach (var l in data.Lines)
        {
            if (yPos < 40) { pdf.NewPage(); Header(); }
            pdf.TextRight(cSeq + 20, yPos, l.Seq.ToString(), size);
            pdf.Text(cDate, yPos, l.Date.ToString("dd/MM/yyyy"), size);
            pdf.Text(cLabel, yPos, SimplePdf.Fit(l.Label, size, cAcc - cLabel - 6), size);
            var account = l.Transfer > 0 ? $"{l.Account} > {l.ToAccount}" : l.Account;
            pdf.Text(cAcc, yPos, SimplePdf.Fit(account, size, cIn - cAcc - 4), size);
            if (l.CashIn > 0) pdf.TextRight(cIn + 52, yPos, Money.Format(l.CashIn), size);
            if (l.CashOut > 0) pdf.TextRight(cOut + 52 - 2, yPos, Money.Format(l.CashOut), size);
            var tail = l.Status switch
            {
                "Annulée" => "ANNULÉE",
                "Annulation" => $"Annulation de n° {l.ReversalOfSeq}",
                _ => l.Transfer > 0 ? $"Virement {Money.Format(l.Transfer)}" : l.Allocation
            };
            pdf.Text(cAlloc + 60, yPos, SimplePdf.Fit(tail, size, right - cAlloc - 60), size);
            yPos -= rowH;
        }
        if (yPos < 60) { pdf.NewPage(); Header(); }
        pdf.Line(left, yPos + 7, right, yPos + 7);
        yPos -= 4;
        pdf.Text(cLabel, yPos, $"Totaux (hors annulations) - {data.Lines.Count} écriture(s), dont {data.CancelledCount} annulée(s)", size, true);
        pdf.TextRight(cIn + 52, yPos, Money.Format(data.TotalIn), size, true);
        pdf.TextRight(cOut + 52 - 2, yPos, Money.Format(data.TotalOut), size, true);
        return pdf.Save($"Édité le {DateTime.Now:dd/MM/yyyy HH:mm} - Tout Compte Fait");
    }

    // ------------------------------------------------------------------ numéros de compte (FEC)

    /// <summary>Catégorie (genre + nom, tous exercices) avec son numéro de compte FEC saisi, et le numéro automatique
    /// utilisé à défaut.</summary>
    public sealed record CategoryAccountRow(CategoryKind Kind, string Name, string? Number, string AutoNumber)
    {
        public string Effective => Number ?? AutoNumber;
    }

    /// <summary>Numéros de compte des catégories connues (toutes années), du plus récent exercice au plus ancien pour
    /// l'ordre automatique.</summary>
    public List<CategoryAccountRow> CategoryAccountNumbers()
    {
        using var l = Open();
        var ctx = l.Ctx;
        var saved = ctx.CategoryAccounts.AsNoTracking().ToList().ToDictionary(c => (c.Kind, c.Name.ToLowerInvariant()), c => c.AccountNumber);
        var years = ctx.Years.AsNoTracking().OrderByDescending(y => y.StartDate).Select(y => y.Id).ToList();
        var cats = ctx.Categories.AsNoTracking().ToList();
        var result = new List<CategoryAccountRow>();
        var seen = new HashSet<(string, string)>();
        foreach (var kind in new[] { "P", "C" })
        {
            var ordered = years.SelectMany(yid => cats.Where(c => c.YearId == yid && c.Kind == kind).OrderBy(c => c.Position).ThenBy(c => c.Id)).ToList();
            var rank = 0;
            foreach (var c in ordered)
            {
                if (!seen.Add((kind, c.Name.ToLowerInvariant()))) continue;
                rank++;
                result.Add(new CategoryAccountRow(CatKindFromDb(kind), c.Name,
                    saved.GetValueOrDefault((kind, c.Name.ToLowerInvariant())),
                    (kind == "P" ? "7" : "6") + rank.ToString("D5", CultureInfo.InvariantCulture)));
            }
        }
        return result;
    }

    private static string? CleanAccountNumber(string? number)
    {
        number = number?.Trim();
        if (string.IsNullOrEmpty(number)) return null;
        if (number.Length is < 3 or > 20 || !number.All(char.IsDigit))
            throw new ComptaException("Un numéro de compte comptable est composé de 3 à 20 chiffres (ex. 706000).");
        return number;
    }

    /// <summary>Enregistre (ou efface, si vide) le numéro de compte d'une catégorie. Paramétrage : autorisé même sur les
    /// exercices validés (table à part, hors de l'empreinte).</summary>
    public void SetCategoryAccountNumber(CategoryKind kind, string name, string? number)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        var clean = CleanAccountNumber(number);
        var k = CatKindToDb(kind);
        using var tx = Begin();
        var ctx = Open().Ctx;
        var existing = ctx.CategoryAccounts.AsEnumerable().FirstOrDefault(c => c.Kind == k && string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (clean is null)
        {
            if (existing is not null) ctx.CategoryAccounts.Remove(existing);
        }
        else if (existing is null)
            ctx.CategoryAccounts.Add(new CategoryAccountEntity { Kind = k, Name = name, AccountNumber = clean });
        else
            existing.AccountNumber = clean;
        Save(ctx);
        Log(ctx, "Numéro de compte de catégorie", $"{(kind == CategoryKind.Product ? "Produit" : "Charge")} « {name} » : {clean ?? "(automatique)"}");
        tx.Commit();
    }

    /// <summary>Enregistre (ou efface) le numéro de compte comptable d'un compte bancaire / caisse.</summary>
    public void SetAccountNumber(long accountId, string? number)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        var clean = CleanAccountNumber(number);
        using var tx = Begin();
        var ctx = Open().Ctx;
        var a = ctx.Accounts.FirstOrDefault(x => x.Id == accountId) ?? throw new ComptaException("Compte introuvable.");
        a.AccountNumber = clean;
        Save(ctx);
        Log(ctx, "Numéro de compte comptable", $"{a.Name} : {clean ?? "(automatique)"}");
        tx.Commit();
    }

    // ------------------------------------------------------------------ FEC

    private sealed record FecAccount(string Number, string Label, string Journal, string JournalLabel);

    private sealed record FecLine(string Journal, string JournalLabel, long Num, DateOnly Date, string Account,
        string AccountLabel, string PieceRef, string Label, long Debit, long Credit);

    private static string FecText(string? s) =>
        (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>Nom de fichier réglementaire : SIREN + « FEC » + date de clôture (AAAAMMJJ).</summary>
    public static string FecFileName(string? siren, DateOnly closing)
    {
        var digits = new string((siren ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length != 9) digits = "000000000";
        return $"{digits}FEC{closing:yyyyMMdd}.txt";
    }

    /// <summary>Fichier des Écritures Comptables (art. L47 A-I du LPF, format BOI-CF-IOR-60-40-20) d'un exercice : une
    /// écriture par ligne du livre (numéro d'ordre = EcritureNum), équilibrée en débit / crédit, plus l'à-nouveau et les
    /// régularisations. Contenu en UTF-8, séparateur tabulation, 18 colonnes. Les comptes sont ceux saisis (voir
    /// <see cref="SetCategoryAccountNumber"/>) ou, à défaut, des numéros automatiques de classe 6, 7 et 5. Lève une
    /// erreur si une écriture n'est pas équilibrée.</summary>
    public string ExportFec(long yearId)
    {
        var year = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        var allAccounts = Accounts(true);
        var fecAccounts = new Dictionary<long, FecAccount>();
        var bankIndex = 0;
        var cashIndex = 0;
        foreach (var kindGroup in allAccounts.GroupBy(a => a.Kind))
        {
            var rank = 0;
            foreach (var a in kindGroup.OrderBy(a => a.Position).ThenBy(a => a.Id))
            {
                rank++;
                var prefix = a.Kind switch { AccountKind.Bank => "5120", AccountKind.Savings => "5121", AccountKind.Cash => "5300", _ => "4670" };
                var journal = a.Kind switch
                {
                    AccountKind.Bank or AccountKind.Savings => "BQ" + (++bankIndex),
                    AccountKind.Cash => "CA" + (++cashIndex),
                    _ => "OD"
                };
                fecAccounts[a.Id] = new FecAccount(a.AccountNumber ?? prefix + rank.ToString("D2", CultureInfo.InvariantCulture), a.Name,
                    journal, journal == "OD" ? "Opérations diverses" : $"Journal {a.Name}");
            }
        }
        var catNumbers = CategoryAccountNumbers().ToDictionary(r => (r.Kind, r.Name.ToLowerInvariant()), r => r.Effective);
        var cats = Categories(yearId).ToDictionary(c => c.Id);
        FecAccount CatAccount(long catId)
        {
            if (!cats.TryGetValue(catId, out var c)) return new FecAccount("471000", "Compte d'attente", "OD", "Opérations diverses");
            var number = catNumbers.GetValueOrDefault((c.Kind, c.Name.ToLowerInvariant())) ?? (c.Kind == CategoryKind.Product ? "706000" : "600000");
            return new FecAccount(number, c.Name, "OD", "Opérations diverses");
        }
        var waiting = new FecAccount("471000", "Compte d'attente (à classer)", "OD", "Opérations diverses");

        var lines = new List<FecLine>();
        var rows = Entries(yearId, includeCancelled: true).OrderBy(r => r.Entry.Seq).ToList();
        foreach (var r in rows)
        {
            var e = r.Entry;
            var piece = e.Seq.ToString(CultureInfo.InvariantCulture);
            var lib = FecText(e.Label);
            FecAccount? bank = e.AccountId is { } aid && fecAccounts.TryGetValue(aid, out var fa) ? fa : null;
            void Add(string journal, string journalLabel, FecAccount acc, long debit, long credit)
            {
                if (debit == 0 && credit == 0) return;
                lines.Add(new FecLine(journal, journalLabel, e.Seq, e.Date, acc.Number, acc.Label, piece, lib, debit, credit));
            }

            if (e.Kind == EntryKind.Transfer)
            {
                var from = bank ?? waiting;
                var to = e.ToAccountId is { } tid && fecAccounts.TryGetValue(tid, out var ta) ? ta : waiting;
                Add("OD", "Opérations diverses", to, e.AmountCents, 0);
                Add("OD", "Opérations diverses", from, 0, e.AmountCents);
                continue;
            }

            var journalCode = e.Kind == EntryKind.NoCash || bank is null ? "OD" : bank.Journal;
            var journalLabelText = e.Kind == EntryKind.NoCash || bank is null ? "Opérations diverses" : bank.JournalLabel;
            long productsMinusCharges = 0;
            foreach (var a in r.Allocations)
            {
                var acc = CatAccount(a.CategoryId);
                var isProduct = cats.TryGetValue(a.CategoryId, out var c) && c.Kind == CategoryKind.Product;
                // Produit : crédit ; charge : débit ; un montant négatif (remboursement, annulation) inverse le sens.
                var debit = isProduct ? (a.AmountCents < 0 ? -a.AmountCents : 0) : (a.AmountCents > 0 ? a.AmountCents : 0);
                var credit = isProduct ? (a.AmountCents > 0 ? a.AmountCents : 0) : (a.AmountCents < 0 ? -a.AmountCents : 0);
                Add(journalCode, journalLabelText, acc, debit, credit);
                productsMinusCharges += isProduct ? a.AmountCents : -a.AmountCents;
            }
            if (e.Kind != EntryKind.NoCash && bank is not null)
                Add(journalCode, journalLabelText, bank, e.NetCash > 0 ? e.NetCash : 0, e.NetCash < 0 ? -e.NetCash : 0);
            // Part non ventilée : compte d'attente, pour que chaque écriture reste équilibrée.
            var gap = e.NetCash - productsMinusCharges;
            Add(journalCode, journalLabelText, waiting, gap < 0 ? -gap : 0, gap > 0 ? gap : 0);
        }

        var lastSeq = rows.Count == 0 ? 0 : rows.Max(r => r.Entry.Seq);
        var next = lastSeq;

        // À-nouveaux : soldes d'ouverture des comptes de trésorerie.
        var openings = allAccounts.Select(a => (Account: a, Cents: Opening(yearId, a.Id))).Where(x => x.Cents != 0).ToList();
        if (openings.Count > 0)
        {
            next++;
            foreach (var (a, cents) in openings)
            {
                var fa = fecAccounts[a.Id];
                lines.Add(new FecLine("AN", "À-nouveaux", next, year.Start, fa.Number, fa.Label, "AN", "Solde d'ouverture",
                    cents > 0 ? cents : 0, cents < 0 ? -cents : 0));
            }
            var total = openings.Sum(x => x.Cents);
            lines.Add(new FecLine("AN", "À-nouveaux", next, year.Start, total >= 0 ? "110000" : "119000",
                total >= 0 ? "Report à nouveau (solde créditeur)" : "Report à nouveau (solde débiteur)", "AN", "Solde d'ouverture",
                total < 0 ? -total : 0, total > 0 ? total : 0));
        }

        // Régularisations entre exercices (charges / produits constatés d'avance, à payer, à recevoir).
        foreach (var adj in Adjustments(yearId).OrderBy(a => a.Id))
        {
            next++;
            var isProduct = adj.Kind == CategoryKind.Product;
            var cat = adj.CategoryId is { } cid ? CatAccount(cid) : waiting;
            var counter = (adj.Type, isProduct) switch
            {
                (AdjustmentType.Due, false) => new FecAccount("408000", "Fournisseurs - factures non parvenues", "OD", ""),
                (AdjustmentType.Due, true) => new FecAccount("418000", "Clients - produits non encore facturés", "OD", ""),
                (_, false) => new FecAccount("486000", "Charges constatées d'avance", "OD", ""),
                _ => new FecAccount("487000", "Produits constatés d'avance", "OD", "")
            };
            var increases = adj.Sign > 0;   // ajoute au résultat de la catégorie
            long catDebit = 0, catCredit = 0;
            if (isProduct) { if (increases) catCredit = adj.AmountCents; else catDebit = adj.AmountCents; }
            else { if (increases) catDebit = adj.AmountCents; else catCredit = adj.AmountCents; }
            var libAdj = FecText(adj.Label);
            lines.Add(new FecLine("OD", "Opérations diverses", next, year.End, cat.Number, cat.Label, "REG" + adj.Id, libAdj, catDebit, catCredit));
            lines.Add(new FecLine("OD", "Opérations diverses", next, year.End, counter.Number, counter.Label, "REG" + adj.Id, libAdj, catCredit, catDebit));
        }

        // Contrôle : chaque écriture est équilibrée, le fichier aussi.
        foreach (var g in lines.GroupBy(l => l.Num))
            if (g.Sum(l => l.Debit) != g.Sum(l => l.Credit))
                throw new ComptaException($"Export FEC impossible : l'écriture n° {g.Key} n'est pas équilibrée ({Plain(g.Sum(l => l.Debit))} au débit, {Plain(g.Sum(l => l.Credit))} au crédit).");

        var sb = new StringBuilder();
        sb.Append("JournalCode\tJournalLib\tEcritureNum\tEcritureDate\tCompteNum\tCompteLib\tCompAuxNum\tCompAuxLib\tPieceRef\tPieceDate\t")
          .Append("EcritureLib\tDebit\tCredit\tEcritureLet\tDateLet\tValidDate\tMontantdevise\tIdevise\r\n");
        var valid = year.ValidatedAt is { } v ? v.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "";
        foreach (var l in lines.OrderBy(l => l.Num).ThenBy(l => l.Debit == 0 ? 1 : 0))
        {
            var date = l.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            sb.Append(l.Journal).Append('\t').Append(FecText(l.JournalLabel)).Append('\t').Append(l.Num).Append('\t').Append(date).Append('\t')
              .Append(l.Account).Append('\t').Append(FecText(l.AccountLabel)).Append("\t\t\t")
              .Append(FecText(l.PieceRef)).Append('\t').Append(date).Append('\t').Append(FecText(l.Label)).Append('\t')
              .Append(Plain(l.Debit)).Append('\t').Append(Plain(l.Credit)).Append("\t\t\t").Append(valid).Append("\t\t\r\n");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ archive des exercices validés

    private static string SafeFileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    /// <summary>Noms des fichiers d'archive d'un exercice (livre CSV, livre PDF, FEC).</summary>
    public static (string Csv, string Pdf, string Fec) ArchiveFileNames(FiscalYear y) =>
        ($"Livre_{SafeFileName(y.Label)}.csv", $"Livre_{SafeFileName(y.Label)}.pdf", $"FEC_{SafeFileName(y.Label)}.txt");

    /// <summary>Fichiers d'archive manquants dans <paramref name="folder"/> pour ces exercices (livre CSV et FEC exigés).</summary>
    public static List<string> ArchiveFilesMissing(string? folder, IEnumerable<FiscalYear> years)
    {
        var missing = new List<string>();
        foreach (var y in years)
        {
            var (csv, _, fec) = ArchiveFileNames(y);
            foreach (var name in new[] { csv, fec })
            {
                var path = string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder, name);
                if (path is null || !File.Exists(path) || new FileInfo(path).Length == 0) missing.Add(name);
            }
        }
        return missing;
    }

    /// <summary>Exporte le livre (CSV et PDF) et le FEC de chaque exercice validé dans <paramref name="folder"/> (créé au
    /// besoin). Retourne le nombre d'exercices archivés.</summary>
    public int ArchiveValidatedYears(string folder, string? organization = null)
    {
        Directory.CreateDirectory(folder);
        var count = 0;
        foreach (var y in Years().Where(y => y.Validated))
        {
            var (csv, pdf, fec) = ArchiveFileNames(y);
            File.WriteAllText(Path.Combine(folder, csv), ExportLedgerCsv(y.Id), new UTF8Encoding(true));
            File.WriteAllBytes(Path.Combine(folder, pdf), ExportLedgerPdf(y.Id, organization));
            File.WriteAllText(Path.Combine(folder, fec), ExportFec(y.Id), new UTF8Encoding(false));
            count++;
        }
        return count;
    }
}
