using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Import;
using ToutCompteFait.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

public enum BankLineStatus
{
    /// <summary>Aucune écriture correspondante : sera créée, sans produit ni charge (à ventiler ensuite).</summary>
    New,
    /// <summary>Écriture retrouvée (même compte, date et montant) : son intitulé sera remplacé par celui de la banque.</summary>
    LabelUpdated,
    /// <summary>Écriture retrouvée, intitulé déjà identique.</summary>
    Unchanged,
    /// <summary>La date ne tombe dans aucun exercice : ligne non importée.</summary>
    NoYear,
    /// <summary>La date tombe dans un exercice validé : ligne non importée (rien n'est modifié).</summary>
    YearValidated,
    /// <summary>Doute : une écriture de même montant existe à quelques jours près. L'utilisateur doit trancher
    /// (rapprocher, créer une nouvelle écriture ou ignorer la ligne) avant d'importer.</summary>
    ToConfirm
}

/// <summary>Écriture existante qui pourrait correspondre à une ligne du relevé (même compte et même montant, date
/// proche).</summary>
public sealed record BankMatchCandidate(long EntryId, DateOnly Date, string Label, bool Allocated, int DaysApart,
    long? CounterpartAccountId = null)
{
    /// <summary>Contrepartie sur un autre compte : la ligne du relevé et cette écriture forment un même virement entre
    /// comptes.</summary>
    public bool IsTransfer => CounterpartAccountId is not null;
}

public sealed record BankLinePlan(BankLine Line, BankLineStatus Status, long? YearId, long? EntryId, string? OldLabel,
    IReadOnlyList<BankMatchCandidate>? Candidates = null);

public sealed record BankImportPlan(long AccountId, IReadOnlyList<BankLinePlan> Lines)
{
    public int Count(BankLineStatus s) => Lines.Count(l => l.Status == s);
}

public enum BankDecisionKind { Match, CreateNew, Skip, Transfer }

/// <summary>Choix de l'utilisateur pour une ligne douteuse (<see cref="BankLineStatus.ToConfirm"/>).</summary>
public sealed record BankDecision(BankDecisionKind Kind, long? EntryId = null);

public sealed record BankImportResult(int Created, int LabelsUpdated, int Pointed, int Unchanged, int Skipped, int DatesAligned = 0,
    int TransfersLinked = 0);

public sealed partial class Compta
{
    /// <summary>Écart de dates toléré (en jours) entre une ligne du relevé et une écriture déjà saisie : la banque
    /// date souvent l'opération autrement que la saisie (date de carte, de valeur, de traitement).</summary>
    public const int BankDateToleranceDays = 7;

    /// <summary>Écart de dates toléré pour reconnaître les deux moitiés d'un virement entre comptes (les banques
    /// datent généralement les deux côtés du même jour ou à un jour d'écart).</summary>
    public const int BankTransferToleranceDays = 4;

    // ------------------------------------------------------------------ import d'un relevé bancaire

    /// <summary>Compare un relevé aux écritures existantes du compte, sans rien modifier.
    /// 1. Rapprochement sûr : même compte, même date, même montant signé (à égalité, l'intitulé identique est préféré).
    /// 2. Rapprochement douteux : même compte et même montant, date à moins de <see cref="BankDateToleranceDays"/> jours
    ///    d'une écriture non rapprochée → statut <see cref="BankLineStatus.ToConfirm"/>, à valider par l'utilisateur.
    /// 3. Sinon : nouvelle écriture.</summary>
    public BankImportPlan PlanBankImport(long accountId, IReadOnlyList<BankLine> lines)
    {
        Require(Permission.WriteLedger);
        if (Account(accountId) is null) throw new ComptaException("Compte introuvable.");
        var years = Years();
        var validatedYears = years.Where(y => y.Validated).Select(y => y.Id).ToHashSet();

        // Écritures de ce compte, avec leur montant signé du point de vue du compte.
        var flat = new List<(Entry E, long Signed, EntryRow Row)>();
        foreach (var y in years)
        {
            foreach (var r in Entries(y.Id))
            {
                var e = r.Entry;
                long? signed = e.Kind switch
                {
                    EntryKind.Transfer when e.AccountId == accountId => -e.AmountCents,
                    EntryKind.Transfer when e.ToAccountId == accountId => e.AmountCents,
                    EntryKind.Normal or EntryKind.MemberRefund when e.AccountId == accountId =>
                        e.Direction == Direction.In ? e.AmountCents : -e.AmountCents,
                    _ => null
                };
                if (signed is not null) flat.Add((e, signed.Value, r));
            }
        }
        var pools = flat.GroupBy(x => (x.E.YearId, x.E.Date, x.Signed)).ToDictionary(g => g.Key, g => g.Select(x => x.E).ToList());

        // Écritures ordinaires des AUTRES comptes (contrepartie possible d'un virement entre comptes).
        var others = new List<(Entry E, long Signed, EntryRow Row)>();
        foreach (var y in years.Where(y => !y.Validated))
            foreach (var r in Entries(y.Id))
            {
                var e = r.Entry;
                if (e.Kind != EntryKind.Normal || e.AccountId is null || e.AccountId == accountId || e.RefundOfId is not null) continue;
                others.Add((e, e.Direction == Direction.In ? e.AmountCents : -e.AmountCents, r));
            }

        var used = new HashSet<long>();
        var usedCounterparts = new HashSet<long>();   // contreparties déjà proposées à une autre ligne de ce relevé
        var plan = new List<BankLinePlan?>();
        var yearOf = new List<FiscalYear?>();

        // Passe 1 : rapprochements sûrs.
        foreach (var line in lines)
        {
            var year = years.FirstOrDefault(y => y.Contains(line.Date));
            yearOf.Add(year);
            if (year is null) { plan.Add(new BankLinePlan(line, BankLineStatus.NoYear, null, null, null)); continue; }

            Entry? match = null;
            if (pools.TryGetValue((year.Id, line.Date, line.AmountCents), out var candidates))
            {
                match = candidates.FirstOrDefault(c => !used.Contains(c.Id) && SameLabel(c.Label, line.Label))
                        ?? candidates.FirstOrDefault(c => !used.Contains(c.Id));
            }
            if (match is not null) used.Add(match.Id);

            if (year.Validated)
                plan.Add(new BankLinePlan(line, BankLineStatus.YearValidated, year.Id, match?.Id, match?.Label));
            else if (match is null)
                plan.Add(null); // décidé à la passe 2
            else if (SameLabel(match.Label, line.Label))
                plan.Add(new BankLinePlan(line, BankLineStatus.Unchanged, year.Id, match.Id, match.Label));
            else
                plan.Add(new BankLinePlan(line, BankLineStatus.LabelUpdated, year.Id, match.Id, match.Label));
        }

        // Passe 2 : les lignes restantes cherchent une écriture proche en date (jamais déjà rapprochée, jamais dans
        // un exercice validé : on ne peut pas la modifier).
        for (var i = 0; i < lines.Count; i++)
        {
            if (plan[i] is not null) continue;
            var line = lines[i];
            var year = yearOf[i]!;
            var near = flat
                .Where(x => x.Signed == line.AmountCents && !used.Contains(x.E.Id) && !validatedYears.Contains(x.E.YearId))
                .Select(x => (x.E, Days: Math.Abs(x.E.Date.DayNumber - line.Date.DayNumber), x.Row))
                .Where(x => x.Days <= BankDateToleranceDays)
                .OrderBy(x => x.Days).ThenBy(x => SameLabel(x.E.Label, line.Label) ? 0 : 1).ThenBy(x => x.E.Id)
                .Take(5)
                .ToList();
            // Virement entre comptes : même jour (à quelques jours près), montant opposé, autre compte, même exercice.
            var counterparts = others
                .Where(x => x.Signed == -line.AmountCents && x.E.YearId == year.Id && !used.Contains(x.E.Id) && !usedCounterparts.Contains(x.E.Id))
                .Select(x => (x.E, Days: Math.Abs(x.E.Date.DayNumber - line.Date.DayNumber), x.Row))
                .Where(x => x.Days <= BankTransferToleranceDays)
                .OrderBy(x => x.Days).ThenBy(x => x.E.Id).Take(3).ToList();
            if (near.Count == 0 && counterparts.Count == 0)
            {
                plan[i] = new BankLinePlan(line, BankLineStatus.New, year.Id, null, null);
                continue;
            }
            if (near.Count == 0 && counterparts.Count > 0) usedCounterparts.Add(counterparts[0].E.Id);
            var cands = near.Select(n => new BankMatchCandidate(n.E.Id, n.E.Date, n.E.Label, n.Row.Allocations.Count > 0, n.Days))
                .Concat(counterparts.Select(n => new BankMatchCandidate(n.E.Id, n.E.Date, n.E.Label, n.Row.Allocations.Count > 0, n.Days,
                    n.E.AccountId)))
                .ToList();
            plan[i] = new BankLinePlan(line, BankLineStatus.ToConfirm, year.Id, cands[0].EntryId, cands[0].Label, cands);
        }
        return new BankImportPlan(accountId, plan.Select(p => p!).ToList());
    }

    private static bool SameLabel(string a, string b) =>
        string.Equals(BankStatementParser.Clean(a), BankStatementParser.Clean(b), StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Applique le plan : met à jour les intitulés retrouvés (jamais la ventilation) et crée les lignes
    /// nouvelles <b>sans produit ni charge</b>.
    /// <paramref name="decisions"/> : choix de l'utilisateur, par numéro de ligne du plan, pour chaque ligne
    /// <see cref="BankLineStatus.ToConfirm"/> — aucune n'est appliquée sans décision explicite.
    /// <paramref name="markPointed"/> : les lignes du relevé sont pointées.
    /// <paramref name="alignDates"/> : pour les rapprochements confirmés, la date de l'écriture prend celle de la banque
    /// (si elle reste dans le même exercice). Tout ou rien (une seule transaction).</summary>
    public BankImportResult ApplyBankImport(BankImportPlan plan, bool markPointed = true,
        IReadOnlyDictionary<int, BankDecision>? decisions = null, bool alignDates = false)
    {
        Require(Permission.WriteLedger);
        RequireWritable();

        var undecided = plan.Lines.Select((l, i) => (l, i))
            .Where(x => x.l.Status == BankLineStatus.ToConfirm && (decisions is null || !decisions.ContainsKey(x.i))).ToList();
        if (undecided.Count > 0)
            throw new ComptaException($"{undecided.Count} ligne(s) du relevé ont une correspondance incertaine : " +
                                      "indiquez pour chacune de la rapprocher, de créer une nouvelle écriture ou de l'ignorer.");

        int created = 0, updated = 0, pointed = 0, unchanged = 0, skipped = 0, aligned = 0, transfers = 0;
        var createdEntities = new List<EntryEntity>();
        var changes = new List<string>();
        var matchedEntries = new HashSet<long>();
        foreach (var l in plan.Lines)
            if (l.Status is BankLineStatus.LabelUpdated or BankLineStatus.Unchanged && l.EntryId is { } id) matchedEntries.Add(id);

        using var tx = Begin();
        var ctx = Open().Ctx;
        var now = Now();

        for (var i = 0; i < plan.Lines.Count; i++)
        {
            var p = plan.Lines[i];
            var status = p.Status;
            long? entryId = p.EntryId;
            var fuzzy = false;
            var linkTransfer = false;

            if (status == BankLineStatus.ToConfirm)
            {
                var d = decisions![i];
                switch (d.Kind)
                {
                    case BankDecisionKind.Skip:
                        skipped++;
                        continue;
                    case BankDecisionKind.CreateNew:
                        status = BankLineStatus.New;
                        break;
                    case BankDecisionKind.Transfer:
                        if (d.EntryId is not { } other || p.Candidates is null || p.Candidates.All(c => c.EntryId != other || !c.IsTransfer))
                            throw new ComptaException("Le virement choisi n'est pas l'une des écritures proposées.");
                        if (!matchedEntries.Add(other))
                            throw new ComptaException("Une même écriture est rapprochée de deux lignes du relevé : revoyez vos choix.");
                        entryId = other;
                        status = BankLineStatus.LabelUpdated;   // traité plus bas
                        linkTransfer = true;
                        break;
                    default:
                        if (d.EntryId is not { } chosen || p.Candidates is null || p.Candidates.All(c => c.EntryId != chosen))
                            throw new ComptaException("Le rapprochement choisi n'est pas l'une des écritures proposées.");
                        if (!matchedEntries.Add(chosen))
                            throw new ComptaException("Une même écriture est rapprochée de deux lignes du relevé : revoyez vos choix.");
                        entryId = chosen;
                        fuzzy = true;
                        var existing = p.Candidates.First(c => c.EntryId == chosen);
                        status = SameLabel(existing.Label, p.Line.Label) ? BankLineStatus.Unchanged : BankLineStatus.LabelUpdated;
                        break;
                }
            }

            switch (status)
            {
                case BankLineStatus.NoYear:
                case BankLineStatus.YearValidated:
                    skipped++;
                    break;

                case BankLineStatus.New:
                {
                    var year = Year(p.YearId!.Value)!;
                    if (year.Validated) { skipped++; break; } // exercice validé depuis le calcul du plan
                    var fresh = new EntryEntity
                    {
                        YearId = year.Id,
                        EntryDate = D(p.Line.Date),
                        Label = p.Line.Label,
                        Pointed = markPointed,
                        Kind = "NORMAL",
                        AccountId = plan.AccountId,
                        Direction = p.Line.AmountCents >= 0 ? "IN" : "OUT",
                        AmountCents = Math.Abs(p.Line.AmountCents),
                        ImportRef = p.Line.ExternalId is { Length: > 0 } id ? "banque:" + id : "banque",
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    ctx.Entries.Add(fresh);
                    createdEntities.Add(fresh);
                    created++;
                    break;
                }

                default: // LabelUpdated, Unchanged
                {
                    var e = ctx.Entries.FirstOrDefault(x => x.Id == entryId!.Value);
                    if (e is null) { skipped++; break; }
                    if (linkTransfer)
                    {
                        // L'écriture de l'autre compte et cette ligne sont les deux moitiés d'un même virement : l'écriture
                        // existante devient le virement (du compte qui paie vers le compte qui reçoit), sans ventilation.
                        var lineAccount = plan.AccountId;
                        var otherAccount = e.AccountId!.Value;
                        var lineReceives = p.Line.AmountCents > 0;
                        changes.Add($"n° {e.Seq} : « {e.Label} » devenue virement entre comptes ({Money.Format(e.AmountCents)} €)");
                        e.Kind = "TRANSFER";
                        e.Direction = "OUT";
                        e.AccountId = lineReceives ? otherAccount : lineAccount;
                        e.ToAccountId = lineReceives ? lineAccount : otherAccount;
                        e.MemberName = null;
                        e.ImportRef ??= "banque";
                        ctx.Allocations.RemoveRange(ctx.Allocations.Where(a => a.EntryId == e.Id));
                        if (markPointed) e.Pointed = true;
                        e.UpdatedAt = now;
                        transfers++;
                        break;
                    }
                    var changed = false;
                    // Écriture saisie à la main puis retrouvée sur le relevé : le montant est désormais celui de la banque.
                    if (e.ImportRef is null) { e.ImportRef = "banque"; changed = true; }
                    if (status == BankLineStatus.LabelUpdated && e.Label != p.Line.Label)
                    {
                        changes.Add($"n° {e.Seq} : « {e.Label} » → « {p.Line.Label} »");
                        e.Label = p.Line.Label;
                        changed = true;
                        updated++;
                    }
                    else unchanged++;
                    if (markPointed && !e.Pointed)
                    {
                        e.Pointed = true;
                        changed = true;
                        pointed++;
                    }
                    if (fuzzy && alignDates && e.EntryDate != D(p.Line.Date))
                    {
                        // La date de la banque ne doit pas faire changer l'écriture d'exercice.
                        var y = Year(e.YearId);
                        if (y is not null && y.Contains(p.Line.Date))
                        {
                            changes.Add($"n° {e.Seq} : date {ParseDate(e.EntryDate):dd/MM/yyyy} → {p.Line.Date:dd/MM/yyyy}");
                            e.EntryDate = D(p.Line.Date);
                            changed = true;
                            aligned++;
                        }
                    }
                    if (changed) e.UpdatedAt = now;
                    break;
                }
            }
        }
        Save(ctx);
        // Détail pour la piste d'audit : numéros des écritures créées (numérotation continue) et intitulés remplacés.
        var detail = new System.Text.StringBuilder();
        detail.Append($"{Account(plan.AccountId)?.Name} : {created} créée(s), {updated} intitulé(s) mis à jour, {pointed} pointée(s)");
        if (aligned > 0) detail.Append($", {aligned} date(s) alignée(s)");
        if (transfers > 0) detail.Append($", {transfers} virement(s) entre comptes reconnu(s)");
        detail.Append($", {skipped} ignorée(s)");
        foreach (var g in createdEntities.GroupBy(c => c.YearId))
        {
            var label = Year(g.Key)?.Label ?? g.Key.ToString();
            var seqs = g.Select(c => c.Seq).OrderBy(s => s).ToList();
            detail.Append($"\nExercice {label} : écritures créées n° {string.Join(", ", seqs.Take(60))}" +
                          (seqs.Count > 60 ? $" … ({seqs.Count} au total)" : ""));
        }
        foreach (var c in changes.Take(60)) detail.Append("\nÉcriture ").Append(c);
        if (changes.Count > 60) detail.Append($"\n… {changes.Count - 60} autre(s) modification(s)");
        Log(ctx, "Relevé bancaire importé", detail.ToString());
        tx.Commit();
        return new BankImportResult(created, updated, pointed, unchanged, skipped, aligned, transfers);
    }
}
