using ToutCompteFait.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

/// <summary>Dépense pouvant être remboursée (totalement ou en partie), avec ce qui l'a déjà été.</summary>
public sealed record RefundCandidate(Entry Entry, string YearLabel, long Refunded)
{
    public long Remaining => Entry.AmountCents - Refunded;
}

public sealed partial class Compta
{
    /// <summary>Écritures « vivantes » : ni annulées, ni écritures d'annulation (les remboursements annulés ne comptent
    /// plus, une contre-passation n'est jamais une dépense remboursable).</summary>
    private static IQueryable<EntryEntity> Live(AppDbContext ctx) =>
        ctx.Entries.AsNoTracking().Where(e => e.ReversalOfId == null && !ctx.Entries.Any(r => r.ReversalOfId == e.Id));

    /// <summary>Total des remboursements reçus liés à une dépense (hors <paramref name="excludeEntryId"/>).</summary>
    public long RefundedAmount(long entryId, long excludeEntryId = 0)
    {
        using var l = Open();
        return Live(l.Ctx).Where(e => e.RefundOfId == entryId && e.Id != excludeEntryId)
            .Select(e => e.AmountCents).AsEnumerable().Sum();
    }

    /// <summary>Pour les dépenses d'un exercice : total remboursé (celles sans remboursement n'y figurent pas).</summary>
    public Dictionary<long, long> RefundedAmounts(long yearId)
    {
        using var l = Open();
        var ctx = l.Ctx;
        var rows = (from r in Live(ctx)
                    join o in ctx.Entries.AsNoTracking() on r.RefundOfId equals o.Id
                    where o.YearId == yearId
                    select new { o.Id, r.AmountCents }).ToList();
        return rows.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Sum(x => x.AmountCents));
    }

    /// <summary>Pour les remboursements d'un exercice : libellé de la dépense d'origine.</summary>
    public Dictionary<long, string> RefundOrigins(long yearId)
    {
        using var l = Open();
        var ctx = l.Ctx;
        return (from r in Live(ctx)
                join o in ctx.Entries.AsNoTracking() on r.RefundOfId equals o.Id
                where r.YearId == yearId
                select new { r.Id, o.EntryDate, o.Label }).AsEnumerable()
            .ToDictionary(x => x.Id, x => $"{ParseDate(x.EntryDate):dd/MM/yyyy} « {x.Label} »");
    }

    /// <summary>Dépenses (tous exercices) encore remboursables, plus <paramref name="includeEntryId"/> si indiqué
    /// (dépense déjà liée à l'écriture en cours de modification). Les plus récentes d'abord.</summary>
    public List<RefundCandidate> RefundCandidates(long? includeEntryId = null, long excludeRefundId = 0, int max = 400)
    {
        using var l = Open();
        var ctx = l.Ctx;
        var labels = ctx.Years.AsNoTracking().ToDictionary(y => y.Id, y => y.Label);
        var refunded = Live(ctx).Where(e => e.RefundOfId != null && e.Id != excludeRefundId)
            .Select(e => new { Id = e.RefundOfId!.Value, e.AmountCents }).AsEnumerable()
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Sum(x => x.AmountCents));
        var outs = Live(ctx).Where(e => e.Direction == "OUT" && (e.Kind == "NORMAL" || e.Kind == "MEMBER_REFUND"))
            .OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.Id).AsEnumerable()
            .Select(e => new RefundCandidate(ToEntry(e), labels.GetValueOrDefault(e.YearId, ""), refunded.GetValueOrDefault(e.Id)))
            .Where(c => c.Remaining > 0 || c.Entry.Id == includeEntryId)
            .Take(max).ToList();
        return outs;
    }
}
