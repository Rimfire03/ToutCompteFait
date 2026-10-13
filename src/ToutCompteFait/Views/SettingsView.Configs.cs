using System.IO;
using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Security;
using ToutCompteFait.Core.Services;
using ToutCompteFait.Dialogs;

namespace ToutCompteFait.Views;

/// <summary>Onglet « Configurations » (statut Développeur) : plusieurs configurations de l'application, une par base de
/// données (production, essais, démonstration…), et bascule de l'une à l'autre sans ressaisir les paramètres.</summary>
public partial class SettingsView
{
    private sealed record ConfigRow(ConfigProfile Profile, string Name, string Kind, string Target, string State);

    private readonly DataGrid _configGrid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 120, MaxHeight = 360
    };

    private void InitConfigs()
    {
        if (_scope != SettingsScope.Application || !App.Session.Can(Permission.ManageConfigurations)) return;
        Tabs.Items.Add(new TabItem { Header = "Configurations", Content = BuildConfigsPanel() });
        ReloadConfigs();
    }

    private UIElement BuildConfigsPanel()
    {
        var root = new StackPanel { Margin = new Thickness(0, 12, 12, 24), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(new TextBlock
        {
            Style = Sty("MutedText"), TextWrapping = TextWrapping.Wrap,
            Text = "Statut Développeur. Une configuration mémorise la base de données à ouvrir (fichier SQLite ou serveur) sous un nom : " +
                   "production, essais, démonstration… Basculer de l'une à l'autre redémarre le logiciel sur l'écran de connexion de cette base. " +
                   "Les configurations sont enregistrées sur ce poste, dans le dossier « Configurations » ; le mot de passe d'un serveur y est " +
                   "chiffré pour votre session Windows. Elles ne contiennent aucune donnée comptable."
        });

        _configGrid.Margin = new Thickness(0, 10, 0, 0);
        void Col(string header, string path, double width, bool star = false) =>
            _configGrid.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = new System.Windows.Data.Binding(path),
                Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
                ElementStyle = Sty("LeftCell")
            });
        Col("Nom", nameof(ConfigRow.Name), 200);
        Col("Type", nameof(ConfigRow.Kind), 130);
        Col("Base de données", nameof(ConfigRow.Target), 0, star: true);
        Col("État", nameof(ConfigRow.State), 130);
        _configGrid.MouseDoubleClick += (_, _) => SwitchSelectedConfig();
        root.Children.Add(_configGrid);

        var bar = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button B(string text, string style, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Style = Sty(style) };
            b.Click += click;
            bar.Children.Add(b);
            return b;
        }
        B("Basculer vers cette configuration", "PrimaryButton", (_, _) => SwitchSelectedConfig());
        B("Enregistrer la base actuelle sous un nom…", "SecondaryButton", (_, _) => SaveCurrentConfig());
        B("Ajouter un fichier SQLite…", "SecondaryButton", (_, _) => AddSqliteConfig());
        B("Ajouter depuis un fichier de configuration serveur…", "SecondaryButton", (_, _) => AddServerConfig());
        B("Supprimer", "DangerButton", (_, _) => DeleteSelectedConfig());
        root.Children.Add(bar);
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void ReloadConfigs()
    {
        _configGrid.ItemsSource = ConfigProfiles.List().Select(p => new ConfigRow(p, p.Name,
            p.UseServer ? DatabaseProviderInfo.DisplayName(p.Server.Provider) : "Fichier SQLite", p.Describe(),
            ConfigProfiles.IsCurrent(p, App.Settings) ? "● Active" : "")).ToList();
    }

    private ConfigProfile? SelectedConfig => (_configGrid.SelectedItem as ConfigRow)?.Profile;

    private void SwitchSelectedConfig()
    {
        if (SelectedConfig is not { } p) { Ui.Info("Sélectionnez d'abord une configuration."); return; }
        if (ConfigProfiles.IsCurrent(p, App.Settings)) { Ui.Info("Cette configuration est déjà la configuration active."); return; }
        if (!Ui.Confirm($"Basculer vers la configuration « {p.Name} » ?\n\n{p.Describe()}\n\nLe logiciel va redémarrer sur l'écran de connexion de cette base.",
                "Changer de configuration")) return;
        if (App.SwitchConfiguration(p) is { } error) Ui.Error(error);
    }

    private string? AskConfigName(string suggestion) =>
        PromptWindow.Ask(Window.GetWindow(this), "Nom de la configuration",
            "Nom de cette configuration (ex. Production, Essais, Démonstration) :", suggestion, "Enregistrer");

    private void SaveCurrentConfig()
    {
        var name = AskConfigName(App.DatabaseName.Length > 0 ? App.DatabaseName : "Configuration");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (Ui.Try(() => ConfigProfiles.Save(ConfigProfiles.FromSettings(App.Settings, name)))) ReloadConfigs();
    }

    private void AddSqliteConfig()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir la base de données SQLite de cette configuration",
            Filter = "Base Tout Compte Fait (*.db)|*.db|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        var name = AskConfigName(Path.GetFileNameWithoutExtension(dlg.FileName));
        if (string.IsNullOrWhiteSpace(name)) return;
        var profile = new ConfigProfile { Name = name, UseServer = false, FilePath = dlg.FileName };
        if (Ui.Try(() => ConfigProfiles.Save(profile))) ReloadConfigs();
    }

    private void AddServerConfig()
    {
        var info = ConfigLoader.Pick(Window.GetWindow(this));
        if (info is null) return;
        var name = AskConfigName(info.Database);
        if (string.IsNullOrWhiteSpace(name)) return;
        var profile = new ConfigProfile { Name = name, UseServer = true, Server = info };
        if (Ui.Try(() => ConfigProfiles.Save(profile))) ReloadConfigs();
    }

    private void DeleteSelectedConfig()
    {
        if (SelectedConfig is not { } p) { Ui.Info("Sélectionnez d'abord une configuration."); return; }
        if (!Ui.Confirm($"Supprimer la configuration « {p.Name} » ?\n\nLa base de données elle-même n'est pas touchée.", "Supprimer")) return;
        if (Ui.Try(() => ConfigProfiles.Delete(p.Name))) ReloadConfigs();
    }
}