using ComptaAepby.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ComptaAepby.Core.Data.Migrations;

/// <summary>Étapes de mise à jour du schéma, appliquées par <see cref="DbContextFactory.EnsureDatabaseUpToDate"/>.
/// Même système que FiltresApp : la version atteinte est enregistrée dans la base (SQLite : <c>PRAGMA user_version</c> ;
/// serveurs : table <c>DbInfo</c>) et, au lancement, le poste rédacteur applique uniquement les migrations manquantes
/// après une sauvegarde.</summary>
internal static class DatabaseMigrations
{
    /// <summary>Migrations du schéma, dans l'ordre. Règles : ne jamais modifier ni renuméroter une migration publiée,
    /// toujours en ajouter une nouvelle à la fin, écrire les changements de données en SQL brut (le modèle EF évolue,
    /// pas la migration) et, pour chaque nouvelle migration, ajouter l'équivalent serveur dans
    /// <see cref="ServerMigrations"/>.</summary>
    internal static readonly (int Version, string Description, Action<AppDbContext> Apply)[] All =
    {
        (1, "Schéma initial (exercices, comptes, écritures, ventilations, reports, journal)", _ => { }),
        (2, "Comptes utilisateurs : mot de passe et rôles (administrateur, trésorier, lecteur)", AddUsers),
    };

    /// <summary>Dernière version de <see cref="All"/> déjà contenue dans le schéma créé directement par EF Core sur un
    /// serveur : les bases serveur naissent au dernier schéma et n'ont jamais besoin des migrations SQLite.</summary>
    internal const int ServerBaselineVersion = 2;

    /// <summary>Mises à jour du schéma des bases serveur au-delà de <see cref="ServerBaselineVersion"/>. RÈGLE : toute
    /// nouvelle migration ajoutée à <see cref="All"/> doit avoir ici une entrée de même numéro (corps vide si sans effet
    /// sur le schéma), écrite en code portable (LINQ / <see cref="ServerSql"/>). Sinon le démarrage sur serveur échoue
    /// explicitement plutôt que de laisser le schéma diverger.</summary>
    internal static readonly (int Version, string Description, Action<AppDbContext> Apply)[] ServerMigrations =
    {
    };

    /// <summary>Migrations serveur à appliquer depuis <paramref name="fromVersion"/> ; lève si l'une manque.</summary>
    internal static IReadOnlyList<(int Version, string Description, Action<AppDbContext> Apply)> ServerPlan(int fromVersion)
    {
        var plan = new List<(int, string, Action<AppDbContext>)>();
        foreach (var m in All.Where(m => m.Version > Math.Max(fromVersion, ServerBaselineVersion)))
        {
            var server = ServerMigrations.FirstOrDefault(s => s.Version == m.Version);
            if (server.Apply is null)
                throw new InvalidOperationException(
                    $"La mise à jour v{m.Version} de la base n'a pas d'équivalent pour les serveurs : ce logiciel ne peut pas mettre à jour cette base serveur.");
            plan.Add(server);
        }
        return plan;
    }

    // ------------------------------------------------------------------ v2 : comptes utilisateurs (SQLite)

    private static void AddUsers(AppDbContext ctx)
    {
        ctx.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "users" (
                "id" INTEGER NOT NULL CONSTRAINT "PK_users" PRIMARY KEY AUTOINCREMENT,
                "user_name" TEXT NOT NULL,
                "display_name" TEXT NOT NULL,
                "password_hash" TEXT NOT NULL,
                "role" TEXT NOT NULL,
                "active" INTEGER NOT NULL,
                "must_change_password" INTEGER NOT NULL,
                "created_at" TEXT NOT NULL,
                "created_by" TEXT NULL,
                "last_login_at" TEXT NULL
            );
            """);
        ctx.Database.ExecuteSqlRaw("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_user_name" ON "users" ("user_name");""");
    }
}
