using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

/// <summary>Deux écritures ordinaires qui sont en réalité les deux moitiés d'un même virement entre comptes : une sortie
/// sur un compte et une entrée du même montant sur un autre, à quelques jours d'écart.</summary>
public sealed record TransferPair(Entry Out, Entry In)
{
    public int DaysApart => Math.Abs(Out.Date.DayNumber - In.Date.DayNumber);
}

public sealed partial class Compta
{
    /// <summary>Paires d'écritures d'un exercice à fusionner en un seul virement : une sortie et une entrée de même
    /// montant sur deux comptes différents, à <see cref="BankTransferToleranceDays"/> jours au plus, chaque écriture
    /// n'apparaissant que dans une paire (les plus proches d'abord). Remboursements de membres et remboursements reçus
    /// exclus.</summary>
    public List<TransferPair> FindTransferPairs(long yearId)
    {
        var rows = Entries(yearId).Select(r => r.Entry)
            .Where(e => e.Kind == EntryKind.Normal && e.AccountId is not null && e.RefundOfId is null).ToList();
        var outs = rows.Where(e => e.Direction == Direction.Out).ToList();
        var ins = rows.Where(e => e.Direction == Direction.In).ToList();
        var candidates = (from o in outs
                          from i in ins
                          where o.AccountId != i.AccountId && o.AmountCents == i.AmountCents
                          let days = Math.Abs(o.Date.DayNumber - i.Date.DayNumber)
                          where days <= BankTransferToleranceDays
                          orderby days, o.Id, i.Id
                          select new TransferPair(o, i)).ToList();
        var usedOut = new HashSet<long>();
        var usedIn = new HashSet<long>();
        var result = new List<TransferPair>();
        foreach (var p in candidates)
        {
            if (usedOut.Contains(p.Out.Id) || usedIn.Contains(p.In.Id)) continue;
            usedOut.Add(p.Out.Id);
            usedIn.Add(p.In.Id);
            result.Add(p);
        }
        return result.OrderBy(p => p.Out.Date).ThenBy(p => p.Out.Id).ToList();
    }

    /// <summary>Fusionne deux écritures en un seul virement entre comptes : l'écriture de sortie devient le virement
    /// (du compte débité vers le compte crédité), l'écriture d'entrée est supprimée. Les ventilations des deux écritures
    /// sont retirées (un virement n'a ni produit ni charge) ; les justificatifs sont conservés sur le virement.</summary>
    public void MergeIntoTransfer(long outEntryId, long inEntryId)
    {
        var o = EntryById(outEntryId) ?? throw new ComptaException("Écriture introuvable.");
        var i = EntryById(inEntryId) ?? throw new ComptaException("Écriture introuvable.");
        if (o.Entry.YearId != i.Entry.YearId) throw new ComptaException("Les deux écritures doivent être du même exercice.");
        RequireOpen(o.Entry.YearId);
        if (o.Entry.Kind != EntryKind.Normal || i.Entry.Kind != EntryKind.Normal
            || o.Entry.Direction != Direction.Out || i.Entry.Direction != Direction.In)
            throw new ComptaException("Il faut une sortie d'argent et une entrée d'argent ordinaires.");
        if (o.Entry.AccountId == i.Entry.AccountId || o.Entry.AccountId is null || i.Entry.AccountId is null)
            throw new ComptaException("Les deux écritures doivent concerner deux comptes différents.");
        if (o.Entry.AmountCents != i.Entry.AmountCents)
            throw new ComptaException("Les deux écritures n'ont pas le même montant : ce n'est pas un même virement.");

        using var tx = Begin();
        var ctx = Open().Ctx;
        var outE = ctx.Entries.First(e => e.Id == outEntryId);
        var inE = ctx.Entries.First(e => e.Id == inEntryId);
        outE.Kind = "TRANSFER";
        outE.Direction = "OUT";
        outE.ToAccountId = inE.AccountId;
        outE.MemberName = null;
        outE.Pointed = outE.Pointed || inE.Pointed;
        outE.UpdatedAt = Now();
        ctx.Allocations.RemoveRange(ctx.Allocations.Where(a => a.EntryId == outEntryId || a.EntryId == inEntryId));
        ctx.SaveChanges();
        foreach (var a in ctx.Attachments.Where(x => x.EntryId == inEntryId).ToList()) a.EntryId = outEntryId;
        ctx.SaveChanges();
        ctx.Entries.Remove(inE);
        Log(ctx, "Virement entre comptes fusionné",
            $"{o.Entry.Date:dd/MM/yyyy} « {o.Entry.Label} » : {Money.Format(o.Entry.AmountCents)} € de {Account(o.Entry.AccountId!.Value)?.Name} vers {Account(i.Entry.AccountId!.Value)?.Name}");
        Save(ctx);
        tx.Commit();
    }
}
