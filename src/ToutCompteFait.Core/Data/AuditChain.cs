using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core.Data;

/// <summary>Résultat du contrôle du journal d'audit chaîné.</summary>
/// <param name="Ok">Aucune anomalie détectée.</param>
/// <param name="Rows">Nombre de lignes du journal.</param>
/// <param name="Unsealed">Lignes antérieures au chaînage, pas encore scellées (sans anomalie en soi).</param>
/// <param name="FirstBadId">Première ligne en défaut (modifiée, insérée ou retirée après coup), si anomalie.</param>
public sealed record AuditChainReport(bool Ok, int Rows, int Unsealed, long? FirstBadId, string? Problem);

/// <summary>Journal d'audit à chaînage de hachages : chaque ligne porte l'empreinte SHA-256 de son contenu et de
/// l'empreinte de la ligne précédente. Toute modification, insertion ou retrait d'une ligne rompt la chaîne, ce que
/// <see cref="Verify"/> détecte ; l'empreinte du dernier maillon est en outre gravée dans chaque exercice validé
/// (<c>closing_json</c>), ce qui révèle aussi la suppression des dernières lignes.
/// <para>ÉVOLUTION : la formule du hachage est figée par <see cref="Algorithm"/> ; en changer exige un nouvel
/// identifiant et la conservation de l'ancienne formule pour les lignes déjà scellées.</para></summary>
public static class AuditChain
{
    internal const string Algorithm = "TCF-AUDIT-v1";

    public static string ComputeHash(string prevHash, string at, string user, string action, string? detail)
    {
        var text = string.Join('\u001f', Algorithm, prevHash, at, user, action, detail ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Dernier maillon de la chaîne (null si le journal est vide ou pas encore scellé).</summary>
    public static string? Head(AppDbContext ctx) =>
        ctx.Audit.AsNoTracking().Where(a => a.Hash != null).OrderByDescending(a => a.Id).Select(a => a.Hash).FirstOrDefault();

    /// <summary>Appelé par <see cref="AppDbContext.SaveChanges(bool)"/> : chaîne les lignes ajoutées sans empreinte.
    /// Les lignes déjà empreintées (copie d'une base vers une autre) sont conservées telles quelles.</summary>
    internal static void Stamp(AppDbContext ctx)
    {
        var added = ctx.ChangeTracker.Entries<AuditEntity>()
            .Where(e => e.State == EntityState.Added && e.Entity.Hash is null).Select(e => e.Entity).ToList();
        if (added.Count == 0) return;

        var last = ctx.Audit.AsNoTracking().OrderByDescending(a => a.Id).Select(a => new { a.Id, a.Hash }).FirstOrDefault();
        if (last is not null && last.Hash is null)
        {
            SealLegacy(ctx);   // base d'avant le chaînage : on scelle l'existant avant d'ajouter la suite
            last = ctx.Audit.AsNoTracking().OrderByDescending(a => a.Id).Select(a => new { a.Id, a.Hash }).FirstOrDefault();
        }
        var prev = last?.Hash ?? "";
        foreach (var a in added)
        {
            a.PrevHash = prev;
            a.Hash = ComputeHash(prev, a.At, a.UserName, a.Action, a.Detail);
            prev = a.Hash;
        }
    }

    /// <summary>Scelle les lignes d'avant le chaînage (hachage absent), dans l'ordre. Seule modification permise sur une
    /// ligne du journal : passer de « sans empreinte » à « empreintée » (déclencheur SQLite et garde EF). Retourne le
    /// nombre de lignes scellées.</summary>
    public static int SealLegacy(AppDbContext ctx)
    {
        var rows = ctx.Audit.AsNoTracking().Where(a => a.Hash == null).OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.At, a.UserName, a.Action, a.Detail }).ToList();
        if (rows.Count == 0) return 0;
        var firstId = rows[0].Id;
        var prev = ctx.Audit.AsNoTracking().Where(a => a.Id < firstId && a.Hash != null).OrderByDescending(a => a.Id)
            .Select(a => a.Hash).FirstOrDefault() ?? "";
        foreach (var r in rows)
        {
            var hash = ComputeHash(prev, r.At, r.UserName, r.Action, r.Detail);
            ctx.Database.ExecuteSqlRaw(
                $"UPDATE {ServerSql.Q(ctx.Provider, "audit_log")} SET {ServerSql.Q(ctx.Provider, "prev_hash")} = {{0}}, " +
                $"{ServerSql.Q(ctx.Provider, "hash")} = {{1}} WHERE {ServerSql.Q(ctx.Provider, "id")} = {{2}}", prev, hash, r.Id);
            prev = hash;
        }
        return rows.Count;
    }

    /// <summary>Contrôle toute la chaîne, puis la présence des empreintes gravées dans les exercices validés.</summary>
    public static AuditChainReport Verify(AppDbContext ctx)
    {
        var rows = ctx.Audit.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.At, a.UserName, a.Action, a.Detail, a.PrevHash, a.Hash }).ToList();
        var prev = "";
        var unsealed = 0;
        var started = false;
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (r.Hash is null)
            {
                if (started)
                    return new AuditChainReport(false, rows.Count, unsealed, r.Id, "Ligne sans empreinte insérée dans la chaîne.");
                unsealed++;
                continue;
            }
            started = true;
            if (!string.Equals(r.PrevHash ?? "", prev, StringComparison.Ordinal))
                return new AuditChainReport(false, rows.Count, unsealed, r.Id, "Chaînage rompu : une ligne a été retirée ou insérée.");
            var expected = ComputeHash(prev, r.At, r.UserName, r.Action, r.Detail);
            if (!string.Equals(r.Hash, expected, StringComparison.Ordinal))
                return new AuditChainReport(false, rows.Count, unsealed, r.Id, "Contenu d'une ligne modifié après coup.");
            prev = r.Hash;
            hashes.Add(r.Hash);
        }

        foreach (var y in ctx.Years.AsNoTracking().Where(y => y.Status == "VALIDATED" && y.ClosingJson != null)
                     .Select(y => new { y.Label, y.ClosingJson }).ToList())
        {
            var head = ReadAuditHead(y.ClosingJson!);
            if (head is not null && !hashes.Contains(head))
                return new AuditChainReport(false, rows.Count, unsealed, null,
                    $"Des lignes du journal antérieures à la validation de l'exercice {y.Label} ont disparu.");
        }
        return new AuditChainReport(true, rows.Count, unsealed, null, null);
    }

    /// <summary>Empreinte du journal enregistrée dans le bilan de clôture d'un exercice (null si absente).</summary>
    internal static string? ReadAuditHead(string closingJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(closingJson);
            return doc.RootElement.TryGetProperty("auditHead", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
