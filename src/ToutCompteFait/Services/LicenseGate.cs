using System.Windows;
using System.Windows.Threading;
using TomLine.Licensing;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Licensing;
using ToutCompteFait.Dialogs;
using TomLine.UI.Wpf.Controls;

namespace ToutCompteFait.Services;

/// <summary>Contrôle de licence de l'application : CLUF, validation au lancement puis toutes les 24 h.</summary>
public static class LicenseGate
{
    private static DispatcherTimer? _timer;

    /// <summary>Configuration du client de licences partagé (TomLine.Licensing). <c>StoreName</c> reprend les noms de stockage
    /// historiques : les licences déjà enregistrées restent lisibles. <c>PublicKeys</c> : à renseigner (clé publique du serveur),
    /// puis passer <c>RequireSignature</c> à vrai une fois le serveur déployé avec sa clé de signature.</summary>
    private static readonly LicenseOptions Options = new()
    {
        // Propres à la version du logiciel (voir Editions) : la version historique garde le produit et le nom de stockage
        // d'origine, donc les licences déjà enregistrées restent lisibles ; une licence ne donne droit qu'à une version.
        ProductSlug = Editions.Current.ProductSlug,
        StoreName = Editions.Current.DataName,
        Edition = Editions.Current.LicenseEdition,
        // Clé publique du serveur de licences (vérification seule, sans danger dans le dépôt).
        PublicKeys = new[] { "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjairPbdPQuYiWSJ6MtVo6xchepZYn9h+JFdSI72MNzuVwmFgTCiYBRuPHJBJT45eC+qloOQbBWZDSZSkZxttvQ==" },
        // Le serveur signe activate/validate/request-demo : une réponse sans signature valide est refusée.
        RequireSignature = true,
    };

    public static LicenseService Service { get; private set; } = null!;

    /// <summary>Fonction centrale de contrôle des fonctionnalités (voir <see cref="LicenseService.HasFeature"/>).</summary>
    public static bool HasFeature(string featureId) => Service is not null && Service.HasFeature(featureId);

    /// <summary>CLUF (premier lancement ou texte modifié) puis licence. Retourne false si l'application doit se fermer.</summary>
    public static bool Run()
    {
        if (!AcceptEula()) return false;

        try
        {
            // Licence gratuite (fichier licence*.ini) propre à la version : le fichier d'une version ne vaut jamais pour une autre.
            Service = new LicenseService(new LicenseClient(Options), LicensePlatform.CreateIdentity(), LicensePlatform.CreateStore(Options),
                new EditionBypassFile(Editions.Current));
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            MessageBox.Show("Impossible d'identifier ce poste pour la licence :\n\n" + ex.Message, "Licence",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        // Délai réseau court (5 s) : le démarrage n'est jamais bloqué longtemps.
        var state = Task.Run(() => Service.StartAsync()).GetAwaiter().GetResult();
        while (!state.IsUsable)
        {
            var window = new LicenseWindow(Service, state.Reason);
            if (window.ShowDialog() != true) return false;
            state = Service.State;
        }

        if (state.Status != LicenseStatus.Bypass) StartTimer();
        // Informations client demandées par le serveur : jamais bloquant, une seule fois par lancement, jamais en licence.ini.
        TryShowProfilePrompt();
        return true;
    }

    private static void TryShowProfilePrompt()
    {
        try { LicenseProfilePrompt.ShowIfRequired(Service, Application.Current?.MainWindow, Editions.AppName); }
        catch (Exception) { /* la saisie des informations ne doit jamais gêner le lancement */ }
    }

    private static bool AcceptEula()
    {
        var settings = App.Settings;
        if (Eula.IsAccepted(settings.EulaAcceptedHash)) return true;
        var changed = !string.IsNullOrEmpty(settings.EulaAcceptedHash);
        var window = new EulaWindow(requireAcceptance: true, changed);
        if (window.ShowDialog() != true) return false;
        settings.EulaAcceptedHash = Eula.Hash;
        settings.EulaAcceptedAt = DateTime.Now;
        settings.Save();
        return true;
    }

    private static void StartTimer()
    {
        _timer = new DispatcherTimer { Interval = LicenseService.RevalidationInterval };
        _timer.Tick += async (_, _) =>
        {
            var state = await Service.RevalidateAsync();
            if (state.IsUsable) { TryShowProfilePrompt(); return; }
            _timer?.Stop();
            MessageBox.Show(LicenseReasons.Describe(state.Reason) + "\n\nLe logiciel va se fermer.",
                "Licence", MessageBoxButton.OK, MessageBoxImage.Warning);
            Application.Current.Shutdown();
        };
        _timer.Start();
    }
}
