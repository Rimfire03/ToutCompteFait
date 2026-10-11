namespace ToutCompteFait.Core.Licensing;

/// <summary>Cœur du système de licences (mêmes règles sous Windows et macOS) :
/// validation en ligne à chaque lancement puis toutes les 24 h ; le serveur fait foi immédiatement (refus = verrouillage,
/// aucune grâce) ; serveur injoignable = grâce hors-ligne de min(dernière validation + 30 jours, échéance), et aucune
/// grâce sans validation en ligne préalable ; <c>licence.ini</c> = licence gratuite sans contrôle ; commandes à distance
/// <c>install_bypass</c> / <c>remove_bypass</c>.</summary>
public sealed class LicenseService
{
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(30);
    public static readonly TimeSpan RevalidationInterval = TimeSpan.FromHours(24);

    private readonly LicenseClient _client;
    private readonly IDeviceIdentity _device;
    private readonly ILicenseStore _store;
    private readonly BypassFile _bypass;
    private readonly Func<DateTimeOffset> _now;

    public LicenseService(LicenseClient client, IDeviceIdentity device, ILicenseStore store, BypassFile? bypass = null,
        Func<DateTimeOffset>? clock = null)
    {
        _client = client;
        _device = device;
        _store = store;
        _bypass = bypass ?? new BypassFile();
        _now = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Dernier état connu (mis à jour par chaque opération).</summary>
    public LicenseState State { get; private set; } = new(LicenseStatus.NoLicense);

    public string DeviceName => _device.DeviceName;
    public BypassFile Bypass => _bypass;

    // ------------------------------------------------------------------ fonctionnalités

    /// <summary>Fonction centrale de contrôle d'accès : vrai si la licence est pleine, si sa liste de fonctionnalités
    /// contient <paramref name="featureId"/>, ou pour une démo en cours (tout est débloqué). Fonctionnement en licence
    /// gratuite (<c>licence.ini</c>) : tout est disponible. Faux si l'application n'est pas utilisable.</summary>
    public bool HasFeature(string featureId)
    {
        var s = State;
        if (s.Status == LicenseStatus.Bypass) return true;
        if (!s.IsUsable || s.License is not { } l) return false;
        if (l.IsFull || l.IsDemo) return true;
        return l.Features.Any(f => string.Equals(f, featureId, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ démarrage et validation périodique

    /// <summary>À appeler une fois au lancement.</summary>
    public async Task<LicenseState> StartAsync(CancellationToken ct = default)
    {
        if (_bypass.Exists)
        {
            var ping = await TryAsync(() => _client.BypassPingAsync(_device.DeviceId, _device.DeviceName, _bypass.ReadCustomerName(), ct));
            if (ping is not null && ping.AllCommands.Contains("remove_bypass", StringComparer.OrdinalIgnoreCase))
            {
                if (_bypass.TryDelete())
                    await TryAsync(() => _client.BypassRemovedAsync(_device.DeviceId, ct));
                else
                    return State = new LicenseState(LicenseStatus.Bypass);   // suppression impossible : reste en gratuit
            }
            else return State = new LicenseState(LicenseStatus.Bypass);
        }

        var stored = _store.Load();
        if (stored is null) return State = new LicenseState(LicenseStatus.NoLicense);
        return await ValidateStoredAsync(stored, ct);
    }

    /// <summary>Revalidation périodique (24 h). Sans effet en licence gratuite ni sans licence.</summary>
    public async Task<LicenseState> RevalidateAsync(CancellationToken ct = default)
    {
        if (State.Status == LicenseStatus.Bypass) return State;
        var stored = _store.Load();
        if (stored is null) return State = new LicenseState(LicenseStatus.NoLicense);
        return await ValidateStoredAsync(stored, ct);
    }

    private async Task<LicenseState> ValidateStoredAsync(StoredLicense stored, CancellationToken ct)
    {
        LicenseResponse resp;
        try
        {
            resp = await _client.ValidateAsync(stored.Key, _device.DeviceId, ct);
        }
        catch (LicenseNetworkException)
        {
            return State = Offline(stored);
        }

        if (!resp.Valid || resp.License is null)
        {
            // Refus explicite : verrouillage immédiat, aucune grâce. La licence locale devient inutile.
            _store.Clear();
            return State = new LicenseState(LicenseStatus.Locked, null, resp.Reason ?? "invalid_response");
        }

        Apply(stored, resp.License);
        stored.LastValidatedAt = _now();
        _store.Save(stored);
        State = new LicenseState(LicenseStatus.Valid, stored);

        if (resp.AllCommands.Contains("install_bypass", StringComparer.OrdinalIgnoreCase))
            await InstallBypassAsync(resp.BypassName, stored.CustomerName, ct);
        return State;
    }

    /// <summary>Serveur injoignable : grâce = min(dernière validation + 30 j, échéance) ; aucune sans validation préalable.</summary>
    private LicenseState Offline(StoredLicense stored)
    {
        if (stored.LastValidatedAt is not { } last)
            return new LicenseState(LicenseStatus.Locked, stored, "no_prior_validation");
        var until = last + OfflineGrace;
        if (stored.ExpiresAt is { } exp && exp < until) until = exp;
        var now = _now();
        if (now <= until) return new LicenseState(LicenseStatus.OfflineGrace, stored, null, until);
        var expired = stored.ExpiresAt is { } e && now > e;
        return new LicenseState(LicenseStatus.Locked, stored, expired ? "license_expired" : "offline_grace_expired", until);
    }

    // ------------------------------------------------------------------ commandes à distance

    private async Task InstallBypassAsync(string? bypassName, string? customerName, CancellationToken ct)
    {
        if (_bypass.Exists)
        {
            // Fichier déjà là : jamais écrasé, sauf s'il est vide ET que rien d'autre ne peut le nommer (licence sans
            // nom de client) alors que le serveur fournit un nom.
            if (_bypass.ReadCustomerName() is not null || !string.IsNullOrWhiteSpace(customerName) || string.IsNullOrWhiteSpace(bypassName))
            {
                // Rien à écrire : on se contente du ping.
            }
            else _bypass.TryCreate(bypassName);
        }
        else
        {
            var content = !string.IsNullOrWhiteSpace(bypassName) ? bypassName : null;
            if (!_bypass.TryCreate(content)) return;   // dossier non inscriptible : on reste en licence normale
        }
        await TryAsync(() => _client.BypassPingAsync(_device.DeviceId, _device.DeviceName, _bypass.ReadCustomerName(), ct));
        State = new LicenseState(LicenseStatus.Bypass);
    }

    // ------------------------------------------------------------------ activation, démo, désactivation

    /// <summary>Active une clé saisie. En cas de refus, l'état reste « sans licence » avec le motif.</summary>
    public async Task<LicenseState> ActivateAsync(string key, CancellationToken ct = default)
    {
        key = (key ?? "").Trim();
        if (key.Length == 0) return State = new LicenseState(LicenseStatus.NoLicense, null, "license_not_found");
        try
        {
            var resp = await _client.ActivateAsync(key, _device.DeviceId, _device.DeviceName, ct);
            return StoreFromResponse(resp, key);
        }
        catch (LicenseNetworkException)
        {
            return State = new LicenseState(LicenseStatus.NoLicense, null, "network");
        }
    }

    /// <summary>Demande la licence démo de ce poste : la clé reçue est stockée sans jamais être affichée.</summary>
    public async Task<LicenseState> RequestDemoAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _client.RequestDemoAsync(_device.DeviceId, _device.DeviceName, ct);
            return StoreFromResponse(resp, resp.License?.Key);
        }
        catch (LicenseNetworkException)
        {
            return State = new LicenseState(LicenseStatus.NoLicense, null, "network");
        }
    }

    private LicenseState StoreFromResponse(LicenseResponse resp, string? key)
    {
        var effectiveKey = string.IsNullOrWhiteSpace(resp.License?.Key) ? key : resp.License!.Key;
        if (!resp.Valid || resp.License is null || string.IsNullOrWhiteSpace(effectiveKey))
            return State = new LicenseState(LicenseStatus.NoLicense, null, resp.Reason ?? "invalid_response");
        var stored = new StoredLicense { Key = effectiveKey! };
        Apply(stored, resp.License);
        stored.LastValidatedAt = _now();
        _store.Save(stored);
        return State = new LicenseState(LicenseStatus.Valid, stored);
    }

    /// <summary>Désactive ce poste côté serveur puis supprime la licence locale. Si le serveur est injoignable, rien n'est
    /// supprimé (la place resterait occupée côté serveur) : retourne false avec le motif « network ».</summary>
    public async Task<(bool Done, string? Reason)> DeactivateAsync(CancellationToken ct = default)
    {
        var stored = _store.Load();
        if (stored is null) return (true, null);
        try
        {
            var resp = await _client.DeactivateAsync(stored.Key, _device.DeviceId, ct);
            // Une licence inconnue / révoquée côté serveur n'a plus rien à désactiver : on la retire aussi.
            _ = resp;
        }
        catch (LicenseNetworkException)
        {
            return (false, "network");
        }
        _store.Clear();
        State = new LicenseState(LicenseStatus.NoLicense);
        return (true, null);
    }

    // ------------------------------------------------------------------ utilitaires

    private static void Apply(StoredLicense target, LicenseDto l)
    {
        target.Type = l.Type;
        target.CustomerName = l.CustomerName;
        target.ExpiresAt = l.ExpiresAt;
        target.Features = l.Features?.ToList() ?? new List<string>();
        target.IsFull = l.IsFull;
        target.MaxActivations = l.MaxActivations;
    }

    private static async Task<LicenseResponse?> TryAsync(Func<Task<LicenseResponse>> call)
    {
        try { return await call(); }
        catch (LicenseNetworkException) { return null; }
    }
}
