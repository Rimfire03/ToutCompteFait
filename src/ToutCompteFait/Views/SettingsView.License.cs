using System.Windows;
using TomLine.Licensing;
using ToutCompteFait.Core.Licensing;
using ToutCompteFait.Dialogs;
using ToutCompteFait.Services;

namespace ToutCompteFait.Views;

/// <summary>Onglet « Licence » : type, client, échéance, désactivation du poste, CLUF.</summary>
public partial class SettingsView
{
    private void InitLicense()
    {
        var service = LicenseGate.Service;
        var state = service?.State;
        var l = state?.License;
        if (state is null || state.Status == LicenseStatus.NoLicense)
        {
            LicenseTypeText.Text = "Aucune licence active sur ce poste.";
            LicenseCustomerText.Text = "";
            LicenseExpiryText.Text = "";
            LicenseStatusText.Text = "";
            DeactivateButton.IsEnabled = false;
        }
        else if (state.Status == LicenseStatus.Bypass)
        {
            LicenseTypeText.Text = "Licence gratuite (fichier licence.ini)";
            LicenseCustomerText.Text = service!.Bypass.ReadCustomerName() is { } n ? "Client : " + n : "";
            LicenseExpiryText.Text = "Aucune échéance.";
            LicenseStatusText.Text = "Aucun contrôle de licence n'est effectué en licence gratuite.";
            DeactivateButton.IsEnabled = false;
        }
        else if (l is not null)
        {
            LicenseTypeText.Text = "Type de licence : " + (l.IsDemo ? "Démonstration" : string.IsNullOrWhiteSpace(l.Type) ? (l.IsFull ? "Complète" : "Standard") : l.Type)
                                   + (l.IsFull && !l.IsDemo ? " (complète)" : "");
            LicenseCustomerText.Text = string.IsNullOrWhiteSpace(l.CustomerName) ? "" : "Client : " + l.CustomerName;
            var now = DateTimeOffset.UtcNow;
            if (l.ExpiresAt is { } exp)
            {
                var left = state.DaysLeft(now) ?? 0;
                LicenseExpiryText.Text = $"Expire le {exp.LocalDateTime:dd/MM/yyyy}" + (l.IsDemo ? $" — {left} jour(s) restant(s)" : "");
            }
            else LicenseExpiryText.Text = "Sans échéance.";
            LicenseStatusText.Text = state.Status == LicenseStatus.OfflineGrace
                ? $"Serveur de licences injoignable : fonctionnement hors-ligne autorisé jusqu'au {state.GraceUntil?.LocalDateTime:dd/MM/yyyy}."
                : l.LastValidatedAt is { } v ? $"Dernière validation en ligne : {v.LocalDateTime:dd/MM/yyyy HH:mm}." : "";
            DeactivateButton.IsEnabled = true;
        }

        var s = App.Settings;
        EulaStatusText.Text = s.EulaAcceptedAt is { } at
            ? $"Contrat de licence accepté le {at:dd/MM/yyyy HH:mm} (version {Eula.Revision})."
            : "";
    }

    private async void Deactivate_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("Désactiver ce poste ?\n\nLa licence locale sera supprimée et la place de ce poste libérée sur le serveur. " +
                        "Le logiciel se fermera ; une nouvelle activation sera demandée au prochain lancement.", "Désactiver ce poste"))
            return;
        DeactivateButton.IsEnabled = false;
        var (done, reason) = await LicenseGate.Service.DeactivateAsync();
        if (!done)
        {
            DeactivateButton.IsEnabled = true;
            Ui.Error(LicenseReasons.Describe(reason) + "\nLa licence n'a pas été supprimée : réessayez avec une connexion Internet.");
            return;
        }
        Ui.Info("Ce poste est désactivé. Le logiciel va se fermer.");
        Application.Current.Shutdown();
    }

    private void ReadEula_Click(object sender, RoutedEventArgs e) =>
        new EulaWindow(requireAcceptance: false) { Owner = Window.GetWindow(this) }.ShowDialog();
}
