using ToutCompteFait.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core.Data;

/// <summary>Petit SQL portable pour les tables techniques hors modèle EF (<c>DbInfo</c> : version du schéma,
/// version du logiciel, dernière saisie, verrou d'écriture) sur SQLite, PostgreSQL, MariaDB/MySQL et SQL Server.
/// Les noms sont toujours des constantes du code ; les valeurs passent en paramètres.</summary>
internal static class ServerSql
{
    public const string SchemaVersionKey = "SchemaVersion";

    public static string Q(DatabaseProvider p, string ident) => p switch
    {
        DatabaseProvider.MariaDb => $"`{ident}`",
        DatabaseProvider.SqlServer => $"[{ident}]",
        _ => $"\"{ident}\""
    };

    public static void EnsureDbInfo(AppDbContext ctx)
    {
        var sql = ctx.Provider switch
        {
            DatabaseProvider.PostgreSql => """CREATE TABLE IF NOT EXISTS "DbInfo" ("Key" varchar(100) NOT NULL PRIMARY KEY, "Value" text NOT NULL);""",
            DatabaseProvider.MariaDb => "CREATE TABLE IF NOT EXISTS `DbInfo` (`Key` varchar(100) NOT NULL PRIMARY KEY, `Value` longtext NOT NULL);",
            DatabaseProvider.SqlServer => "IF OBJECT_ID(N'DbInfo', N'U') IS NULL CREATE TABLE [DbInfo] ([Key] nvarchar(100) NOT NULL PRIMARY KEY, [Value] nvarchar(max) NOT NULL);",
            DatabaseProvider.Sqlite => """CREATE TABLE IF NOT EXISTS "DbInfo" ("Key" TEXT NOT NULL PRIMARY KEY, "Value" TEXT NOT NULL);""",
            _ => throw new NotSupportedException()
        };
        ctx.Database.ExecuteSqlRaw(sql);
    }

    public static string? GetInfo(AppDbContext ctx, string key)
    {
        var p = ctx.Provider;
        var sql = $"SELECT {Q(p, "Value")} AS {Q(p, "Value")} FROM {Q(p, "DbInfo")} WHERE {Q(p, "Key")} = {{0}}";
        return ctx.Database.SqlQueryRaw<string>(sql, key).AsEnumerable().FirstOrDefault();
    }

    public static void SetInfo(AppDbContext ctx, string key, string value)
    {
        var p = ctx.Provider;
        var table = Q(p, "DbInfo");
        var updated = ctx.Database.ExecuteSqlRaw(
            $"UPDATE {table} SET {Q(p, "Value")} = {{0}} WHERE {Q(p, "Key")} = {{1}}", value, key);
        if (updated == 0)
            ctx.Database.ExecuteSqlRaw(
                $"INSERT INTO {table} ({Q(p, "Key")}, {Q(p, "Value")}) VALUES ({{0}}, {{1}})", key, value);
    }

    /// <summary>La table existe-t-elle dans le schéma courant ?</summary>
    public static bool TableExists(AppDbContext ctx, string table)
    {
        var sql = ctx.Provider switch
        {
            DatabaseProvider.Sqlite => "SELECT count(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = {0}",
            DatabaseProvider.PostgreSql => """SELECT count(*)::int AS "Value" FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = {0}""",
            DatabaseProvider.MariaDb => "SELECT CAST(count(*) AS SIGNED) AS `Value` FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = {0}",
            DatabaseProvider.SqlServer => "SELECT CAST(count(*) AS int) AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = {0}",
            _ => throw new NotSupportedException()
        };
        return ctx.Database.SqlQueryRaw<int>(sql, table).AsEnumerable().First() > 0;
    }

    /// <summary>La colonne existe-t-elle dans la table (schéma courant) ?</summary>
    public static bool ColumnExists(AppDbContext ctx, string table, string column)
    {
        var sql = ctx.Provider switch
        {
            DatabaseProvider.Sqlite => "SELECT count(*) AS \"Value\" FROM pragma_table_info({0}) WHERE name = {1}",
            DatabaseProvider.PostgreSql => """SELECT count(*)::int AS "Value" FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = {0} AND column_name = {1}""",
            DatabaseProvider.MariaDb => "SELECT CAST(count(*) AS SIGNED) AS `Value` FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = {0} AND column_name = {1}",
            DatabaseProvider.SqlServer => "SELECT CAST(count(*) AS int) AS [Value] FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = {0} AND COLUMN_NAME = {1}",
            _ => throw new NotSupportedException()
        };
        return ctx.Database.SqlQueryRaw<int>(sql, table, column).AsEnumerable().First() > 0;
    }

    /// <summary>La base du serveur contient-elle déjà les tables de l'application ?</summary>
    public static bool HasAppTables(AppDbContext ctx) => TableExists(ctx, "fiscal_years");
}
