using System.Text.Json;

namespace ToutCompteFait.Core.Services;

/// <summary>Fichier de configuration du serveur de base de données (« .tcfconfig », JSON), à remettre à un nouvel
/// utilisateur pour qu'il le charge au premier lancement. Ne contient QUE les informations du serveur : aucun réglage
/// du poste, aucune donnée, aucun compte. Le mot de passe du serveur n'y figure que si on le demande (en clair).</summary>
public static class ServerConfigFile
{
    public const string Extension = ".tcfconfig";
    public const string DefaultFileName = "ToutCompteFait" + Extension;
    private const string Marker = "ToutCompteFait.server";

    private sealed class Dto
    {
        public string Format { get; set; } = Marker;
        public int Version { get; set; } = 1;
        public string Provider { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Database { get; set; } = "";
        public string UserName { get; set; } = "";
        public bool UseSsl { get; set; }
        public string? Password { get; set; }
    }

    public static void Write(string path, DbConnectionInfo info, bool includePassword)
    {
        var dto = new Dto
        {
            Provider = info.Provider.ToString(), Host = info.Host, Port = info.Port, Database = info.Database,
            UserName = info.UserName, UseSsl = info.UseSsl,
            Password = includePassword && info.Password.Length > 0 ? info.Password : null
        };
        File.WriteAllText(path, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Lit un fichier de configuration ; <see cref="ComptaException"/> s'il n'est pas valide.</summary>
    public static DbConnectionInfo Read(string path)
    {
        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new ComptaException("Fichier de configuration illisible : " + ex.Message);
        }
        if (dto is null || dto.Format != Marker)
            throw new ComptaException("Ce fichier n'est pas un fichier de configuration Tout Compte Fait.");
        if (!Enum.TryParse<DatabaseProvider>(dto.Provider, out var provider) || !DatabaseProviderInfo.IsServer(provider))
            throw new ComptaException("Moteur de base de données inconnu dans le fichier de configuration.");
        if (dto.Port is < 1 or > 65535 || dto.Host.Trim().Length == 0 || dto.Database.Trim().Length == 0)
            throw new ComptaException("Fichier de configuration incomplet (serveur, port ou base manquant).");

        var info = new DbConnectionInfo
        {
            Provider = provider, Host = dto.Host.Trim(), Port = dto.Port, Database = dto.Database.Trim(),
            UserName = dto.UserName.Trim(), UseSsl = dto.UseSsl
        };
        info.Password = dto.Password ?? "";
        return info;
    }
}
