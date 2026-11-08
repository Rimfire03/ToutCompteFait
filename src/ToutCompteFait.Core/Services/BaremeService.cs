using ToutCompteFait.Core.Business;

namespace ToutCompteFait.Core.Services;

/// <summary>Télécharge le barème URSSAF publié dans le dépôt du logiciel. Aucune donnée de l'utilisateur n'est envoyée : une
/// simple lecture du fichier public.</summary>
public sealed class BaremeService
{
    private static readonly HttpClient Shared = Create();
    private readonly HttpClient _http;

    public BaremeService(HttpClient? http = null) => _http = http ?? Shared;

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(Editions.Current.ExeBaseName + "-Bareme");
        return c;
    }

    /// <summary>Lit les révisions du barème. Lève une exception si le réseau ou le fichier est indisponible ou invalide.</summary>
    public async Task<IReadOnlyList<BaremeEntry>> FetchAsync() => Bareme.Parse(await _http.GetStringAsync(Bareme.Url));
}