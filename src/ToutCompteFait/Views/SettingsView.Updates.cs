using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core.Security;
using ToutCompteFait.Core.Services;

namespace ToutCompteFait.Views;

/// <summary>Onglet « Mises à jour » : même principe que FiltresApp (releases GitHub, installation en un clic).</summary>
public partial class SettingsView
{
    private UpdateInfo? _pendingUpdate;
    private bool _updateInit;

    private void InitUpdates()
    {
        _updateInit = true;
        VersionText.Text = $"Tout Compte Fait — version {App.AppVersionText}";
        AutoUpdateBox.IsChecked = App.Settings.AutoUpdateEnabled;
        ChannelBox.ItemsSource = UpdateChannels.All;
        ChannelBox.SelectedItem = UpdateChannels.All.Contains(App.Settings.UpdateChannel) ? App.Settings.UpdateChannel : UpdateChannels.Main;
        _updateInit = false;
    }

    private void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (_updateInit) return;
        App.Settings.AutoUpdateEnabled = AutoUpdateBox.IsChecked == true;
        App.Settings.Save();
    }

    private async void Channel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updateInit || ChannelBox.SelectedItem is not string channel) return;
        App.Settings.UpdateChannel = channel;
        App.Settings.Save();
        _pendingUpdate = null;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "";
        await CheckUpdateAsync();   // vérification immédiate sur le nouveau canal
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdateAsync();

    private async Task CheckUpdateAsync()
    {
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        _pendingUpdate = null;
        UpdateStatusText.Text = "Recherche d'une mise à jour…";
        try
        {
            var info = await App.CheckForUpdateAsync();
            if (info is null)
                UpdateStatusText.Text = App.Updater.LastCheckNote ?? $"Vous utilisez la dernière version ({App.AppVersionText}).";
            else
            {
                _pendingUpdate = info;
                UpdateStatusText.Text = info.IsChannelSwitch
                    ? $"Retour sur le canal Main possible : version {info.Version} (actuelle : {App.AppVersionText}), même base de données."
                    : $"Nouvelle version disponible : {info.Version} (actuelle : {App.AppVersionText}).";
                if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
                    UpdateStatusText.Text += "\n\n" + info.ReleaseNotes.Split("<!--")[0].Trim();
                var admin = App.Session.Can(Permission.ManageDatabase);
                InstallUpdateButton.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
                if (!admin) UpdateStatusText.Text += "\n\nL'installation est réservée à un administrateur.";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Erreur pendant la vérification : {ex.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        if (!Ui.Confirm($"Télécharger et installer la version {_pendingUpdate.Version} ? Une copie de sauvegarde de la base de données sera " +
                        "faite avant toute chose. Le logiciel va se fermer puis redémarrer automatiquement.", "Installer la mise à jour"))
            return;
        InstallUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Téléchargement de la mise à jour…";
        await App.InstallUpdateAsync(_pendingUpdate);
        InstallUpdateButton.IsEnabled = true;
        CheckUpdateButton.IsEnabled = true;
    }
}