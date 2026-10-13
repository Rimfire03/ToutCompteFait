using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ToutCompteFait.Core.Services;

/// <summary>Une sauvegarde du dossier Sauvegardes (fichier .bak, toujours un fichier SQLite, que la base
/// utilisée soit un fichier SQLite ou un serveur).</summary>
public sealed record BackupEntry(string Path, DateTime Date, long Size, string Kind, string Source,
    int? SchemaVersion, string? AppVersion)
{
    /// <summary>Sauvegarde protégée : jamais supprimée automatiquement (purge du nombre conservé).</summary>
    public bool IsProtected { get; set; }

    /// <summary>Sauvegarde de conservation : la plus récente de son année civile, à garder 10 ans (documents comptables,
    /// art. L123-22 du Code de commerce). Jamais supprimée automatiquement.</summary>
    public bool IsRetentionKeeper { get; set; }

    public string RetentionText => IsRetentionKeeper ? "Conservée (10 ans)" : IsProtected ? "Protégée" : "";

    public string FileName => System.IO.Path.GetFileName(Path);
    public string DateText => Date.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture);
    public string SchemaText => SchemaVersion is { } v ? "v" + v : "?";
    public string AppVersionText => AppVersion ?? "?";

    public string SizeText => Size < 1024 * 1024 ? $"{Size / 1024.0:0.0} Ko" : $"{Size / 1024.0 / 1024.0:0.0} Mo";

    public string AgeText
    {
        get
        {
            var age = DateTime.Now - Date;
            if (age.TotalMinutes < 1) return "à l'instant";
            if (age.TotalHours < 1) return $"il y a {(int)age.TotalMinutes} min";
            if (age.TotalDays < 1) return $"il y a {(int)age.TotalHours} h";
            if (age.TotalDays < 60) return $"il y a {(int)age.TotalDays} j";
            return $"il y a {(int)(age.TotalDays / 30)} mois";
        }
    }
}

/// <summary>Liste, supprime et purge les sauvegardes du dossier <see cref="DbContextFactory.BackupDirectory"/>.</summary>
public static class BackupService
{
    // « compta.db.avant-maj-v2-20261003-022340.bak » ou « serveur.auto-20261009-134222.bak »
    private static readonly Regex NamePattern = new(
        @"^(?:(?<srv>serveur)|(?<file>.+?\.db))\.(?<reason>.+)-(?<ts>\d{8}-\d{6})\.bak$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Nombre de sauvegardes à conserver (0 = illimité). Renseigné au démarrage depuis les réglages.</summary>
    public static int MaxToKeep { get; set; }

    /// <summary>Suspend la purge (le temps de lire une sauvegarde qu'on s'apprête à restaurer).</summary>
    public static bool PruneSuspended { get; set; }

    /// <summary>Noms des fichiers protégés contre la suppression automatique (réglages).</summary>
    public static HashSet<string> ProtectedFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Durée légale de conservation des documents comptables (en années).</summary>
    public const int RetentionYears = 10;

    public static List<BackupEntry> List()
    {
        var dir = DbContextFactory.BackupDirectory;
        if (!Directory.Exists(dir)) return [];
        var list = Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .Select(Read)
            .OrderByDescending(b => b.Date)
            .ToList();
        MarkRetentionKeepers(list, DateTime.Now);
        return list;
    }

    /// <summary>Repère, pour chaque année civile des 10 dernières années et chaque source (fichier / serveur), la
    /// sauvegarde la plus récente : c'est elle qui est conservée quand la purge automatique réduit le nombre de
    /// sauvegardes. La liste doit être triée de la plus récente à la plus ancienne.</summary>
    internal static void MarkRetentionKeepers(IReadOnlyList<BackupEntry> sortedNewestFirst, DateTime now)
    {
        var limit = now.AddYears(-RetentionYears);
        var seen = new HashSet<(int Year, string Source)>();
        foreach (var b in sortedNewestFirst)
        {
            if (b.Date < limit) continue;
            if (seen.Add((b.Date.Year, b.Source))) b.IsRetentionKeeper = true;
        }
    }

    private static BackupEntry Read(string path)
    {
        var info = new FileInfo(path);
        var name = info.Name;
        var date = info.LastWriteTime;
        string kind = "Autre", source = "SQLite (fichier)";

        var m = NamePattern.Match(name);
        if (m.Success)
        {
            source = m.Groups["srv"].Success ? "Serveur BDD" : "SQLite (fichier)";
            kind = KindOf(m.Groups["reason"].Value);
            if (DateTime.TryParseExact(m.Groups["ts"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                date = parsed;
        }

        var (schema, app) = ReadDbInfo(path);
        return new BackupEntry(path, date, info.Length, kind, source, schema, app) { IsProtected = ProtectedFiles.Contains(name) };
    }

    private static string KindOf(string reason)
    {
        if (reason.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "Automatique (planifiée)";
        if (reason.Equals("manuelle", StringComparison.OrdinalIgnoreCase)) return "Manuelle";
        if (reason.StartsWith("avant-version-", StringComparison.OrdinalIgnoreCase)) return "Changement de version du logiciel";
        if (reason.StartsWith("avant-maj-v", StringComparison.OrdinalIgnoreCase)) return "Mise à jour du schéma de la base";
        if (reason.StartsWith("avant-mise-a-jour", StringComparison.OrdinalIgnoreCase)) return "Installation d'une mise à jour";
        if (reason.StartsWith("avant-import", StringComparison.OrdinalIgnoreCase)) return "Avant import";
        if (reason.StartsWith("avant-restauration", StringComparison.OrdinalIgnoreCase)) return "Avant restauration";
        return "Avant opération (" + reason + ")";
    }

    /// <summary>Version du schéma et du logiciel enregistrées dans la sauvegarde (ouverture en lecture seule).</summary>
    private static (int? Schema, string? App) ReadDbInfo(string path)
    {
        try
        {
            using var cn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT \"Value\" FROM \"DbInfo\" WHERE \"Key\" = 'SchemaVersion'";
            int? schema = null;
            string? app = null;
            try
            {
                if (int.TryParse(cmd.ExecuteScalar()?.ToString(), out var s)) schema = s;
            }
            catch (SqliteException) { /* pas de table DbInfo */ }
            if (schema is null)
            {
                cmd.CommandText = "PRAGMA user_version";
                schema = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            cmd.CommandText = "SELECT \"Value\" FROM \"DbInfo\" WHERE \"Key\" = 'AppVersion'";
            try { app = cmd.ExecuteScalar()?.ToString(); }
            catch (SqliteException) { }
            return (schema, app);
        }
        catch
        {
            return (null, null);
        }
    }

    public static void Delete(string path)
    {
        File.Delete(path);
        ProtectedFiles.Remove(System.IO.Path.GetFileName(path));
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Delete(path + suffix);
    }

    /// <summary>Ne garde que les <paramref name="keep"/> sauvegardes les plus récentes (0 = ne rien supprimer). Les
    /// sauvegardes protégées et les sauvegardes de conservation (la plus récente de chaque année civile des 10 dernières
    /// années) ne sont jamais supprimées automatiquement et ne comptent pas dans <paramref name="keep"/>.</summary>
    public static int Prune(int keep)
    {
        if (keep <= 0 || PruneSuspended) return 0;
        var removed = 0;
        foreach (var old in List().Where(b => !b.IsProtected && !b.IsRetentionKeeper).Skip(keep))
        {
            try { Delete(old.Path); removed++; }
            catch { /* fichier ouvert ailleurs : réessayé à la prochaine purge */ }
        }
        return removed;
    }
}


