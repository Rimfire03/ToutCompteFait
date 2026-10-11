using ComptaAepby.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace ComptaAepby.Core.Services;

/// <summary>Date de la dernière saisie enregistrée dans la base (affichée dans Paramètres, « Base de données »).
/// SQLite : chaque table de données porte trois déclencheurs (ajout, modification, suppression) qui inscrivent
/// l'heure locale du poste dans <c>DbInfo</c> (clé <see cref="LastWriteKey"/>) : quel que soit le chemin d'écriture,
/// et pour tous les postes puisque l'information est dans la base partagée. Serveurs : l'heure est inscrite par
/// <see cref="AppDbContext.SaveChanges(bool)"/>. Les tables techniques, le journal et les comptes d'accès sont exclus
/// (une connexion n'est pas une saisie).</summary>
public static class DatabaseWriteTracking
{
    public const string LastWriteKey = "LastWriteAt";

    private const string Stamp = "strftime('%Y-%m-%dT%H:%M:%S','now','localtime')";

    private static readonly string[] Tracked =
        { "accounts", "fiscal_years", "year_openings", "operations", "categories", "entries", "allocations", "adjustments" };

    /// <summary>Crée les déclencheurs manquants (idempotent).</summary>
    public static void EnsureTriggers(AppDbContext ctx)
    {
        ServerSql.EnsureDbInfo(ctx);
        if (ctx.Provider != DatabaseProvider.Sqlite) return;

        foreach (var table in Tracked)
        {
            foreach (var (suffix, action) in new[] { ("ins", "INSERT"), ("upd", "UPDATE"), ("del", "DELETE") })
            {
                ctx.Database.ExecuteSqlRaw($"""
                    CREATE TRIGGER IF NOT EXISTS "trg_lastwrite_{table}_{suffix}" AFTER {action} ON "{table}"
                    BEGIN
                        INSERT OR REPLACE INTO "DbInfo" ("Key", "Value") VALUES ('{LastWriteKey}', {Stamp});
                    END;
                    """);
            }
        }
    }

    /// <summary>Heure locale de la dernière saisie enregistrée, ou null si aucune n'a encore été relevée.</summary>
    public static DateTime? ReadLastWrite(AppDbContext ctx)
    {
        try
        {
            var value = ServerSql.GetInfo(ctx, LastWriteKey);
            return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var at) ? at : null;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException
                                       or System.Data.Common.DbException)
        {
            return null;
        }
    }

    /// <summary>Serveur : inscrit l'heure locale de la saisie qui vient d'être enregistrée. Ne doit jamais faire
    /// échouer l'enregistrement déjà fait.</summary>
    public static void StampServer(AppDbContext ctx)
    {
        try
        {
            ServerSql.SetInfo(ctx, LastWriteKey, DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch
        {
            // suivi informatif seulement
        }
    }
}
