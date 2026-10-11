using System.Net.Http.Json;
using System.Text.Json;

namespace ToutCompteFait.Core.Licensing;

/// <summary>Le serveur de licences n'a pas répondu (réseau, délai dépassé, réponse inexploitable, limite de débit).</summary>
public sealed class LicenseNetworkException : Exception
{
    public LicenseNetworkException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Client HTTP du serveur de licences. Délai court (5 s) : le démarrage n'est jamais bloqué longtemps.
/// Aucune clé ni donnée de licence n'est écrite dans un message d'erreur.</summary>
public sealed class LicenseClient : IDisposable
{
    public const string DefaultBaseUrl = "https://licences.tlpc.fr";
    public const string ProductSlug = "tout-compte-fait";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;

    public LicenseClient(HttpMessageHandler? handler = null, string baseUrl = DefaultBaseUrl)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(baseUrl);
        _http.Timeout = Timeout;
    }

    public Task<LicenseResponse> ActivateAsync(string key, string deviceId, string deviceName, CancellationToken ct = default) =>
        PostAsync("/v1/activate", new { licenseKey = key, productSlug = ProductSlug, deviceId, deviceName }, ct);

    public Task<LicenseResponse> ValidateAsync(string key, string deviceId, CancellationToken ct = default) =>
        PostAsync("/v1/validate", new { licenseKey = key, productSlug = ProductSlug, deviceId }, ct);

    public Task<LicenseResponse> DeactivateAsync(string key, string deviceId, CancellationToken ct = default) =>
        PostAsync("/v1/deactivate", new { licenseKey = key, productSlug = ProductSlug, deviceId }, ct);

    public Task<LicenseResponse> RequestDemoAsync(string deviceId, string deviceName, CancellationToken ct = default) =>
        PostAsync("/v1/request-demo", new { productSlug = ProductSlug, deviceId, deviceName }, ct);

    /// <summary><c>customerName</c> est omis quand il est vide.</summary>
    public Task<LicenseResponse> BypassPingAsync(string deviceId, string deviceName, string? customerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customerName))
            return PostAsync("/v1/bypass/ping", new { productSlug = ProductSlug, deviceId, deviceName }, ct);
        return PostAsync("/v1/bypass/ping", new { productSlug = ProductSlug, deviceId, deviceName, customerName = customerName.Trim() }, ct);
    }

    public Task<LicenseResponse> BypassRemovedAsync(string deviceId, CancellationToken ct = default) =>
        PostAsync("/v1/bypass/removed", new { productSlug = ProductSlug, deviceId }, ct);

    private async Task<LicenseResponse> PostAsync(string path, object body, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(path, body, Json, ct).ConfigureAwait(false);
            // Refus métier (404, 403, 410…) : le corps JSON porte « valid:false » et le motif.
            if ((int)response.StatusCode is >= 500 or 429)
                throw new LicenseNetworkException("Serveur de licences indisponible (" + (int)response.StatusCode + ").");
            var parsed = await response.Content.ReadFromJsonAsync<LicenseResponse>(Json, ct).ConfigureAwait(false);
            if (parsed is null) throw new LicenseNetworkException("Réponse du serveur de licences vide.");
            return parsed;
        }
        catch (LicenseNetworkException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException
                                       or OperationCanceledException or InvalidOperationException)
        {
            throw new LicenseNetworkException("Serveur de licences injoignable.", ex);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Fichier <c>licence.ini</c> à côté de l'exécutable : sa présence met l'application en licence gratuite, sans
/// contrôle. Son contenu (texte libre) est le nom du client transmis lors du ping.</summary>
public sealed class BypassFile
{
    public const string FileName = "licence.ini";

    public string Path { get; }

    public BypassFile(string? directory = null) =>
        Path = System.IO.Path.Combine(directory ?? AppContext.BaseDirectory, FileName);

    public bool Exists => File.Exists(Path);

    /// <summary>Texte du fichier (première ligne non vide), ou null s'il est vide / illisible.</summary>
    public string? ReadCustomerName()
    {
        try
        {
            var line = File.ReadAllLines(Path).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line is null) return null;
            return line.Length > 200 ? line[..200] : line;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool TryCreate(string? content)
    {
        try
        {
            File.WriteAllText(Path, (content ?? "").Trim() + (string.IsNullOrWhiteSpace(content) ? "" : Environment.NewLine));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public bool TryDelete()
    {
        try { File.Delete(Path); return !File.Exists(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
