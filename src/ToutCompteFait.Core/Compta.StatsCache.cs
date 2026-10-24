using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

/// <summary>Cache des résultats calculés pour les exercices validés : plus aucune écriture n'y étant possible, le résultat ne
/// change pas, inutile de relire tout le livre (sur un serveur distant, c'est ce qui coûte). Le résultat est conservé avec une
/// clé (empreinte de validation de l'exercice, version du calcul, paramétrage modifiable après clôture) : si l'une de ces
/// données change, il est recalculé. Un cache illisible ou absent est ignoré : on recalcule, sans erreur.</summary>
public sealed partial class Compta
{
    /// <summary>À augmenter quand la façon de calculer un résultat conservé change : les anciens résultats sont alors ignorés.</summary>
    private const int StatsCacheVersion = 1;

    /// <summary>Empreinte de validation de l'exercice, ou null s'il n'est pas validé (rien à conserver).</summary>
    private string? ValidatedSeal(long yearId)
    {
        using var l = Open();
        return l.Ctx.Years.AsNoTracking().Where(y => y.Id == yearId && y.Status == "VALIDATED").Select(y => y.Checksum).FirstOrDefault();
    }

    private static string StatsKey(params string?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StatsCacheVersion + "\u001f" + string.Join("\u001f", parts))));

    private T? ReadStatsCache<T>(long yearId, string kind, string key) where T : class
    {
        try
        {
            using var l = Open();
            var row = l.Ctx.StatsCache.AsNoTracking().FirstOrDefault(c => c.YearId == yearId && c.Kind == kind);
            return row is not null && row.CacheKey == key ? JsonSerializer.Deserialize<T>(row.Payload) : null;
        }
        catch
        {
            return null;   // table absente, JSON d'une ancienne version, base indisponible : on recalcule
        }
    }

    /// <summary>Conserve un résultat ; silencieux en cas d'échec (poste en lecture seule, base indisponible) : c'est un cache.</summary>
    private void WriteStatsCache(long yearId, string kind, string key, object value)
    {
        if (_factory.ReadOnly) return;
        try
        {
            using var l = Open();
            var ctx = l.Ctx;
            var row = ctx.StatsCache.FirstOrDefault(c => c.YearId == yearId && c.Kind == kind);
            if (row is null) ctx.StatsCache.Add(row = new Data.StatsCacheEntity { YearId = yearId, Kind = kind });
            row.CacheKey = key;
            row.Payload = JsonSerializer.Serialize(value);
            row.ComputedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            Save(ctx);
        }
        catch
        {
            // cache seulement : jamais d'erreur pour l'utilisateur
        }
    }
}