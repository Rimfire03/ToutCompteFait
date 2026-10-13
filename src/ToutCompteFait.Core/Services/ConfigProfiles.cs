using System.Text.Json;

namespace ToutCompteFait.Core.Services;

/// <summary>Une configuration enregistrée de l'application : la base à ouvrir (fichier SQLite ou serveur), sous un nom.
/// Sert à basculer d'une base à l'autre (production, essais, démonstration…). Le mot de passe du serveur est chiffré
/// pour l'utilisateur Windows courant, comme dans les réglages du poste.</summary>
public sealed class ConfigProfile
{
    public string Name { get; set; } = "";
    public bool UseServer { get; set; }
    public string? FilePath { get; set; }
    public DbConnectionInfo Server { get; set; } = new();

    /// <summary>Description sans secret, pour l'affichage.</summary>
    public string Describe() => UseServer ? Server.Describe() : "SQLite - " + FilePath;
}

/// <summary>Configurations enregistrées (un fichier JSON par configuration, dossier « Configurations » du poste).
/// Réservé au statut Développeur dans l'interface. Les fichiers ne contiennent aucune donnée comptable.</summary>
public static class ConfigProfiles
{
    public const string Extension = ".tcfprofil.json";

    /// <summary>Dossier des configurations de ce poste.</summary>
    public static string Folder => Path.Combine(AppSettings.DataRoot, "Configurations");

    public static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return clean.Length == 0 ? "configuration" : clean;
    }

    private static string PathOf(string name, string? folder = null) => Path.Combine(folder ?? Folder, Sanitize(name) + Extension);

    /// <summary>Toutes les configurations, triées par nom ; les fichiers illisibles sont ignorés.</summary>
    public static List<ConfigProfile> List(string? folder = null)
    {
        var dir = folder ?? Folder;
        var list = new List<ConfigProfile>();
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.EnumerateFiles(dir, "*" + Extension))
        {
            try
            {
                var p = JsonSerializer.Deserialize<ConfigProfile>(File.ReadAllText(file));
                if (p is not null && p.Name.Trim().Length > 0) list.Add(p);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // fichier illisible : ignoré
            }
        }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Enregistre (ou remplace) une configuration.</summary>
    public static void Save(ConfigProfile profile, string? folder = null)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ComptaException("Le nom de la configuration est obligatoire.");
        if (profile.UseServer)
        {
            if (profile.Server.Host.Trim().Length == 0 || profile.Server.Database.Trim().Length == 0)
                throw new ComptaException("Configuration serveur incomplète (serveur ou base manquant).");
        }
        else if (string.IsNullOrWhiteSpace(profile.FilePath))
            throw new ComptaException("Chemin du fichier de base de données manquant.");
        try
        {
            Directory.CreateDirectory(folder ?? Folder);
            profile.Name = profile.Name.Trim();
            File.WriteAllText(PathOf(profile.Name, folder), JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ComptaException("Impossible d'enregistrer la configuration : " + ex.Message);
        }
    }

    public static void Delete(string name, string? folder = null)
    {
        var path = PathOf(name, folder);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ComptaException("Impossible de supprimer la configuration : " + ex.Message);
        }
    }

    /// <summary>Configuration correspondant aux réglages courants du poste (base ouverte), sous le nom donné.</summary>
    public static ConfigProfile FromSettings(AppSettings s, string name) => new()
    {
        Name = name.Trim(),
        UseServer = s.UseDatabaseServer,
        FilePath = s.UseDatabaseServer ? null : s.ResolvedDatabasePath,
        Server = s.DatabaseServer.Clone()
    };

    /// <summary>Applique la configuration aux réglages du poste (à enregistrer puis redémarrer par l'appelant).</summary>
    public static void Apply(ConfigProfile p, AppSettings s)
    {
        s.UseDatabaseServer = p.UseServer;
        if (p.UseServer) s.DatabaseServer = p.Server.Clone();
        else s.DatabasePath = p.FilePath;
    }

    /// <summary>La configuration désigne-t-elle la base actuellement réglée sur ce poste ?</summary>
    public static bool IsCurrent(ConfigProfile p, AppSettings s)
    {
        if (p.UseServer != s.UseDatabaseServer) return false;
        if (p.UseServer)
            return p.Server.Provider == s.DatabaseServer.Provider && p.Server.Host.Equals(s.DatabaseServer.Host, StringComparison.OrdinalIgnoreCase)
                   && p.Server.Port == s.DatabaseServer.Port && p.Server.Database.Equals(s.DatabaseServer.Database, StringComparison.OrdinalIgnoreCase)
                   && p.Server.UserName.Equals(s.DatabaseServer.UserName, StringComparison.OrdinalIgnoreCase);
        return string.Equals(Path.GetFullPath(p.FilePath ?? ""), s.ResolvedDatabasePath, StringComparison.OrdinalIgnoreCase);
    }
}