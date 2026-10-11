using System.Diagnostics;
using ComptaAepby.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace ComptaAepby.Core.Services;

/// <summary>Résultat de <see cref="ServerWriteLock.TryAcquire"/> : le verrou si ce poste devient le rédacteur,
/// sinon le nom de la session qui le détient.</summary>
public sealed record ServerLockResult(ServerWriteLock? Lock, string? OwnerUser);

/// <summary>
/// Verrou « un seul rédacteur » sur un serveur de base de données, équivalent du fichier .lock du mode SQLite :
/// le premier poste connecté peut modifier les données, tous les suivants sont en lecture seule.
/// Le verrou est une ligne de la table DbInfo (clé « WriteLock ») : <c>jeton|session|poste|pid|date UTC</c>.
/// Il est pris par une écriture conditionnelle (atomique sur les trois moteurs), renouvelé toutes les 15 s par le
/// poste rédacteur et libéré à la fermeture. Si le poste plante, la ligne n'est plus renouvelée : au bout de
/// <see cref="StaleAfter"/> un autre poste peut reprendre la main au prochain lancement.
/// </summary>
public sealed class ServerWriteLock : IDisposable
{
    private const string Key = "WriteLock";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);

    private readonly DbTarget _target;
    private readonly string _token = Guid.NewGuid().ToString("N");
    private string _current;

    private ServerWriteLock(DbTarget target, string current)
    {
        _target = target;
        _current = current;
    }

    private string NewValue() =>
        $"{_token}|{Environment.UserName}|{Environment.MachineName}|{Environment.ProcessId}|{DateTime.UtcNow:O}";

    private static string Table(AppDbContext ctx) => ServerSql.Q(ctx.Provider, "DbInfo");
    private static string Col(AppDbContext ctx, string name) => ServerSql.Q(ctx.Provider, name);

    /// <summary>Prend le verrou si personne ne le détient (ou si le détenteur ne donne plus signe de vie).</summary>
    public static ServerLockResult TryAcquire(DbTarget target)
    {
        var self = new ServerWriteLock(target, "");
        string? owner = null;
        var deadline = DateTime.UtcNow.AddSeconds(8);

        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var ctx = new AppDbContext(target, false);
                ServerSql.EnsureDbInfo(ctx);
                var current = ServerSql.GetInfo(ctx, Key);
                var mine = self.NewValue();

                if (current is null)
                {
                    try
                    {
                        ctx.Database.ExecuteSqlRaw(
                            $"INSERT INTO {Table(ctx)} ({Col(ctx, "Key")}, {Col(ctx, "Value")}) VALUES ({{0}}, {{1}})", Key, mine);
                        self._current = mine;
                        return new ServerLockResult(self, null);
                    }
                    catch
                    {
                        continue; // un autre poste vient de l'insérer : relecture
                    }
                }

                var info = Parse(current);
                owner = info?.User;
                var staleOrGone = info is null || IsStale(info);
                // Redémarrage sur le même poste (changement de réglages...) : l'ancien processus est en train de se
                // fermer, on lui laisse quelques secondes pour libérer le verrou.
                var restarting = info is not null && info.Machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                                 && info.Pid != Environment.ProcessId && IsAlive(info.Pid);
                if (!staleOrGone && restarting && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(500);
                    continue;
                }
                if (!staleOrGone && !(info!.Machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) && !IsAlive(info.Pid)))
                    return new ServerLockResult(null, owner);

                var rows = ctx.Database.ExecuteSqlRaw(
                    $"UPDATE {Table(ctx)} SET {Col(ctx, "Value")} = {{0}} WHERE {Col(ctx, "Key")} = {{1}} AND {Col(ctx, "Value")} = {{2}}",
                    mine, Key, current);
                if (rows == 1)
                {
                    self._current = mine;
                    return new ServerLockResult(self, null);
                }
            }
            catch (Exception ex) when (DbContextFactory.IsMissingDatabase(ex))
            {
                return new ServerLockResult(null, null); // base pas encore créée : le premier poste la crée, sans verrou
            }
        }
        return new ServerLockResult(null, owner);
    }

    /// <summary>Renouvelle le verrou. Retourne false si un autre poste l'a repris entre-temps.</summary>
    public bool Refresh()
    {
        using var ctx = new AppDbContext(_target, false);
        var next = NewValue();
        var rows = ctx.Database.ExecuteSqlRaw(
            $"UPDATE {Table(ctx)} SET {Col(ctx, "Value")} = {{0}} WHERE {Col(ctx, "Key")} = {{1}} AND {Col(ctx, "Value")} = {{2}}",
            next, Key, _current);
        if (rows == 1)
        {
            _current = next;
            return true;
        }

        if (ServerSql.GetInfo(ctx, Key) is not null) return false; // repris par un autre poste

        try
        {
            ctx.Database.ExecuteSqlRaw(
                $"INSERT INTO {Table(ctx)} ({Col(ctx, "Key")}, {Col(ctx, "Value")}) VALUES ({{0}}, {{1}})", Key, next);
            _current = next;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Libère le verrou (sans effet s'il a été repris par un autre poste).</summary>
    public void Dispose()
    {
        try
        {
            using var ctx = new AppDbContext(_target, false);
            ctx.Database.ExecuteSqlRaw(
                $"DELETE FROM {Table(ctx)} WHERE {Col(ctx, "Key")} = {{0}} AND {Col(ctx, "Value")} = {{1}}", Key, _current);
        }
        catch
        {
            // Serveur injoignable à la fermeture : le verrou expirera tout seul.
        }
    }

    /// <summary>Session Windows qui détient actuellement le verrou, ou null.</summary>
    public static string? ReadOwner(DbTarget target)
    {
        try
        {
            using var ctx = new AppDbContext(target, false);
            var current = ServerSql.GetInfo(ctx, Key);
            return current is null ? null : Parse(current)?.User;
        }
        catch
        {
            return null;
        }
    }

    private sealed record LockInfo(string Token, string User, string Machine, int Pid, DateTime Heartbeat);

    private static LockInfo? Parse(string value)
    {
        var p = value.Split('|');
        if (p.Length < 5) return null;
        return int.TryParse(p[3], out var pid) && DateTime.TryParse(p[4], null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? new LockInfo(p[0], p[1], p[2], pid, t.ToUniversalTime())
            : null;
    }

    private static bool IsStale(LockInfo info) => DateTime.UtcNow - info.Heartbeat > StaleAfter;

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }
}

