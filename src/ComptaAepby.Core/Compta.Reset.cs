using ComptaAepby.Core.Data;
using ComptaAepby.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ComptaAepby.Core;

public sealed partial class Compta
{
    /// <summary>Vide toutes les données de la base (exercices, comptes bancaires, écritures, ventilations, justificatifs,
    /// reports) en conservant les <b>comptes utilisateurs</b> et le journal d'audit, où l'opération est consignée.
    /// Réservé aux administrateurs. Tout ou rien (une seule transaction). Les exercices validés sont supprimés eux
    /// aussi : c'est une remise à zéro volontaire, à faire précéder d'une sauvegarde par l'appelant.</summary>
    public void ResetDatabaseData()
    {
        Require(Permission.ManageDatabase);
        RequireWritable();

        using var tx = Begin();
        var ctx = Open().Ctx;
        var p = ctx.Provider;
        string Q(string n) => ServerSql.Q(p, n);

        // Les exercices validés verrouillent leurs données (déclencheurs SQLite) : on les rouvre le temps du vidage.
        ctx.Database.ExecuteSqlRaw($"UPDATE {Q("fiscal_years")} SET {Q("status")} = 'OPEN' WHERE {Q("status")} = 'VALIDATED'");
        // Références vers la même table, supprimées avant les lignes (RESTRICT).
        ctx.Database.ExecuteSqlRaw($"UPDATE {Q("entries")} SET {Q("refund_of_id")} = NULL WHERE {Q("refund_of_id")} IS NOT NULL");
        ctx.Database.ExecuteSqlRaw($"UPDATE {Q("adjustments")} SET {Q("source_id")} = NULL WHERE {Q("source_id")} IS NOT NULL");

        // Ordre : du plus dépendant au moins dépendant.
        foreach (var table in new[]
                 { "attachments", "allocations", "adjustments", "entries", "year_openings", "categories", "operations",
                   "fiscal_years", "accounts" })
            ctx.Database.ExecuteSqlRaw($"DELETE FROM {Q(table)}");

        Log(ctx, "Base de données vidée", "Toutes les données ont été supprimées ; les comptes utilisateurs sont conservés.");
        Save(ctx);
        tx.Commit();
    }
}
