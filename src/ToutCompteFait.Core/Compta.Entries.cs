using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

public sealed partial class Compta
{
    // ------------------------------------------------------------------ écritures du livre

    /// <summary>Toutes les écritures d'un exercice, triées par date puis par ordre de saisie.</summary>
    public List<EntryRow> Entries(long yearId)
    {
        using var l = Open();
        var ctx = l.Ctx;
        var entries = ctx.Entries.AsNoTracking().Where(e => e.YearId == yearId)
            .OrderBy(e => e.EntryDate).ThenBy(e => e.Id).ToList();
        var allocs = (from a in ctx.Allocations.AsNoTracking()
                      join e in ctx.Entries.AsNoTracking() on a.EntryId equals e.Id
                      where e.YearId == yearId
                      orderby a.Id
                      select a).ToList();
        var byEntry = allocs.GroupBy(a => a.EntryId)
            .ToDictionary(g => g.Key, g => g.Select(x => new Allocation(x.CategoryId, x.AmountCents)).ToList());
        return entries.Select(e => new EntryRow(ToEntry(e),
            byEntry.TryGetValue(e.Id, out var list) ? list : (IReadOnlyList<Allocation>)Array.Empty<Allocation>())).ToList();
    }

    public EntryRow? EntryById(long id)
    {
        using var l = Open();
        var e = l.Ctx.Entries.AsNoTracking().FirstOrDefault(x => x.Id == id);
        if (e is null) return null;
        var al = l.Ctx.Allocations.AsNoTracking().Where(a => a.EntryId == id).OrderBy(a => a.Id)
            .AsEnumerable().Select(a => new Allocation(a.CategoryId, a.AmountCents)).ToList();
        return new EntryRow(ToEntry(e), al);
    }

    /// <summary>Écart de ventilation d'une écriture : trésorerie réelle − (produits − charges ventilés).
    /// Zéro = l'écriture est complète et cohérente.</summary>
    public static long Imbalance(Entry e, IEnumerable<Allocation> allocs, IReadOnlyDictionary<long, Category> cats)
    {
        long v = 0;
        foreach (var a in allocs)
        {
            if (!cats.TryGetValue(a.CategoryId, out var c)) throw new ComptaException("Catégorie inconnue dans la ventilation.");
            v += c.Kind == CategoryKind.Product ? a.AmountCents : -a.AmountCents;
        }
        return e.NetCash - v;
    }

    /// <summary>Une écriture d'argent (hors virement interne et opération sans mouvement) qui n'a encore aucune
    /// ventilation : typiquement une ligne importée du relevé bancaire, à classer en produit ou en charge.</summary>
    public static bool NeedsAllocation(EntryRow r) =>
        r.Entry.Kind is EntryKind.Normal or EntryKind.MemberRefund && r.Allocations.Count == 0;

    /// <summary>Ajoute (Id = 0) ou modifie une écriture avec sa ventilation.
    /// <paramref name="strict"/> : refuse une écriture dont la ventilation ne correspond pas au montant
    /// ou dont la date sort de l'exercice (saisie normale) ; l'import l'autorise pour pouvoir signaler.</summary>
    public long SaveEntry(Entry entry, IReadOnlyList<Allocation> allocations, bool strict = true, string? importRef = null)
    {
        RequireOpen(entry.YearId);
        var year = Year(entry.YearId)!;

        if (string.IsNullOrWhiteSpace(entry.Label)) throw new ComptaException("Le libellé est obligatoire.");
        if (entry.AmountCents <= 0) throw new ComptaException("Le montant doit être supérieur à zéro.");
        if (entry.Kind == EntryKind.NoCash)
        {
            if (entry.AccountId is not null || entry.ToAccountId is not null)
                throw new ComptaException("Une opération sans mouvement d'argent n'a pas de compte.");
        }
        else if (entry.AccountId is null || Account(entry.AccountId.Value) is null)
            throw new ComptaException("Compte introuvable.");
        if (entry.Kind == EntryKind.Transfer)
        {
            if (entry.ToAccountId is null) throw new ComptaException("Choisissez le compte de destination du virement.");
            if (entry.ToAccountId == entry.AccountId) throw new ComptaException("Le compte de départ et le compte d'arrivée doivent être différents.");
            if (Account(entry.ToAccountId.Value) is null) throw new ComptaException("Compte de destination introuvable.");
            if (entry.Direction != Direction.Out) throw new ComptaException("Un virement part toujours du compte de départ.");
        }
        else if (entry.ToAccountId is not null) throw new ComptaException("Compte de destination réservé aux virements.");
        if (entry.Kind == EntryKind.MemberRefund)
        {
            if (string.IsNullOrWhiteSpace(entry.MemberName)) throw new ComptaException("Indiquez le membre remboursé.");
            if (entry.Direction != Direction.Out) throw new ComptaException("Un remboursement à un membre est une sortie d'argent.");
        }
        if (strict && !year.Contains(entry.Date))
            throw new ComptaException($"La date {entry.Date:dd/MM/yyyy} est hors de l'exercice {year.Label} ({year.Start:dd/MM/yyyy} - {year.End:dd/MM/yyyy}).");

        if (entry.RefundOfId is { } origId)
        {
            if (entry.Kind != EntryKind.Normal || entry.Direction != Direction.In)
                throw new ComptaException("Seul un remboursement reçu (entrée d'argent) peut être lié à une dépense.");
            if (origId == entry.Id) throw new ComptaException("Un remboursement ne peut pas se rembourser lui-même.");
            var orig = EntryById(origId) ?? throw new ComptaException("Dépense d'origine introuvable.");
            if (orig.Entry.Direction != Direction.Out || orig.Entry.Kind is not (EntryKind.Normal or EntryKind.MemberRefund))
                throw new ComptaException("L'écriture choisie n'est pas une dépense.");
            var already = RefundedAmount(origId, entry.Id);
            if (already + entry.AmountCents > orig.Entry.AmountCents)
                throw new ComptaException($"Le remboursement dépasse la dépense : {Money.Format(orig.Entry.AmountCents)} € dépensés, " +
                                          $"{Money.Format(already)} € déjà remboursés, il reste {Money.Format(orig.Entry.AmountCents - already)} € à rembourser.");
        }

        var cats = Categories(entry.YearId).ToDictionary(c => c.Id);
        foreach (var a in allocations)
        {
            if (!cats.ContainsKey(a.CategoryId)) throw new ComptaException("Une catégorie de la ventilation n'appartient pas à cet exercice.");
            if (a.AmountCents == 0) throw new ComptaException("Un montant de ventilation est à zéro.");
        }
        if (strict)
        {
            var gap = Imbalance(entry, allocations, cats);
            if (gap != 0)
                throw new ComptaException($"La ventilation ne correspond pas au montant : il reste {Money.Format(gap)} € à ventiler.");
        }

        using var tx = Begin();
        var ctx = Open().Ctx;
        EntryEntity e;
        var dir = entry.Kind == EntryKind.NoCash || entry.Direction == Direction.In ? "IN" : "OUT";
        var member = entry.Kind == EntryKind.MemberRefund ? entry.MemberName!.Trim() : null;
        if (entry.Id == 0)
        {
            e = new EntryEntity { YearId = entry.YearId, ImportRef = importRef, CreatedAt = Now() };
            ctx.Entries.Add(e);
        }
        else
        {
            e = ctx.Entries.FirstOrDefault(x => x.Id == entry.Id) ?? throw new ComptaException("Écriture introuvable.");
            if (e.YearId != entry.YearId) throw new ComptaException("Une écriture ne peut pas changer d'exercice.");
            ctx.Allocations.RemoveRange(ctx.Allocations.Where(a => a.EntryId == e.Id));
        }
        e.EntryDate = D(entry.Date);
        e.Label = entry.Label.Trim();
        e.Pointed = entry.Pointed;
        e.Kind = EntryKindToDb(entry.Kind);
        e.AccountId = entry.AccountId;
        e.Direction = dir;
        e.ToAccountId = entry.ToAccountId;
        e.AmountCents = entry.AmountCents;
        e.MemberName = member;
        e.Note = entry.Note;
        e.RefundOfId = entry.Kind == EntryKind.Normal && entry.Direction == Direction.In ? entry.RefundOfId : null;
        e.UpdatedAt = Now();
        ctx.SaveChanges();
        foreach (var a in allocations)
            ctx.Allocations.Add(new AllocationEntity { EntryId = e.Id, CategoryId = a.CategoryId, AmountCents = a.AmountCents });
        var id = e.Id;
        Save(ctx);
        tx.Commit();
        return id;
    }

    public void DeleteEntry(long entryId)
    {
        var row = EntryById(entryId) ?? throw new ComptaException("Écriture introuvable.");
        RequireOpen(row.Entry.YearId);
        if (RefundedAmount(entryId) > 0)
            throw new ComptaException("Cette dépense a des remboursements liés : modifiez ou supprimez d'abord les remboursements.");
        using var l = Open();
        var ctx = l.Ctx;
        ctx.Allocations.RemoveRange(ctx.Allocations.Where(a => a.EntryId == entryId));
        ctx.Entries.Remove(ctx.Entries.First(e => e.Id == entryId));
        Save(ctx);
    }

    /// <summary>Réinitialise la ventilation d'une écriture (toutes ses lignes sont retirées) : elle redevient « à ventiler ».
    /// Pour repartir d'une ventilation erronée sans toucher au montant, à la date ni au libellé.</summary>
    public void ClearAllocations(long entryId)
    {
        var row = EntryById(entryId) ?? throw new ComptaException("Écriture introuvable.");
        RequireOpen(row.Entry.YearId);
        if (row.Entry.Kind == EntryKind.Transfer)
            throw new ComptaException("Un virement entre comptes n'a pas de ventilation.");
        if (row.Allocations.Count == 0) return;
        using var tx = Begin();
        var ctx = Open().Ctx;
        ctx.Allocations.RemoveRange(ctx.Allocations.Where(a => a.EntryId == entryId));
        ctx.Entries.First(e => e.Id == entryId).UpdatedAt = Now();
        Log(ctx, "Ventilation réinitialisée", $"{row.Entry.Date:dd/MM/yyyy} « {row.Entry.Label} » ({row.Allocations.Count} ligne(s) retirée(s))");
        Save(ctx);
        tx.Commit();
    }

    public void SetPointed(long entryId, bool pointed)
    {
        var row = EntryById(entryId) ?? throw new ComptaException("Écriture introuvable.");
        RequireOpen(row.Entry.YearId);
        using var l = Open();
        var e = l.Ctx.Entries.First(x => x.Id == entryId);
        e.Pointed = pointed;
        e.UpdatedAt = Now();
        Save(l.Ctx);
    }

    /// <summary>Affectation rapide : classe la totalité d'une écriture non ventilée dans une seule catégorie
    /// (le montant et le signe sont déduits du sens de l'argent). Pour une ventilation sur plusieurs catégories,
    /// modifier l'écriture.</summary>
    public void AssignCategory(long entryId, long categoryId)
    {
        var row = EntryById(entryId) ?? throw new ComptaException("Écriture introuvable.");
        var e = row.Entry;
        RequireOpen(e.YearId);
        if (row.Allocations.Count > 0)
            throw new ComptaException("Cette écriture est déjà ventilée : utilisez « Modifier » pour changer sa ventilation.");
        if (e.Kind is not (EntryKind.Normal or EntryKind.MemberRefund))
            throw new ComptaException("L'affectation rapide ne concerne que les entrées et sorties d'argent ordinaires.");
        var cat = Categories(e.YearId).FirstOrDefault(c => c.Id == categoryId)
                  ?? throw new ComptaException("Cette catégorie n'appartient pas à l'exercice de l'écriture.");
        // produits − charges = trésorerie : produit => +net ; charge => −net.
        var amount = cat.Kind == CategoryKind.Product ? e.NetCash : -e.NetCash;
        using var l = Open();
        l.Ctx.Allocations.Add(new AllocationEntity { EntryId = entryId, CategoryId = categoryId, AmountCents = amount });
        var ent = l.Ctx.Entries.First(x => x.Id == entryId);
        ent.UpdatedAt = Now();
        Save(l.Ctx);
    }

    // ------------------------------------------------------------------ régularisations entre exercices

    public List<Adjustment> Adjustments(long yearId)
    {
        using var l = Open();
        return l.Ctx.Adjustments.AsNoTracking().Where(a => a.YearId == yearId).OrderBy(a => a.Id)
            .AsEnumerable().Select(ToAdjustment).ToList();
    }

    private AdjustmentEntity AdjustmentEntityById(AppDbContext ctx, long id) =>
        ctx.Adjustments.FirstOrDefault(a => a.Id == id) ?? throw new ComptaException("Régularisation introuvable.");

    private void CheckAdjustment(AppDbContext ctx, long yearId, CategoryKind kind, long? categoryId, string label, long cents)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new ComptaException("L'intitulé est obligatoire.");
        if (cents <= 0) throw new ComptaException("Le montant doit être supérieur à zéro.");
        if (categoryId is { } cid)
        {
            var c = CategoryEntityById(ctx, cid);
            if (c.YearId != yearId) throw new ComptaException("Cette catégorie appartient à un autre exercice.");
            if (CatKindFromDb(c.Kind) != kind) throw new ComptaException("La catégorie ne correspond pas au type (produit / charge).");
        }
    }

    /// <summary>Ajoute une régularisation saisie à la main. <paramref name="targetName"/> : nom de la catégorie de
    /// l'exercice suivant qui recevra le report (types « pour l'exercice suivant » et « à régler »).</summary>
    public long AddAdjustment(long yearId, AdjustmentType type, CategoryKind kind, long? categoryId, string label,
        long cents, string? targetName = null, string? note = null)
    {
        RequireOpen(yearId);
        long id;
        using (var tx = Begin())
        {
            var ctx = Open().Ctx;
            CheckAdjustment(ctx, yearId, kind, categoryId, label, cents);
            if (type is AdjustmentType.FromPrev or AdjustmentType.ForPast) targetName = null;
            var e = new AdjustmentEntity
            {
                YearId = yearId, Type = AdjToDb(type), Kind = CatKindToDb(kind), CategoryId = categoryId,
                Label = label.Trim(), AmountCents = cents, TargetName = NullIfBlank(targetName), Note = note
            };
            ctx.Adjustments.Add(e);
            Save(ctx);
            id = e.Id;
            tx.Commit();
        }
        SyncCarry(yearId);
        return id;
    }

    /// <summary>Modifie une régularisation. Une ligne reportée automatiquement ne peut changer que de catégorie.</summary>
    public void UpdateAdjustment(long id, long? categoryId, string label, long cents, string? targetName, string? note)
    {
        long yearId;
        using (var tx = Begin())
        {
            var ctx = Open().Ctx;
            var a = AdjustmentEntityById(ctx, id);
            yearId = a.YearId;
            RequireOpen(a.YearId);
            if (a.SourceId is not null)
            {
                // Ligne issue du report automatique : seule la catégorie d'affectation est libre.
                CheckAdjustment(ctx, a.YearId, CatKindFromDb(a.Kind), categoryId, a.Label, a.AmountCents);
                a.CategoryId = categoryId;
                a.Note = note;
            }
            else
            {
                CheckAdjustment(ctx, a.YearId, CatKindFromDb(a.Kind), categoryId, label, cents);
                var type = AdjFromDb(a.Type);
                a.CategoryId = categoryId;
                a.Label = label.Trim();
                a.AmountCents = cents;
                a.TargetName = type is AdjustmentType.FromPrev or AdjustmentType.ForPast ? null : NullIfBlank(targetName);
                a.Note = note;
            }
            Save(ctx);
            tx.Commit();
        }
        SyncCarry(yearId);
    }

    public void DeleteAdjustment(long id)
    {
        long yearId;
        using (var tx = Begin())
        {
            var ctx = Open().Ctx;
            var a = AdjustmentEntityById(ctx, id);
            yearId = a.YearId;
            RequireOpen(a.YearId);
            if (a.SourceId is not null)
                throw new ComptaException("Cette ligne vient du report automatique de l'exercice précédent : modifiez-la à la source.");
            // Les copies reportées sur l'exercice suivant disparaissent avec leur source.
            ctx.Adjustments.RemoveRange(ctx.Adjustments.Where(x => x.SourceId == id));
            ctx.SaveChanges();
            ctx.Adjustments.Remove(a);
            Save(ctx);
            tx.Commit();
        }
        SyncCarry(yearId);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Reporte automatiquement sur l'exercice suivant (s'il existe et n'est pas validé) les régularisations
    /// « pour l'exercice suivant » et « à régler » de l'exercice donné. Idempotent : peut être rappelé à volonté.</summary>
    public void SyncCarry(long fromYearId)
    {
        if (_factory.ReadOnly) return;
        var from = Year(fromYearId);
        if (from is null) return;
        var next = NextYear(from);
        if (next is null || next.Validated) return;

        using var tx = Begin();
        var ctx = Open().Ctx;
        var src = ctx.Adjustments.AsNoTracking()
            .Where(a => a.YearId == from.Id && (a.Type == "TO_NEXT" || a.Type == "DUE")).OrderBy(a => a.Id).ToList();
        var srcIds = src.Select(s => s.Id).ToHashSet();
        var nextAdj = ctx.Adjustments.Where(a => a.YearId == next.Id && a.SourceId != null).ToList();
        var carriedBySource = nextAdj.ToDictionary(a => a.SourceId!.Value);
        var nextCats = ctx.Categories.AsNoTracking().Where(c => c.YearId == next.Id).ToList();
        var fromAdjIds = ctx.Adjustments.AsNoTracking().Where(a => a.YearId == from.Id).Select(a => a.Id).ToHashSet();

        var changed = false;
        // Lignes reportées dont la source n'existe plus (ou n'est plus de type reportable).
        foreach (var c in nextAdj)
        {
            var srcId = c.SourceId!.Value;
            if (fromAdjIds.Contains(srcId) && !srcIds.Contains(srcId))
            {
                ctx.Adjustments.Remove(c);
                carriedBySource.Remove(srcId);
                changed = true;
            }
        }
        if (changed) { ctx.SaveChanges(); }

        foreach (var s in src)
        {
            var newType = s.Type == "TO_NEXT" ? "FROM_PREV" : "FOR_PAST";
            var matched = MatchCategory(nextCats, s.Kind, s.TargetName);
            if (carriedBySource.TryGetValue(s.Id, out var existing))
            {
                // L'affectation choisie à la main est conservée ; elle n'est recalculée que si la cible indiquée
                // à la source a changé depuis le dernier report (la cible utilisée est mémorisée dans TargetName).
                var snapshot = s.TargetName ?? string.Empty;
                var targetChanged = !string.Equals(existing.TargetName ?? string.Empty, snapshot,
                    StringComparison.CurrentCultureIgnoreCase);
                var cat = targetChanged || existing.CategoryId is null ? matched?.Id ?? existing.CategoryId : existing.CategoryId;
                if (existing.Type != newType || existing.Kind != s.Kind || existing.Label != s.Label
                    || existing.AmountCents != s.AmountCents || existing.CategoryId != cat || existing.TargetName != snapshot)
                {
                    existing.Type = newType;
                    existing.Kind = s.Kind;
                    existing.Label = s.Label;
                    existing.AmountCents = s.AmountCents;
                    existing.CategoryId = cat;
                    existing.TargetName = snapshot;
                    changed = true;
                }
            }
            else
            {
                ctx.Adjustments.Add(new AdjustmentEntity
                {
                    YearId = next.Id, Type = newType, Kind = s.Kind, CategoryId = matched?.Id, Label = s.Label,
                    AmountCents = s.AmountCents, TargetName = s.TargetName ?? string.Empty, SourceId = s.Id
                });
                changed = true;
            }
        }
        if (changed) Save(ctx);
        tx.Commit();
    }

    private static CategoryEntity? MatchCategory(IEnumerable<CategoryEntity> cats, string kind, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : cats.FirstOrDefault(c => c.Kind == kind && SameName(c.Name, name));

    /// <summary>Resynchronise les reports de tous les exercices (appelé à l'ouverture d'une base en écriture).</summary>
    public void SyncAllCarries()
    {
        if (_factory.ReadOnly) return;
        foreach (var y in Years()) SyncCarry(y.Id);
    }
}
