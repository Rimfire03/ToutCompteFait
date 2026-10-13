using System.Security.Cryptography;
using System.Text;
using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

/// <summary>Versions de la formule d'empreinte des exercices validés.
/// <para>RÈGLE D'ÉVOLUTION : une formule publiée est <b>figée</b>. Pour protéger de nouvelles données, on ajoute une
/// formule (<c>ComputeChecksumV3</c>…), on augmente <see cref="Current"/> et on conserve les anciennes : chaque exercice
/// est vérifié avec la version enregistrée dans son empreinte (préfixe <c>v2:</c> ; sans préfixe = v1), donc faire
/// évoluer le logiciel ou la structure de la base ne rend jamais un exercice clos « altéré ». La mise à niveau d'un
/// exercice vers la formule courante (<see cref="Compta.UpgradeIntegritySeals"/>) n'est faite qu'après vérification
/// réussie de sa formule d'origine, et tracée au journal.</para></summary>
public static class IntegritySeal
{
    public const int Current = 3;

    /// <summary>Version de la formule utilisée par une empreinte enregistrée (0 = aucune).</summary>
    public static int VersionOf(string? stored) =>
        string.IsNullOrEmpty(stored) ? 0
        : stored.StartsWith("v3:", StringComparison.Ordinal) ? 3
        : stored.StartsWith("v2:", StringComparison.Ordinal) ? 2 : 1;
}

public sealed partial class Compta
{
    private const char Sep = '\u001f';

    /// <summary>Empreinte (SHA-256) du contenu d'un exercice avec la formule courante, préfixée par sa version :
    /// permet de détecter toute modification faite hors du logiciel après la validation. Couvre, en plus de la
    /// formule v1, le numéro d'ordre, les annulations, les notes, les références d'import, la date de saisie, les
    /// régularisations complètes et le contenu (empreinte SHA-256) de chaque justificatif.</summary>
    public string ComputeChecksum(long yearId) => "v3:" + ComputeChecksumV3(yearId);

    /// <summary>Formule v2. FIGÉE (voir <see cref="IntegritySeal"/>).</summary>
    internal string ComputeChecksumV2(long yearId) => ComputeChecksumCore(yearId, 2);

    /// <summary>Formule v3 : v2 + mode de règlement et référence de pièce de chaque écriture. FIGÉE.</summary>
    internal string ComputeChecksumV3(long yearId) => ComputeChecksumCore(yearId, 3);

    private string ComputeChecksumCore(long yearId, int version)
    {
        var y = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        var sb = new StringBuilder();
        void Line(params object?[] fields) => sb.Append(string.Join(Sep, fields.Select(f => f?.ToString() ?? ""))).Append('\n');

        Line("V" + version, y.Label, D(y.Start), D(y.End));
        foreach (var o in Operations(yearId)) Line("O", o.Id, o.Name);
        foreach (var c in Categories(yearId)) Line("C", c.Id, c.Kind, c.Name, c.OperationId);

        using (var l = Open())
        {
            var ctx = l.Ctx;
            var entries = ctx.Entries.AsNoTracking().Where(e => e.YearId == yearId).OrderBy(e => e.Id).ToList();
            foreach (var e in entries)
            {
                if (version >= 3)
                    Line("E", e.Id, e.Seq, e.EntryDate, e.Label, e.Pointed ? 1 : 0, e.Kind, e.AccountId, e.Direction, e.ToAccountId,
                        e.AmountCents, e.MemberName, e.Note, e.ImportRef, e.RefundOfId, e.ReversalOfId, e.CreatedAt,
                        e.PaymentMode, e.PieceRef);
                else
                    Line("E", e.Id, e.Seq, e.EntryDate, e.Label, e.Pointed ? 1 : 0, e.Kind, e.AccountId, e.Direction, e.ToAccountId,
                        e.AmountCents, e.MemberName, e.Note, e.ImportRef, e.RefundOfId, e.ReversalOfId, e.CreatedAt);
            }
            var allocs = (from a in ctx.Allocations.AsNoTracking()
                          join e in ctx.Entries.AsNoTracking() on a.EntryId equals e.Id
                          where e.YearId == yearId
                          orderby a.EntryId, a.CategoryId, a.AmountCents
                          select new { a.EntryId, a.CategoryId, a.AmountCents }).ToList();
            foreach (var a in allocs) Line("A", a.EntryId, a.CategoryId, a.AmountCents);
        }
        foreach (var a in Adjustments(yearId))
            Line("J", a.Id, a.Type, a.Kind, a.CategoryId, a.Label, a.AmountCents, a.TargetName, a.SourceId, a.Note);
        foreach (var ac in Accounts(true)) Line("B", ac.Id, Opening(yearId, ac.Id));

        // Justificatifs : empreinte du contenu stocké, relue pièce par pièce (jamais tout l'exercice en mémoire).
        List<long> attachmentIds;
        using (var l = Open())
            attachmentIds = (from a in l.Ctx.Attachments.AsNoTracking()
                             join e in l.Ctx.Entries.AsNoTracking() on a.EntryId equals e.Id
                             where e.YearId == yearId
                             orderby a.Id
                             select a.Id).ToList();
        foreach (var id in attachmentIds)
        {
            using var l = Open();
            var a = l.Ctx.Attachments.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.Id, x.EntryId, x.FileName, x.ContentType, x.SizeBytes, x.Compression, x.Data }).First();
            Line("T", a.Id, a.EntryId, a.FileName, a.ContentType, a.SizeBytes, a.Compression,
                Convert.ToHexString(SHA256.HashData(a.Data)));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Empreinte enregistrée d'un exercice (null s'il n'est pas validé).</summary>
    private string? StoredChecksum(long yearId)
    {
        using var l = Open();
        return l.Ctx.Years.AsNoTracking().Where(x => x.Id == yearId).Select(x => x.Checksum).FirstOrDefault();
    }

    /// <summary>True si le contenu d'un exercice validé est toujours conforme à l'empreinte enregistrée, calculée avec
    /// la formule dont la version est inscrite dans cette empreinte.</summary>
    public bool VerifyIntegrity(long yearId)
    {
        var y = Year(yearId);
        if (y is null || !y.Validated) return true;
        var stored = StoredChecksum(yearId);
        return IntegritySeal.VersionOf(stored) switch
        {
            1 => string.Equals(stored, ComputeChecksumV1(yearId), StringComparison.Ordinal),
            2 => string.Equals(stored, "v2:" + ComputeChecksumV2(yearId), StringComparison.Ordinal),
            3 => string.Equals(stored, ComputeChecksum(yearId), StringComparison.Ordinal),
            _ => false
        };
    }

    /// <summary>Version de formule de l'empreinte d'un exercice (0 : non validé).</summary>
    public int IntegritySealVersion(long yearId) => IntegritySeal.VersionOf(StoredChecksum(yearId));

    /// <summary>Met à la formule courante l'empreinte des exercices validés d'une version antérieure, à condition que
    /// leur empreinte d'origine soit encore exacte (un exercice altéré n'est jamais « blanchi »). Tracé au journal.
    /// Retourne le nombre d'exercices mis à niveau ; sans effet en lecture seule ou sans droit de validation.</summary>
    public int UpgradeIntegritySeals()
    {
        if (_factory.ReadOnly || !Session.Can(Permission.ValidateYear)) return 0;
        var upgraded = 0;
        foreach (var y in Years().Where(y => y.Validated))
        {
            var stored = StoredChecksum(y.Id);
            var version = IntegritySeal.VersionOf(stored);
            if (version == 0 || version >= IntegritySeal.Current) continue;
            if (!VerifyIntegrity(y.Id)) continue;   // altéré : l'alerte reste affichée, on ne réécrit rien

            using var tx = Begin();
            var ctx = Open().Ctx;
            var e = ctx.Years.First(x => x.Id == y.Id);
            e.Checksum = ComputeChecksum(y.Id);
            Save(ctx);
            Log(ctx, "Empreinte d'exercice mise à niveau",
                $"{y.Label} : formule v{version} → v{IntegritySeal.Current} (contenu vérifié conforme avant la mise à niveau)");
            tx.Commit();
            upgraded++;
        }
        return upgraded;
    }

    /// <summary>Contrôle du journal d'audit chaîné (voir <see cref="AuditChain"/>).</summary>
    public AuditChainReport VerifyAuditChain()
    {
        using var l = Open();
        return AuditChain.Verify(l.Ctx);
    }
}
