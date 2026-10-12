using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace ToutCompteFait.Core.Business;

/// <summary>Informations d'une entreprise récupérées à partir de son SIREN ou SIRET, via l'API publique « Recherche
/// d'entreprises » de l'État (recherche-entreprises.api.gouv.fr, données Sirene/INSEE, gratuite et sans clé).
/// Seul le numéro saisi est envoyé ; aucune donnée de l'application n'est transmise.</summary>
public static class BusinessLookup
{
    public const string BaseUrl = "https://recherche-entreprises.api.gouv.fr/search";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ToutCompteFait");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    /// <summary>Numéro valide (clé de Luhn) de 9 chiffres (SIREN) ou 14 chiffres (SIRET) ; espaces ignorés.</summary>
    public static bool TryParseNumber(string? text, out string digits)
    {
        digits = new string((text ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length is not (9 or 14)) return false;
        // Cas particulier historique : le SIRET de La Poste (356 000 000 xxxxx) ne respecte pas la clé de Luhn.
        if (digits.StartsWith("356000000", StringComparison.Ordinal) && digits.Length == 14)
            return digits.Take(14).Sum(c => c - '0') % 5 == 0;
        return Luhn(digits);
    }

    private static bool Luhn(string digits)
    {
        var sum = 0;
        var alt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var n = digits[i] - '0';
            if (alt)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            alt = !alt;
        }
        return sum % 10 == 0;
    }

    /// <summary>Interroge l'API. Lève <see cref="ComptaException"/> avec un message clair en cas d'échec.</summary>
    public static async Task<BusinessProfile> LookupAsync(string numberText, HttpMessageHandler? handler = null,
        CancellationToken ct = default)
    {
        if (!TryParseNumber(numberText, out var digits))
            throw new ComptaException("Numéro invalide : saisissez un SIREN (9 chiffres) ou un SIRET (14 chiffres) valide.");
        var client = handler is null ? Http : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var response = await client.GetAsync($"{BaseUrl}?q={digits}&per_page=1", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ComptaException($"Le service de recherche d'entreprises a répondu {(int)response.StatusCode}. Réessayez plus tard.");
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json, digits);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new ComptaException("Le service de recherche d'entreprises est injoignable (connexion Internet ?).");
        }
        finally
        {
            if (handler is not null) client.Dispose();
        }
    }

    /// <summary>Lit la réponse de l'API (testable sans réseau). <paramref name="digits"/> : SIREN ou SIRET demandé.</summary>
    public static BusinessProfile Parse(string json, string digits)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new ComptaException("Réponse inattendue du service de recherche d'entreprises."); }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
                throw new ComptaException("Aucune entreprise trouvée pour ce numéro.");

            var siren = digits[..9];
            JsonElement? found = null;
            foreach (var r in results.EnumerateArray())
                if (Str(r, "siren") == siren) { found = r; break; }
            if (found is not { } e) throw new ComptaException("Aucune entreprise trouvée pour ce numéro.");

            JsonElement? siege = e.TryGetProperty("siege", out var s) && s.ValueKind == JsonValueKind.Object ? s : null;
            var siret = digits.Length == 14 ? digits : siege is { } sg ? Str(sg, "siret") : null;

            // L'établissement demandé (SIRET) peut différer du siège : on le cherche dans « matching_etablissements ».
            JsonElement? target = siege;
            if (digits.Length == 14 && e.TryGetProperty("matching_etablissements", out var matches) && matches.ValueKind == JsonValueKind.Array)
                foreach (var m in matches.EnumerateArray())
                    if (Str(m, "siret") == digits) { target = m; break; }

            var ape = ApeMapper.Normalize(target is { } t ? Str(t, "activite_principale") : null)
                      ?? ApeMapper.Normalize(Str(e, "activite_principale"));
            var codes = new List<string>();
            if (ape is not null) codes.Add(ape);
            foreach (var group in new[] { siege, target })
                if (group is { } g && Str(g, "activite_principale") is { } extra && ApeMapper.Normalize(extra) is { } n && !codes.Contains(n))
                    codes.Add(n);

            var name = Str(e, "nom_complet") ?? Str(e, "nom_raison_sociale");
            var address = target is { } ta ? Str(ta, "adresse") : null;
            return new BusinessProfile
            {
                Siren = siren,
                Siret = siret,
                Name = name,
                Address = address,
                PostalCode = target is { } tp ? Str(tp, "code_postal") : null,
                City = target is { } tc ? Str(tc, "libelle_commune") : null,
                CreationDate = ParseDate(Str(e, "date_creation")),
                LegalNature = Str(e, "nature_juridique"),
                ApeMain = ape,
                ApeCodes = codes,
                FetchedAt = DateTime.Now
            };
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim())
            : null;

    private static DateOnly? ParseDate(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}