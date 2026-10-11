using System.Windows;
using System.Windows.Threading;
using ToutCompteFait.Core.Licensing;
using ToutCompteFait.Dialogs;

namespace ToutCompteFait.Services;

/// <summary>Contrôle de licence de l'application : CLUF, validation au lancement puis toutes les 24 h.</summary>
public static class LicenseGate
{
    private static DispatcherTimer? _timer;

    public static LicenseService Service { get; private set; } = null!;

    /// <summary>Fonction centrale de contrôle des fonctionnalités (voir <see cref="LicenseService.HasFeature"/>).</summary>
    public static bool HasFeature(string featureId) => Service is not null && Service.HasFeature(featureId);

    /// <summary>CLUF (premier lancement ou texte modifié) puis licence. Retourne false si l'application doit se fermer.</summary>
    public static bool Run()
    {
        if (!AcceptEula()) return false;

        try
        {
            Service = new LicenseService(new LicenseClient(), LicensePlatform.CreateIdentity(), LicensePlatform.CreateStore());
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
        return true;
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
            if (state.IsUsable) return;
            _timer?.Stop();
            MessageBox.Show(Core.Licensing.LicenseReasons.Describe(state.Reason) + "\n\nLe logiciel va se fermer.",
                "Licence", MessageBoxButton.OK, MessageBoxImage.Warning);
            Application.Current.Shutdown();
        };
        _timer.Start();
    }
}
