using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace ComptaAepby.Core.Services;

/// <summary>Moteur de base de données géré par l'application (même liste que FiltresApp).</summary>
public enum DatabaseProvider
{
    /// <summary>Fichier local ou réseau (mode d'origine).</summary>
    Sqlite = 0,
    PostgreSql = 1,
    MariaDb = 2,
    SqlServer = 3
}

public static class DatabaseProviderInfo
{
    public static string DisplayName(DatabaseProvider p) => p switch
    {
        DatabaseProvider.Sqlite => "SQLite (fichier)",
        DatabaseProvider.PostgreSql => "PostgreSQL",
        DatabaseProvider.MariaDb => "MariaDB / MySQL",
        DatabaseProvider.SqlServer => "SQL Server",
        _ => p.ToString()
    };

    public static int DefaultPort(DatabaseProvider p) => p switch
    {
        DatabaseProvider.PostgreSql => 5432,
        DatabaseProvider.MariaDb => 3306,
        DatabaseProvider.SqlServer => 1433,
        _ => 0
    };

    /// <summary>Moteur conseillé pour cet usage.</summary>
    public static bool IsRecommended(DatabaseProvider p) => p == DatabaseProvider.PostgreSql;

    public static bool IsServer(DatabaseProvider p) => p != DatabaseProvider.Sqlite;
}

/// <summary>Paramètres de connexion à un serveur de base de données (enregistrés dans le fichier de réglages).
/// Le mot de passe est chiffré pour l'utilisateur Windows courant (DPAPI) et n'est jamais écrit en clair.</summary>
public class DbConnectionInfo
{
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.PostgreSql;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "compta_aepby";
    public string UserName { get; set; } = "";

    /// <summary>Connexion chiffrée (SSL/TLS) vers le serveur.</summary>
    public bool UseSsl { get; set; }

    /// <summary>Délai de connexion en secondes (réduit pour le test de connexion).</summary>
    [JsonIgnore]
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>Mot de passe chiffré (DPAPI, base64) tel qu'enregistré dans les réglages.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>Mot de passe en clair, en mémoire uniquement.</summary>
    [JsonIgnore]
    public string Password
    {
        get => Unprotect(ProtectedPassword);
        set => ProtectedPassword = string.IsNullOrEmpty(value) ? null : Protect(value);
    }

    public DbConnectionInfo Clone() => new()
    {
        Provider = Provider, Host = Host, Port = Port, Database = Database,
        UserName = UserName, UseSsl = UseSsl, ProtectedPassword = ProtectedPassword, ConnectTimeoutSeconds = ConnectTimeoutSeconds
    };

    /// <summary>Résumé sans secret, pour l'affichage.</summary>
    public string Describe() => $"{DatabaseProviderInfo.DisplayName(Provider)} - {Host}:{Port}/{Database}";

    /// <summary>Chaîne de connexion ADO.NET du moteur choisi. <paramref name="databaseOverride"/> permet de
    /// se connecter à une autre base (ex. « postgres » / « master » pour créer la base).</summary>
    public string BuildConnectionString(string? databaseOverride = null)
    {
        var db = databaseOverride ?? Database;
        switch (Provider)
        {
            case DatabaseProvider.PostgreSql:
                return new NpgsqlConnectionStringBuilder
                {
                    Host = Host, Port = Port, Database = db, Username = UserName, Password = Password,
                    SslMode = UseSsl ? SslMode.Require : SslMode.Prefer,
                    Timeout = ConnectTimeoutSeconds, CommandTimeout = 60, Pooling = true
                }.ConnectionString;

            case DatabaseProvider.MariaDb:
                return new MySqlConnectionStringBuilder
                {
                    Server = Host, Port = (uint)Port, Database = db, UserID = UserName, Password = Password,
                    SslMode = UseSsl ? MySqlSslMode.Required : MySqlSslMode.Preferred,
                    ConnectionTimeout = (uint)ConnectTimeoutSeconds, DefaultCommandTimeout = 60, AllowUserVariables = true
                }.ConnectionString;

            case DatabaseProvider.SqlServer:
                return new SqlConnectionStringBuilder
                {
                    DataSource = Port == 1433 || Port <= 0 ? Host : $"{Host},{Port}",
                    InitialCatalog = db, UserID = UserName, Password = Password,
                    Encrypt = UseSsl ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
                    TrustServerCertificate = true, ConnectTimeout = ConnectTimeoutSeconds, CommandTimeout = 60
                }.ConnectionString;

            default:
                throw new InvalidOperationException("Ce moteur n'utilise pas de serveur.");
        }
    }

    private static string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return ""; // réglage copié depuis un autre poste / une autre session : à ressaisir
        }
    }
}

/// <summary>Où se trouve la base à ouvrir : un fichier SQLite ou un serveur.</summary>
public sealed record DbTarget(DatabaseProvider Provider, string? FilePath, DbConnectionInfo? Server)
{
    public static DbTarget ForFile(string path) => new(DatabaseProvider.Sqlite, path, null);
    public static DbTarget ForServer(DbConnectionInfo info) => new(info.Provider, null, info);

    public bool IsServer => Provider != DatabaseProvider.Sqlite;

    public string Describe() => IsServer ? Server!.Describe() : FilePath ?? "";
}
