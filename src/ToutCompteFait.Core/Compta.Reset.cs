using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

public sealed partial class Compta
{
    /// <summary>Phrase à saisir pour autoriser la suppression d'exercices validés lors d'un vidage de la base.</summary>
    public const string ResetValidatedPhrase = "SUPPRIMER LES EXERCICES VALIDES";

    /// <summary>Vide toutes les données de la base (exercices, comptes bancaires, écritures, ventilations, justificatifs,
    /// reports) en conservant les <b>comptes utilisateurs</b> et le journal d'audit, où l'opération est consignée.
    /// Réservé aux administrateurs. Tout ou rien (une seule transaction).
    /// <para>Exercices validés : leur contenu est opposable (documents comptables à conserver 10 ans). Le vidage est donc
    /// <b>refusé</b> tant que la base en contient, sauf double garde-fou : la phrase
    /// <see cref="ResetValidatedPhrase"/> saisie à l'identique <b>et</b> une archive exportée au préalable
    /// (<see cref="ArchiveValidatedYears"/> : livre CSV + FEC de chaque exercice validé) dans
    /// <paramref name="archiveFolder"/>. L'appelant doit en outre avoir fait une sauvegarde complète de la base.</para></summary>
    public void ResetDatabaseData(string? confirmation = null, string? archiveFolder = null)
    {
        Require(Permission.ManageDatabase);
        RequireWritable();

        var validated = Years().Where(y => y.Validated).ToList();
        if (validated.Count > 0)
        {
            var labels = string.Join(", ", validated.Select(y => y.Label));
            if (!string.Equals(confirmation?.Trim(), ResetValidatedPhrase, StringComparison.OrdinalIgnoreCase))
                throw new ComptaException(
                    $"La base contient {validated.Count} exercice(s) validé(s) ({labels}) : ce sont des documents comptables à conserver. " +
                    $"Pour les supprimer malgré tout, saisissez exactement « {ResetValidatedPhrase} » après les avoir archivés.");
            var missing = ArchiveFilesMissing(archiveFolder, validated);
            if (missing.Count > 0)
                throw new ComptaException(
                    "Archive des exercices validés incomplète : exportez-la d'abord (livre CSV et FEC de chaque exercice). " +
                    "Fichier(s) manquant(s) : " + string.Join(", ", missing.Take(4)) + (missing.Count > 4 ? " …" : ""));
        }

        using var tx = Begin();
        var ctx = Open().Ctx;
        var p = ctx.Provider;
        string Q(string n) => ServerSql.Q(p, n);

        // Les verrous SQLite (exercices validés, écritures non supprimables) sont levés le temps du vidage volontaire,
        // puis recréés ; le journal d'audit, lui, n'est jamais touché.
        SqliteTriggers.Drop(ctx);

        // Les exercices validés verrouillent leurs données : on les rouvre le temps du vidage.
        ctx.Database.ExecuteSqlRaw($"UPDATE {Q("fiscal_years")} SET {Q("status")} = 'OPEN' WHERE {Q("status")} = 'VALIDATED'");
        // Références vers la même table, supprimées avant les lignes (RESTRICT).
        ctx.Database.ExecuteSqlRaw($"UPDATE {Q("entries")} SET {Q("refund_of_id")} = NULL WHERE {Q("refund_of_id")} IS NOT NULL");
        ctx.Database.ExecuteSqlRaw($"UPDATE {Q("entries")} SET {Q("reversal_of_id")} = NULL WHERE {Q("reversal_of_id")} IS NOT NULL");
        ctx.Database.ExecuteSqlRaw($"UPDATE {Q("adjustments")} SET {Q("source_id")} = NULL WHERE {Q("source_id")} IS NOT NULL");

        // Ordre : du plus dépendant au moins dépendant.
        foreach (var table in new[]
                 { "attachments", "allocations", "adjustments", "entries", "year_openings", "categories", "operations", "urssaf_declarations", "stats_cache",
                   "fiscal_years", "accounts" })
            ctx.Database.ExecuteSqlRaw($"DELETE FROM {Q(table)}");

        SqliteTriggers.EnsureYearLocks(ctx);
        Log(ctx, "Base de données vidée",
            "Toutes les données ont été supprimées ; les comptes utilisateurs sont conservés." +
            (validated.Count > 0
                ? $"\nEXERCICES VALIDÉS SUPPRIMÉS : {string.Join(", ", validated.Select(y => y.Label))} — archive : {archiveFolder}"
                : ""));
        Save(ctx);
        tx.Commit();
    }
}
