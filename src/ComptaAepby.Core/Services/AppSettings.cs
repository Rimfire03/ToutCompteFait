using System.Text.Json;

namespace ComptaAepby.Core.Services;

/// <summary>Réglages locaux du poste, dans un petit fichier JSON à côté de l'exécutable (logiciel portable).
/// Les DONNÉES sont dans la base (fichier SQLite ou serveur), jamais dans l'exécutable ni dans ce fichier.</summary>
public sealed class AppSettings
{
    private const string FileName = "ComptaAepby.settings.json";

    /// <summary>Chemin du fichier SQLite (absolu, ou relatif au dossier de l'exécutable). Vide = Donnees\compta.db.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Utiliser un serveur de base de données (réglages dans <see cref="DatabaseServer"/>) au lieu du fichier.</summary>
    public bool UseDatabaseServer { get; set; }

    /// <summary>Paramètres de connexion au serveur (mot de passe chiffré DPAPI).</summary>
    public DbConnectionInfo DatabaseServer { get; set; } = new();

    /// <summary>Libellé du dernier exercice affiché.</summary>
    public string? LastYear { get; set; }

    /// <summary>Dernier nom d'utilisateur saisi à la connexion (confort : jamais le mot de passe).</summary>
    public string? LastUserName { get; set; }

    /// <summary>Vérification des mises à jour (releases GitHub) au démarrage ; canal « Main » (stable) ou « Dev ».</summary>
    public bool AutoUpdateEnabled { get; set; } = true;
    public string UpdateChannel { get; set; } = UpdateChannels.Main;

    // Sauvegarde automatique planifiée (voir BackupSchedule) et nombre de sauvegardes conservées (0 = illimité).
    public bool AutoBackupEnabled { get; set; }
    public string AutoBackupFrequency { get; set; } = BackupSchedule.Daily;
    public string AutoBackupTime { get; set; } = "02:00";
    public int AutoBackupDayOfWeek { get; set; } = (int)DayOfWeek.Monday;
    public int AutoBackupDayOfMonth { get; set; } = 1;
    public int MaxBackupsToKeep { get; set; } = 30;
    public DateTime? AutoBackupLastRun { get; set; }

    /// <summary>Noms des sauvegardes protégées contre la purge automatique.</summary>
    public List<string> ProtectedBackups { get; set; } = new();

    // ------------------------------------------------------------------ emplacements

    public static string AppDirectory => AppContext.BaseDirectory;

    /// <summary>Dossier des sauvegardes : <c>Sauvegardes</c> à côté de l'exécutable (ou du fichier de réglages).</summary>
    public static string BackupDirectoryPath => Path.Combine(DataRoot, "Sauvegardes");

    /// <summary>Dossier de travail du logiciel : celui de l'exécutable s'il est inscriptible, sinon %LOCALAPPDATA%.</summary>
    public static string DataRoot
    {
        get
        {
            if (CanWrite(AppDirectory)) return AppDirectory;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComptaAepby");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string SettingsPath => Path.Combine(DataRoot, FileName);

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Chemin absolu du fichier SQLite.</summary>
    public string ResolvedDatabasePath
    {
        get
        {
            var p = string.IsNullOrWhiteSpace(DatabasePath) ? Path.Combine("Donnees", "compta.db") : DatabasePath;
            return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(DataRoot, p));
        }
    }

    /// <summary>Base à ouvrir : serveur ou fichier SQLite selon les réglages.</summary>
    public DbTarget ResolvedTarget =>
        UseDatabaseServer ? DbTarget.ForServer(DatabaseServer) : DbTarget.ForFile(ResolvedDatabasePath);

    public static AppSettings Load()
    {
        try
        {
            var p = SettingsPath;
            if (File.Exists(p)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p)) ?? new AppSettings();
        }
        catch { /* réglages illisibles : valeurs par défaut */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non bloquant */ }
    }
}
