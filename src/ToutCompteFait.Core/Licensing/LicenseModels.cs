using System.Text.Json.Serialization;

namespace ToutCompteFait.Core.Licensing;

/// <summary>Licence telle que renvoyée par le serveur (<c>license</c>).</summary>
public sealed class LicenseDto
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("features")] public List<string>? Features { get; set; }
    [JsonPropertyName("isFull")] public bool IsFull { get; set; }
    [JsonPropertyName("customerName")] public string? CustomerName { get; set; }
    [JsonPropertyName("maxActivations")] public int? MaxActivations { get; set; }
}

/// <summary>Réponse des routes activate / validate / request-demo.</summary>
public sealed class LicenseResponse
{
    [JsonPropertyName("valid")] public bool Valid { get; set; }
    [JsonPropertyName("license")] public LicenseDto? License { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("commands")] public List<string>? Commands { get; set; }
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("bypassName")] public string? BypassName { get; set; }

    /// <summary>Commandes demandées par le serveur, qu'elles arrivent en liste (<c>commands</c>) ou seule (<c>command</c>).</summary>
    public IEnumerable<string> AllCommands =>
        (Commands ?? new List<string>()).Concat(string.IsNullOrWhiteSpace(Command) ? Array.Empty<string>() : new[] { Command! });
}

/// <summary>Licence conservée localement (chiffrée, sans fichier) : jamais écrite en clair ni dans un journal.</summary>
public sealed class StoredLicense
{
    public string Key { get; set; } = "";
    public string? Type { get; set; }
    public string? CustomerName { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public List<string> Features { get; set; } = new();
    public bool IsFull { get; set; }
    public int? MaxActivations { get; set; }

    /// <summary>Dernière validation EN LIGNE réussie (activation, démo ou validation) : base de la grâce hors-ligne.</summary>
    public DateTimeOffset? LastValidatedAt { get; set; }

    public bool IsDemo => !IsFull && Type is { } t &&
        (t.Contains("demo", StringComparison.OrdinalIgnoreCase) || t.Contains("trial", StringComparison.OrdinalIgnoreCase)
         || t.Contains("essai", StringComparison.OrdinalIgnoreCase));
}

public enum LicenseStatus
{
    /// <summary>Aucune licence locale : la fenêtre de saisie doit être proposée.</summary>
    NoLicense,
    /// <summary>Licence validée par le serveur.</summary>
    Valid,
    /// <summary>Serveur injoignable : fonctionnement dans la grâce hors-ligne.</summary>
    OfflineGrace,
    /// <summary>Refus explicite du serveur, ou grâce épuisée : application verrouillée.</summary>
    Locked,
    /// <summary>Fichier licence.ini présent : licence gratuite, aucun contrôle.</summary>
    Bypass
}

/// <summary>État de la licence à un instant donné.</summary>
public sealed record LicenseState(LicenseStatus Status, StoredLicense? License = null, string? Reason = null,
    DateTimeOffset? GraceUntil = null)
{
    public bool IsUsable => Status is LicenseStatus.Valid or LicenseStatus.OfflineGrace or LicenseStatus.Bypass;

    /// <summary>Jours restants de la licence (échéance), arrondis au jour supérieur ; null si sans échéance.</summary>
    public int? DaysLeft(DateTimeOffset now) =>
        License?.ExpiresAt is { } e ? Math.Max(0, (int)Math.Ceiling((e - now).TotalDays)) : null;
}

/// <summary>Messages affichables pour les motifs de refus du serveur.</summary>
public static class LicenseReasons
{
    public static string Describe(string? reason) => reason switch
    {
        "license_not_found" => "Cette clé de licence est inconnue.",
        "license_revoked" => "Cette licence a été révoquée.",
        "license_expired" => "Cette licence a expiré.",
        "activation_limit_reached" => "Le nombre maximal de postes activés pour cette licence est atteint.",
        "offline_grace_expired" => "Le serveur de licences est injoignable depuis trop longtemps : connectez ce poste à Internet.",
        "no_prior_validation" => "Le serveur de licences est injoignable et ce poste n'a jamais été validé en ligne.",
        "network" => "Le serveur de licences est injoignable. Vérifiez votre connexion Internet.",
        null or "" => "Licence refusée.",
        _ => "Licence refusée (" + reason + ")."
    };
}
