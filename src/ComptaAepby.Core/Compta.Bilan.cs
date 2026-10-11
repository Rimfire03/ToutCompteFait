using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComptaAepby.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ComptaAepby.Core;

public sealed record AccountBalance(Account Account, long Opening, long ReceiptsIn, long PaymentsOut,
    long TransfersIn, long TransfersOut, long Closing);

/// <summary>Résultat d'une ligne produit ou charge : ce qui est passé dans le livre + les régularisations.</summary>
public sealed record CategoryResult(long? CategoryId, string Name, CategoryKind Kind, long? OperationId,
    long Cash, long FromPrev, long Due, long ToNext, long ForPast)
{
    /// <summary>Montant retenu pour l'exercice.</summary>
    public long Total => Cash + FromPrev + Due - ToNext - ForPast;
    public long Adjustments => Total - Cash;
    public bool Unassigned => CategoryId is null;
}

public sealed record OperationResult(Operation Operation, IReadOnlyList<CategoryResult> Products,
    IReadOnlyList<CategoryResult> Charges, long ProductsTotal, long ChargesTotal)
{
    public long Result => ProductsTotal - ChargesTotal;
}

public sealed record Issue(bool Blocking, string Message, long? EntryId = null, bool Forceable = false);

/// <summary>Compte de fonctionnement + situation (actif / passif) d'un exercice.</summary>
public sealed class Bilan
{
    public required FiscalYear Year { get; init; }
    public required IReadOnlyList<AccountBalance> Accounts { get; init; }
    public required IReadOnlyList<CategoryResult> Products { get; init; }
    public required IReadOnlyList<CategoryResult> Charges { get; init; }
    public required IReadOnlyList<OperationResult> Operations { get; init; }

    public long ProductsTotal => Products.Sum(p => p.Total);
    public long ChargesTotal => Charges.Sum(c => c.Total);
    public long Result => ProductsTotal - ChargesTotal;

    /// <summary>Total des régularisations côté produits − côté charges (signées).</summary>
    public long NetAdjustments => Products.Sum(p => p.Adjustments) - Charges.Sum(c => c.Adjustments);

    public long OpeningTotal => Accounts.Sum(a => a.Opening);
    public long ClosingTotal => Accounts.Sum(a => a.Closing);

    /// <summary>Somme des écarts de ventilation des écritures : doit être 0 pour un exercice sain.</summary>
    public required long UnallocatedGap { get; init; }
    public required int UnbalancedEntries { get; init; }

    /// <summary>Nombre d'écritures sans aucune ventilation (à classer en produit ou en charge).</summary>
    public int EntriesToFill { get; init; }

    // PASSIF = solde d'ouverture + report (−régularisations nettes) + résultat + écart non ventilé.
    public long Report => -NetAdjustments;
    public long LiabilitiesTotal => OpeningTotal + Report + Result + UnallocatedGap;

    /// <summary>Doit toujours être 0 : c'est le contrôle de cohérence du bilan.</summary>
    public long BalanceDifference => ClosingTotal - LiabilitiesTotal;
}

public sealed partial class Compta
{
    public Bilan ComputeBilan(long yearId)
    {
        var year = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        var cats = Categories(yearId);
        var catById = cats.ToDictionary(c => c.Id);
        var rows = Entries(yearId);
        var adjs = Adjustments(yearId);

        // --- comptes
        var balances = new List<AccountBalance>();
        foreach (var a in Accounts(includeInactive: true))
        {
            long inn = 0, outt = 0, trIn = 0, trOut = 0;
            foreach (var r in rows)
            {
                var e = r.Entry;
                if (e.Kind == EntryKind.Transfer)
                {
                    if (e.AccountId == a.Id) trOut += e.AmountCents;
                    if (e.ToAccountId == a.Id) trIn += e.AmountCents;
                }
                else if (e.AccountId == a.Id)
                {
                    if (e.Direction == Direction.In) inn += e.AmountCents; else outt += e.AmountCents;
                }
            }
            var opening = Opening(yearId, a.Id);
            // Un compte inactif sans mouvement ni solde n'apparaît pas.
            if (!a.Active && opening == 0 && inn == 0 && outt == 0 && trIn == 0 && trOut == 0) continue;
            balances.Add(new AccountBalance(a, opening, inn, outt, trIn, trOut, opening + inn - outt + trIn - trOut));
        }

        // --- catégories
        var cash = new Dictionary<long, long>();
        long gap = 0;
        var unbalanced = 0;
        var toFill = 0;
        foreach (var r in rows)
        {
            long v = 0;
            foreach (var al in r.Allocations)
            {
                cash[al.CategoryId] = cash.GetValueOrDefault(al.CategoryId) + al.AmountCents;
                v += catById[al.CategoryId].Kind == CategoryKind.Product ? al.AmountCents : -al.AmountCents;
            }
            var g = r.Entry.NetCash - v;
            if (g != 0) { gap += g; unbalanced++; }
            if (NeedsAllocation(r)) toFill++;
        }

        CategoryResult Build(Category c)
        {
            long Sum(AdjustmentType t) => adjs.Where(a => a.CategoryId == c.Id && a.Type == t).Sum(a => a.AmountCents);
            return new CategoryResult(c.Id, c.Name, c.Kind, c.OperationId, cash.GetValueOrDefault(c.Id),
                Sum(AdjustmentType.FromPrev), Sum(AdjustmentType.Due), Sum(AdjustmentType.ToNext), Sum(AdjustmentType.ForPast));
        }

        List<CategoryResult> Lines(CategoryKind kind)
        {
            var list = cats.Where(c => c.Kind == kind).OrderBy(c => c.Position).ThenBy(c => c.Id).Select(Build).ToList();
            // Régularisations sans catégorie : regroupées sur une ligne « À affecter » pour ne rien perdre.
            var un = adjs.Where(a => a.CategoryId is null && a.Kind == kind).ToList();
            if (un.Count > 0)
            {
                long Sum(AdjustmentType t) => un.Where(a => a.Type == t).Sum(a => a.AmountCents);
                list.Add(new CategoryResult(null, "À affecter (régularisations sans catégorie)", kind, null, 0,
                    Sum(AdjustmentType.FromPrev), Sum(AdjustmentType.Due), Sum(AdjustmentType.ToNext), Sum(AdjustmentType.ForPast)));
            }
            return list;
        }

        var products = Lines(CategoryKind.Product);
        var charges = Lines(CategoryKind.Charge);

        // --- opérations (produits et charges liés)
        var operations = new List<OperationResult>();
        foreach (var op in Operations(yearId))
        {
            var p = products.Where(x => x.OperationId == op.Id).ToList();
            var c = charges.Where(x => x.OperationId == op.Id).ToList();
            operations.Add(new OperationResult(op, p, c, p.Sum(x => x.Total), c.Sum(x => x.Total)));
        }

        return new Bilan
        {
            Year = year, Accounts = balances, Products = products, Charges = charges, Operations = operations,
            UnallocatedGap = gap, UnbalancedEntries = unbalanced, EntriesToFill = toFill
        };
    }

    // ------------------------------------------------------------------ clôture

    /// <summary>Problèmes à résoudre avant de valider un exercice (bloquants) ou à connaître (avertissements).</summary>
    public List<Issue> ValidationIssues(long yearId, DateOnly? closingDate = null)
    {
        var issues = new List<Issue>();
        var year = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        var closing = closingDate ?? year.End;
        var prev = PreviousYear(year);
        if (prev is not null && !prev.Validated)
            issues.Add(new Issue(true, $"L'exercice précédent ({prev.Label}) doit être validé avant celui-ci : ses reports alimentent cet exercice."));

        var cats = Categories(yearId).ToDictionary(c => c.Id);
        var rows = Entries(yearId);
        if (rows.All(r => r.Entry.Date > closing)) issues.Add(new Issue(false, "L'exercice ne contient aucune écriture."));
        var movedCount = 0;
        foreach (var r in rows)
        {
            var e = r.Entry;
            if (e.Date > closing) { movedCount++; continue; } // déplacée sur l'exercice suivant : contrôlée là-bas
            var gap = Imbalance(e, r.Allocations, cats);
            if (gap != 0)
            {
                var msg = NeedsAllocation(r)
                    ? $"{e.Date:dd/MM/yyyy} « {e.Label} » : {Money.Format(Math.Abs(e.NetCash))} € à classer en produit ou en charge."
                    : $"{e.Date:dd/MM/yyyy} « {e.Label} » : {Money.Format(gap)} € non ventilé(s) (ou ventilation incohérente avec le montant).";
                issues.Add(new Issue(true, msg, e.Id, Forceable: true));
            }
            if (e.Date < year.Start)
                issues.Add(new Issue(true,
                    $"{e.Date:dd/MM/yyyy} « {e.Label} » : date antérieure au début de l'exercice ({year.Start:dd/MM/yyyy}).", e.Id, Forceable: true));
        }
        if (movedCount > 0)
            issues.Add(new Issue(false, $"{movedCount} écriture(s) datée(s) après le {closing:dd/MM/yyyy} seront déplacée(s) dans l'exercice suivant."));
        foreach (var a in Adjustments(yearId).Where(a => a.CategoryId is null))
            issues.Add(new Issue(true, $"Report « {a.Label} » ({Money.Format(a.AmountCents)} €) : catégorie à affecter."));

        var unpointed = rows.Count(r => !r.Entry.Pointed);
        if (unpointed > 0)
            issues.Add(new Issue(false, $"{unpointed} écriture(s) non pointée(s) sur le relevé bancaire."));

        var b = ComputeBilan(yearId);
        if (b.BalanceDifference != 0)
            issues.Add(new Issue(true, $"Le bilan n'est pas équilibré (écart {Money.Format(b.BalanceDifference)} €) : anomalie interne, ne pas valider."));
        return issues;
    }

    /// <summary>Empreinte (SHA-256) du contenu comptable d'un exercice : permet de détecter toute modification
    /// faite hors du logiciel après la validation.</summary>
    public string ComputeChecksum(long yearId)
    {
        var sb = new StringBuilder();
        var y = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        sb.Append("Y|").Append(y.Label).Append('|').Append(D(y.Start)).Append('|').Append(D(y.End)).Append('\n');
        foreach (var o in Operations(yearId)) sb.Append("O|").Append(o.Id).Append('|').Append(o.Name).Append('\n');
        foreach (var c in Categories(yearId))
            sb.Append("C|").Append(c.Id).Append('|').Append(c.Kind).Append('|').Append(c.Name).Append('|').Append(c.OperationId).Append('\n');
        foreach (var r in Entries(yearId))
        {
            var e = r.Entry;
            sb.Append("E|").Append(e.Id).Append('|').Append(D(e.Date)).Append('|').Append(e.Label).Append('|')
              .Append(e.Pointed ? 1 : 0).Append('|').Append(e.Kind).Append('|').Append(e.AccountId).Append('|')
              .Append(e.Direction).Append('|').Append(e.ToAccountId).Append('|').Append(e.AmountCents).Append('|')
              .Append(e.MemberName).Append('\n');
            foreach (var a in r.Allocations.OrderBy(a => a.CategoryId).ThenBy(a => a.AmountCents))
                sb.Append("A|").Append(e.Id).Append('|').Append(a.CategoryId).Append('|').Append(a.AmountCents).Append('\n');
        }
        foreach (var a in Adjustments(yearId))
            sb.Append("J|").Append(a.Id).Append('|').Append(a.Type).Append('|').Append(a.Kind).Append('|').Append(a.CategoryId)
              .Append('|').Append(a.Label).Append('|').Append(a.AmountCents).Append('\n');
        foreach (var ac in Accounts(true))
            sb.Append("B|").Append(ac.Id).Append('|').Append(Opening(yearId, ac.Id)).Append('\n');
        // Justificatifs (ajoutés à l'empreinte seulement s'il y en a : les exercices validés avant leur
        // introduction gardent la même empreinte).
        using (var l = Open())
        {
            var ctx = l.Ctx;
            var atts = (from a in ctx.Attachments.AsNoTracking()
                        join e in ctx.Entries.AsNoTracking() on a.EntryId equals e.Id
                        where e.YearId == yearId
                        orderby a.Id
                        select new { a.Id, a.EntryId, a.FileName, a.SizeBytes }).ToList();
            foreach (var a in atts)
                sb.Append("T|").Append(a.Id).Append('|').Append(a.EntryId).Append('|').Append(a.FileName).Append('|').Append(a.SizeBytes).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>True si le contenu d'un exercice validé est toujours conforme à l'empreinte enregistrée.</summary>
    public bool VerifyIntegrity(long yearId)
    {
        var y = Year(yearId);
        if (y is null || !y.Validated) return true;
        string? stored;
        using (var l = Open())
            stored = l.Ctx.Years.AsNoTracking().Where(x => x.Id == yearId).Select(x => x.Checksum).FirstOrDefault();
        return stored is not null && string.Equals(stored, ComputeChecksum(yearId), StringComparison.Ordinal);
    }

    /// <summary>Valide (clôture) un exercice à la date de clôture indiquée (par défaut : la fin prévue de l'exercice).
    /// Les écritures datées après cette date sont déplacées dans l'exercice suivant (créé si besoin, début = lendemain
    /// de la clôture). Ensuite, plus aucune écriture, catégorie ni report ne peut être ajouté, modifié ou retiré.</summary>
    /// <param name="force">Clôture forcée (administrateur seulement) malgré des écritures non ventilées ou datées avant
    /// le début ; les anomalies structurelles (exercice précédent non validé, report sans catégorie, bilan déséquilibré)
    /// restent bloquantes. Les points ignorés sont consignés dans le journal et dans l'empreinte de clôture.</param>
    public void ValidateYear(long yearId, DateOnly? closingDate = null, bool force = false)
    {
        RequireOpen(yearId, Permission.ValidateYear);
        var year = Year(yearId)!;
        var closing = closingDate ?? year.End;
        if (closing < year.Start)
            throw new ComptaException($"La date de clôture ne peut pas précéder le début de l'exercice ({year.Start:dd/MM/yyyy}).");
        var blocking = ValidationIssues(yearId, closing).Where(i => i.Blocking).ToList();
        var forcedIssues = new List<Issue>();
        if (force && blocking.Count > 0)
        {
            if (Session.Role != Role.Admin) throw new ComptaException("Seul un administrateur peut forcer une clôture.");
            forcedIssues = blocking;   // un forçage administrateur ne doit jamais échouer : tout est ignoré
            blocking = new List<Issue>();
        }
        if (blocking.Count > 0)
            throw new ComptaException("Validation impossible :\n• " + string.Join("\n• ", blocking.Take(8).Select(i => i.Message))
                + (blocking.Count > 8 ? $"\n… et {blocking.Count - 8} autre(s)." : ""));

        using var tx = Begin();
        var next = NextYear(year);
        if (next is not null && next.Validated && closing != year.End)
            throw new ComptaException($"L'exercice suivant ({next.Label}) est validé : la date de clôture ne peut pas être modifiée.");
        var origEnd = year.End;
        string? forcedNote = null;
        if (force && closing != origEnd && next is not null)
        {
            // Forçage : un conflit de dates avec l'exercice suivant ne fait pas échouer la clôture ; on retombe sur la
            // fin prévue de l'exercice.
            var conflict = next.Validated || closing.AddDays(1) > next.End
                           || (closing > origEnd && Entries(next.Id).Any(r => r.Entry.Date <= closing));
            if (conflict)
            {
                forcedNote = $"date de clôture {closing:dd/MM/yyyy} impossible avec l'exercice suivant, ramenée à la fin prévue {origEnd:dd/MM/yyyy}";
                closing = origEnd;
            }
        }

        // 1. Période : la fin de l'exercice devient la date de clôture ; l'exercice suivant commence le lendemain.
        if (closing != origEnd)
        {
            if (next is not null)
            {
                var newStart = closing.AddDays(1);
                if (newStart > next.End)
                    throw new ComptaException($"L'exercice suivant ({next.Label}) se termine le {next.End:dd/MM/yyyy} : date de clôture trop tardive.");
                if (closing > origEnd && Entries(next.Id).Any(r => r.Entry.Date <= closing))
                    throw new ComptaException($"L'exercice suivant ({next.Label}) contient des écritures datées jusqu'au {closing:dd/MM/yyyy} : " +
                                              "choisissez une date de clôture antérieure ou déplacez-les d'abord.");
                if (closing > origEnd) SetYearPeriod(next.Id, newStart, next.End);
                SetYearPeriod(yearId, year.Start, closing);
                if (closing < origEnd) SetYearPeriod(next.Id, newStart, next.End);
            }
            else SetYearPeriod(yearId, year.Start, closing);
        }

        // 2. Écritures après la date de clôture : déplacées dans l'exercice suivant.
        var moved = 0;
        var closingText = D(closing);
        using (var l = Open())
            moved = l.Ctx.Entries.AsNoTracking().Count(e => e.YearId == yearId && string.Compare(e.EntryDate, closingText) > 0);
        if (moved > 0)
        {
            if (next is null)
            {
                var days = origEnd.DayNumber - year.Start.DayNumber;
                var start2 = closing.AddDays(1);
                var end2 = days is >= 358 and <= 372 ? start2.AddYears(1).AddDays(-1) : start2.AddDays(days);
                next = CreateYear(start2, end2, cloneStructure: true);
            }
            MoveEntriesToNextYear(yearId, next.Id, closingText);
        }

        // Dernier report vers l'exercice suivant avant verrouillage.
        SyncCarry(yearId);
        var b = ComputeBilan(yearId);
        var snapshot = JsonSerializer.Serialize(new
        {
            year = b.Year.Label,
            closingDate = closingText,
            opening = b.OpeningTotal,
            closing = b.ClosingTotal,
            products = b.ProductsTotal,
            charges = b.ChargesTotal,
            result = b.Result,
            report = b.Report,
            accounts = b.Accounts.Select(a => new { a.Account.Name, a.Opening, a.Closing }),
            forced = forcedIssues.Count == 0 ? null : new { by = UserName, count = forcedIssues.Count, issues = forcedIssues.Take(50).Select(i => i.Message) }
        });
        var sum = ComputeChecksum(yearId);
        var ctx = Open().Ctx;
        var e = ctx.Years.First(y => y.Id == yearId);
        e.Status = "VALIDATED";
        e.ValidatedAt = Now();
        e.ValidatedBy = UserName;
        e.Checksum = sum;
        e.ClosingJson = snapshot;
        Save(ctx);
        Log(ctx, "Exercice validé", $"{b.Year.Label} — clôture au {closing:dd/MM/yyyy}" +
            (moved > 0 ? $", {moved} écriture(s) déplacée(s) vers l'exercice suivant" : "") +
            (forcedIssues.Count > 0 ? $" — CLÔTURE FORCÉE par un administrateur malgré {forcedIssues.Count} point(s) bloquant(s)" : "") +
            (forcedNote is not null ? " ; " + forcedNote : ""));
        tx.Commit();
    }

    /// <summary>Déplace les écritures de <paramref name="fromYearId"/> datées après <paramref name="afterDate"/> (yyyy-MM-dd)
    /// vers <paramref name="toYearId"/>, en rattachant leur ventilation aux catégories de même nom (créées si absentes).</summary>
    private void MoveEntriesToNextYear(long fromYearId, long toYearId, string afterDate)
    {
        var ctx = Open().Ctx;
        var entries = ctx.Entries.Where(e => e.YearId == fromYearId && string.Compare(e.EntryDate, afterDate) > 0).ToList();
        if (entries.Count == 0) return;
        var ids = entries.Select(e => e.Id).ToList();
        var allocs = ctx.Allocations.Where(a => ids.Contains(a.EntryId)).ToList();

        var fromCats = ctx.Categories.AsNoTracking().Where(c => c.YearId == fromYearId).ToDictionary(c => c.Id);
        var fromOps = ctx.Operations.AsNoTracking().Where(o => o.YearId == fromYearId).ToDictionary(o => o.Id);
        var toCats = ctx.Categories.AsNoTracking().Where(c => c.YearId == toYearId).ToList();
        var toOps = ctx.Operations.AsNoTracking().Where(o => o.YearId == toYearId).ToList();

        var map = new Dictionary<long, long>();
        foreach (var catId in allocs.Select(a => a.CategoryId).Distinct())
        {
            if (!fromCats.TryGetValue(catId, out var src)) continue;
            var target = toCats.FirstOrDefault(c => c.Kind == src.Kind && SameName(c.Name, src.Name));
            if (target is null)
            {
                long? opId = null;
                if (src.OperationId is { } oid && fromOps.TryGetValue(oid, out var op))
                    opId = toOps.FirstOrDefault(o => SameName(o.Name, op.Name))?.Id;
                target = new ComptaAepby.Core.Data.CategoryEntity
                {
                    YearId = toYearId, Kind = src.Kind, Name = src.Name, OperationId = opId,
                    Position = (toCats.Count == 0 ? 0 : toCats.Max(c => c.Position)) + 1
                };
                ctx.Categories.Add(target);
                ctx.SaveChanges();
                toCats.Add(target);
            }
            map[catId] = target.Id;
        }

        foreach (var a in allocs)
            if (map.TryGetValue(a.CategoryId, out var nid)) a.CategoryId = nid;
        foreach (var e in entries) e.YearId = toYearId;
        Save(ctx);
    }
    /// <summary>Rouvre un exercice validé (correction exceptionnelle, réservée à l'administrateur). Refusé si
    /// l'exercice suivant est validé. L'opération est tracée dans le journal.</summary>
    public void ReopenYear(long yearId, string reason)
    {
        Require(Permission.ReopenYear);
        RequireWritable();
        var y = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        if (!y.Validated) throw new ComptaException("Cet exercice n'est pas validé.");
        var next = NextYear(y);
        if (next is { Validated: true })
            throw new ComptaException($"L'exercice suivant ({next.Label}) est validé : rouvrez-le d'abord.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ComptaException("Indiquez le motif de la réouverture.");
        using var tx = Begin();
        var ctx = Open().Ctx;
        var e = ctx.Years.First(x => x.Id == yearId);
        e.Status = "OPEN";
        e.ValidatedAt = null;
        e.ValidatedBy = null;
        e.Checksum = null;
        e.ClosingJson = null;
        Save(ctx);
        Log(ctx, "Exercice rouvert", $"{y.Label} — {reason.Trim()}");
        tx.Commit();
    }
}
