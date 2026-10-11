using System.Security.Cryptography;
using System.Text;

namespace ToutCompteFait.Core.Security;

/// <summary>Rôle d'un utilisateur. L'ordre croissant correspond à des droits croissants.</summary>
public enum Role { Reader = 0, Treasurer = 1, Admin = 2 }

/// <summary>Droits fins vérifiés par le service comptable (jamais seulement par l'interface).</summary>
public enum Permission
{
    /// <summary>Saisir, modifier, annuler des écritures et des reports ; importer un relevé bancaire.</summary>
    WriteLedger,
    /// <summary>Créer un exercice, gérer produits/charges/opérations/comptes, soldes d'ouverture.</summary>
    ManageStructure,
    /// <summary>Valider (clôturer) un exercice.</summary>
    ValidateYear,
    /// <summary>Rouvrir un exercice validé.</summary>
    ReopenYear,
    /// <summary>Créer et gérer les comptes utilisateurs.</summary>
    ManageUsers,
    /// <summary>Réglages de la base (serveur, fichier, sauvegardes, restauration), vidage de la base.</summary>
    ManageDatabase
}

public static class RoleInfo
{
    public static string DisplayName(Role r) => r switch
    {
        Role.Admin => "Administrateur",
        Role.Treasurer => "Trésorier",
        _ => "Lecteur"
    };

    public static string Describe(Role r) => r switch
    {
        Role.Admin => "Tous les droits, dont la création des comptes utilisateurs et les réglages de la base.",
        Role.Treasurer => "Saisie du livre, reports, structure des exercices, validation. Pas de gestion des utilisateurs ni de la base.",
        _ => "Consultation seule : livre, bilan, reports (aucune modification)."
    };

    internal static string ToDb(Role r) => r switch { Role.Admin => "ADMIN", Role.Treasurer => "TREASURER", _ => "READER" };

    internal static Role FromDb(string s) => s switch { "ADMIN" => Role.Admin, "TREASURER" => Role.Treasurer, _ => Role.Reader };

    /// <summary>Matrice des droits : un seul endroit à lire (et à changer) pour savoir qui peut quoi.</summary>
    public static bool Has(Role role, Permission p) => role switch
    {
        Role.Admin => true,
        Role.Treasurer => p is Permission.WriteLedger or Permission.ManageStructure or Permission.ValidateYear,
        _ => false
    };
}

/// <summary>Utilisateur authentifié, porté par le service comptable pour contrôler chaque opération et tracer
/// le journal.</summary>
public sealed record Session(long UserId, string UserName, string DisplayName, Role Role)
{
    /// <summary>Session interne (import en ligne de commande, tests) : droits d'administrateur, nom « système ».</summary>
    public static Session System { get; } = new(0, "système", "Système", Role.Admin);

    public bool Can(Permission p) => RoleInfo.Has(Role, p);
}

/// <summary>Compte utilisateur tel que présenté à l'application (jamais le hachage du mot de passe).</summary>
public sealed record AppUser(long Id, string UserName, string DisplayName, Role Role, bool Active,
    bool MustChangePassword, DateTime? LastLoginAt, DateTime CreatedAt, string? CreatedBy)
{
    public string RoleText => RoleInfo.DisplayName(Role);
}

/// <summary>Hachage des mots de passe : PBKDF2-HMAC-SHA256, sel aléatoire de 16 octets par compte, nombre
/// d'itérations enregistré avec le hachage (réévaluable plus tard). Format : <c>PBKDF2-SHA256$itérations$sel$hash</c>.</summary>
public static class PasswordHasher
{
    private const string Prefix = "PBKDF2-SHA256";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    public const int DefaultIterations = 600_000;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Compare en temps constant.</summary>
    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out var iterations) || iterations < 1)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public static class PasswordPolicy
{
    public const int MinLength = 8;

    /// <summary>Retourne un message d'erreur, ou null si le mot de passe est acceptable.</summary>
    public static string? Validate(string? password, string? userName = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"Le mot de passe doit contenir au moins {MinLength} caractères.";
        if (password.Trim().Length == 0) return "Le mot de passe ne peut pas être vide.";
        if (!string.IsNullOrEmpty(userName) && password.Equals(userName, StringComparison.OrdinalIgnoreCase))
            return "Le mot de passe ne doit pas être identique à l'identifiant.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            return "Le mot de passe doit contenir au moins une lettre et un chiffre.";
        return null;
    }
}
