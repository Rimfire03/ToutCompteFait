using ComptaAepby.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ComptaAepby.Core.Services;

/// <summary>Résultat d'une copie : lignes par table, côté source et côté destination après copie.</summary>
public sealed record CopyReport(IReadOnlyList<(string Table, int SourceRows, int DestinationRows)> Tables)
{
    public int TotalRows => Tables.Sum(t => t.DestinationRows);
    public bool IsComplete => Tables.All(t => t.SourceRows == t.DestinationRows);
}

/// <summary>Copie toutes les données de l'application d'une base vers une autre, quel que soit le moteur
/// (SQLite → serveur pour la migration, serveur → SQLite pour les sauvegardes et exports). Passe par le modèle EF
/// Core, donc indépendant du SQL de chaque moteur. La destination est modifiée dans une seule transaction :
/// en cas d'erreur, elle reste intacte. La source n'est jamais modifiée.</summary>
public static class DatabaseCopier
{
    private const int BatchSize = 500;

    /// <summary>La destination contient déjà des données et <c>replaceDestination</c> est faux.</summary>
    public sealed class DestinationNotEmptyException(string message) : Exception(message);

    public static CopyReport Copy(DbTarget source, DbTarget destination, string? appVersion, bool replaceDestination,
        IProgress<string>? progress = null)
    {
        var sourceFactory = new DbContextFactory(source, readOnly: true);
        if (sourceFactory.IsNewDatabase())
            throw new InvalidOperationException("La base source est introuvable ou vide.");
        var sourceVersion = sourceFactory.GetDatabaseVersion();
        if (sourceVersion != DbContextFactory.LatestVersion)
            throw new InvalidOperationException(
                $"La base source est en version {sourceVersion}, ce logiciel gère la version {DbContextFactory.LatestVersion}. " +
                "Ouvrez d'abord la base avec cette version du logiciel pour la mettre à jour.");

        progress?.Report("Préparation de la base de destination...");
        var destFactory = new DbContextFactory(destination);
        destFactory.EnsureDatabaseUpToDate(appVersion ?? "copie");

        using var src = sourceFactory.Create();
        using var dst = destFactory.Create();

        var ordered = OrderByDependencies(dst.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.GetTableName() is not null).ToList());

        // Destination déjà remplie ?
        var existing = ordered.Sum(e => Invoke<int>(nameof(CountRows), e, dst));
        var hasAnyRow = existing > 0;
        if (existing > 0 && !replaceDestination)
            throw new DestinationNotEmptyException(
                $"La base de destination contient déjà des données ({existing} lignes). Elle doit être vide pour recevoir la copie.");

        var report = new List<(string, int, int)>();
        using (var tx = dst.Database.BeginTransaction())
        {
            try
            {
                if (hasAnyRow)
                {
                    progress?.Report("Vidage de la base de destination...");
                    // Les exercices validés verrouillent leurs données (déclencheurs SQLite) : on les rouvre le temps du vidage ;
                    // ils sont de toute façon remplacés par ceux de la source.
                    dst.Database.ExecuteSqlRaw("UPDATE fiscal_years SET status = 'OPEN' WHERE status = 'VALIDATED'");
                    foreach (var e in Enumerable.Reverse(ordered)) Invoke<int>(nameof(DeleteAll), e, dst);
                }

                s_validatedYears.Clear();
                var done = 0;
                foreach (var e in ordered)
                {
                    var name = e.GetTableName()!;
                    progress?.Report($"Copie de {name} ({++done}/{ordered.Count})...");
                    try
                    {
                        var copied = Invoke<int>(nameof(CopyTable), e, src, dst);
                        report.Add((name, copied, 0));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Échec de la copie de la table « {name} » : {ex.InnermostMessage()}", ex);
                    }
                }
                // Exercices validés : leur statut est remis une fois toutes leurs données copiées (inséré tel quel, il
                // aurait bloqué la copie des écritures de l'exercice).
                if (s_validatedYears.Count > 0)
                {
                    var ids = s_validatedYears.ToList();
                    dst.Set<YearEntity>().Where(y => ids.Contains(y.Id)).ExecuteUpdate(s => s.SetProperty(y => y.Status, "VALIDATED"));
                    s_validatedYears.Clear();
                }
                tx.Commit();
            }
            catch
            {
                s_validatedYears.Clear();
                tx.Rollback();
                throw;
            }
        }

        // Contrôle : comptage réel dans la destination.
        progress?.Report("Vérification des données copiées...");
        dst.ChangeTracker.Clear();
        var final = new List<(string Table, int SourceRows, int DestinationRows)>();
        foreach (var e in ordered)
        {
            var name = e.GetTableName()!;
            final.Add((name, Invoke<int>(nameof(CountRows), e, src), Invoke<int>(nameof(CountRows), e, dst)));
        }
        return new CopyReport(final);
    }

    // ---- Outils génériques (appelés par réflexion, un type d'entité à la fois) ----

    private static T Invoke<T>(string method, IEntityType entity, params object[] args)
    {
        var m = typeof(DatabaseCopier).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(entity.ClrType);
        try
        {
            return (T)m.Invoke(null, args.Append(entity).ToArray())!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static int CountRows<T>(AppDbContext ctx, IEntityType _) where T : class =>
        ctx.Set<T>().AsNoTracking().Count();

    private static int DeleteAll<T>(AppDbContext ctx, IEntityType entity) where T : class
    {
        // Clés étrangères vers la même table (remboursement -> dépense, report -> source) : à vider d'abord, car la
        // suppression d'une ligne encore référencée est refusée (RESTRICT) même au sein d'une seule commande.
        var table = entity.GetTableName()!;
        foreach (var fk in entity.GetForeignKeys().Where(f => f.PrincipalEntityType == entity))
        {
            var col = fk.Properties[0].GetColumnName();
            ctx.Database.ExecuteSqlRaw(
                $"UPDATE {ServerSql.Q(ctx.Provider, table)} SET {ServerSql.Q(ctx.Provider, col)} = NULL");
        }
        return ctx.Set<T>().ExecuteDelete();
    }

    /// <summary>Exercices validés de la source, dont le statut est rétabli en fin de copie (la copie tourne sur un seul
    /// thread).</summary>
    [ThreadStatic] private static List<long>? s_validatedYearsField;
    private static List<long> s_validatedYears => s_validatedYearsField ??= new List<long>();

    private static int CopyTable<T>(AppDbContext src, AppDbContext dst, IEntityType entity) where T : class
    {
        var rows = src.Set<T>().AsNoTracking().ToList();
        if (rows.Count == 0) return 0;
        if (typeof(T) == typeof(YearEntity))
        {
            foreach (var y in rows.Cast<YearEntity>())
            {
                if (y.Status != "VALIDATED") continue;
                s_validatedYears.Add(y.Id);
                y.Status = "OPEN";   // rétabli en fin de copie
            }
        }
        // Écritures : celles qui en référencent une autre (remboursement lié) passent après leur dépense d'origine.
        if (typeof(T) == typeof(ComptaAepby.Core.Data.EntryEntity))
            rows = rows.Cast<ComptaAepby.Core.Data.EntryEntity>().OrderBy(e => e.RefundOfId is null ? 0 : 1).ThenBy(e => e.Id)
                .Cast<T>().ToList();

        var table = entity.GetTableName()!;
        var identity = FindIdentityColumn(entity);
        var sqlServerIdentity = identity is not null && dst.Provider == DatabaseProvider.SqlServer;
        if (sqlServerIdentity) dst.Database.ExecuteSqlRaw($"SET IDENTITY_INSERT [{table}] ON");

        for (var i = 0; i < rows.Count; i += BatchSize)
        {
            dst.Set<T>().AddRange(rows.Skip(i).Take(BatchSize));
            dst.SaveChanges();
            dst.ChangeTracker.Clear();
        }

        if (sqlServerIdentity) dst.Database.ExecuteSqlRaw($"SET IDENTITY_INSERT [{table}] OFF");
        if (identity is not null && dst.Provider == DatabaseProvider.PostgreSql)
        {
            // Les identifiants ont été fournis tels quels : la suite de numérotation repart après le plus grand.
            dst.Database.ExecuteSqlRaw(
                $"""SELECT setval(pg_get_serial_sequence('"{table}"', '{identity}'), (SELECT COALESCE(MAX("{identity}"), 1) FROM "{table}"))""");
        }
        return rows.Count;
    }

    /// <summary>Nom de la colonne clé numérique auto-incrémentée de la table, s'il y en a une.</summary>
    private static string? FindIdentityColumn(IEntityType entity)
    {
        var pk = entity.FindPrimaryKey();
        if (pk is null || pk.Properties.Count != 1) return null;
        var p = pk.Properties[0];
        return p.ValueGenerated == ValueGenerated.OnAdd && p.ClrType == typeof(int) ? p.GetColumnName() : null;
    }

    /// <summary>Tables dans un ordre où chaque table passe après celles qu'elle référence (clés étrangères).</summary>
    internal static List<IEntityType> OrderForCopy(List<IEntityType> entities) => OrderByDependencies(entities);

    private static List<IEntityType> OrderByDependencies(List<IEntityType> entities)
    {
        var result = new List<IEntityType>();
        var remaining = new List<IEntityType>(entities);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(e => e.GetForeignKeys()
                .Select(fk => fk.PrincipalEntityType)
                .Where(p => p != e)
                .All(p => !remaining.Contains(p))).ToList();
            if (ready.Count == 0) ready = new List<IEntityType> { remaining[0] }; // cycle : on avance quand même
            result.AddRange(ready);
            foreach (var r in ready) remaining.Remove(r);
        }
        return result;
    }

}



