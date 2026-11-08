using ToutCompteFait.Core.Business;

namespace ToutCompteFait.Core.Services;

/// <summary>Récupère le barème URSSAF / impôt à partir de deux sources publiques : les règles du simulateur officiel de l'URSSAF
/// (taux de cotisations, versement libératoire, plafonds du régime micro) et le fichier <c>bareme/urssaf.json</c> du dépôt du
/// logiciel (repli, et seuils de TVA que les règles officielles ne contiennent pas). Aucune donnée de l'utilisateur n'est
/// envoyée : de simples lectures de fichiers publics.</summary>
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

    /// <summary>Révisions du barème. Si l'une des deux sources est indisponible ou illisible, l'autre est utilisée ; lève une
    /// exception seulement si aucune ne l'est.</summary>
    public async Task<IReadOnlyList<BaremeEntry>> FetchAsync(DateOnly? today = null)
    {
        var day = today ?? DateOnly.FromDateTime(DateTime.Today);
        IReadOnlyList<BaremeEntry>? file = null;
        Exception? fileError = null;
        try { file = Bareme.Parse(await _http.GetStringAsync(Bareme.Url)); }
        catch (Exception ex) { fileError = ex; }

        var baseline = InForce(file ?? new[] { Bareme.Builtin }, day);
        BaremeEntry? official = null;
        Exception? officialError = null;
        try
        {
            var contributions = _http.GetStringAsync(PublicodesBareme.ContributionsUrl);
            var tax = _http.GetStringAsync(PublicodesBareme.TaxUrl);
            var company = _http.GetStringAsync(PublicodesBareme.CompanyUrl);
            official = PublicodesBareme.Build(await contributions, await tax, await company, day, baseline);
        }
        catch (Exception ex) { officialError = ex; }

        if (file is null && official is null)
            throw new InvalidOperationException("Aucune source de barème disponible : " + (officialError ?? fileError)!.Message, officialError ?? fileError);
        return Combine(file, official, day);
    }

    private static BaremeEntry InForce(IReadOnlyList<BaremeEntry> entries, DateOnly day) =>
        entries.Where(e => e.From <= day).OrderByDescending(e => e.From).FirstOrDefault() ?? Bareme.Builtin;

    /// <summary>Réunit les deux sources : le fichier du dépôt du logiciel fait foi à date égale ; la révision lue dans les règles
    /// officielles n'est ajoutée que si elle est plus récente que celle du fichier (ou du barème livré avec le logiciel).</summary>
    public static IReadOnlyList<BaremeEntry> Combine(IReadOnlyList<BaremeEntry>? file, BaremeEntry? official, DateOnly today)
    {
        var entries = (file ?? new[] { Bareme.Builtin }).ToList();
        if (official is not null && official.From > InForce(entries, today).From && entries.All(e => e.Label != official.Label))
            entries.Add(official);
        return entries.OrderBy(e => e.From).ToList();
    }
}