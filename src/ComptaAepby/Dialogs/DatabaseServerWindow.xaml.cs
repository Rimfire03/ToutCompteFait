using System.Windows;
using System.Windows.Controls;
using ComptaAepby.Core;
using ComptaAepby.Core.Services;

namespace ComptaAepby.Dialogs;

/// <summary>Choix du fichier SQLite ou du serveur de base de données (même fonctionnement que FiltresApp : test de
/// connexion, copie des données vers le serveur, enregistrement puis redémarrage).</summary>
public partial class DatabaseServerWindow : Window
{
    private sealed record ProviderItem(DatabaseProvider Provider)
    {
        public override string ToString() =>
            DatabaseProviderInfo.DisplayName(Provider) + (DatabaseProviderInfo.IsRecommended(Provider) ? " (conseillé)" : "");
    }

    private bool _loading = true;

    public DatabaseServerWindow()
    {
        InitializeComponent();
        ProviderCombo.ItemsSource = new[] { DatabaseProvider.PostgreSql, DatabaseProvider.MariaDb, DatabaseProvider.SqlServer }
            .Select(p => new ProviderItem(p)).ToList();

        var s = App.Settings.DatabaseServer;
        ProviderCombo.SelectedItem = ProviderCombo.Items.OfType<ProviderItem>().First(p => p.Provider == s.Provider);
        HostBox.Text = s.Host;
        PortBox.Text = s.Port.ToString();
        DatabaseBox.Text = s.Database;
        UserBox.Text = s.UserName;
        PasswordBox.Password = s.Password;
        SslBox.IsChecked = s.UseSsl;
        (App.Settings.UseDatabaseServer ? ServerMode : FileMode).IsChecked = true;
        _loading = false;
        UpdateState();
    }

    private DatabaseProvider Provider => (ProviderCombo.SelectedItem as ProviderItem)?.Provider ?? DatabaseProvider.PostgreSql;

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) UpdateState();
    }

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        PortBox.Text = DatabaseProviderInfo.DefaultPort(Provider).ToString();
        UpdateState();
    }

    private void UpdateState()
    {
        if (ServerPanel is null) return;
        ServerPanel.Visibility = ServerMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MigrateButton.IsEnabled = !App.IsReadOnly && !App.Target.IsServer;
        Hint.Text = Provider switch
        {
            DatabaseProvider.PostgreSql => "PostgreSQL : le serveur et la base peuvent être créés à l'avance ; si la base n'existe pas, le logiciel la crée (droit « créer des bases » nécessaire).",
            DatabaseProvider.MariaDb => "MariaDB / MySQL : le nom de la base est créé au premier lancement si l'utilisateur en a le droit.",
            _ => "SQL Server : l'utilisateur doit pouvoir se connecter par identifiant SQL (authentification SQL Server)."
        };
    }

    private DbConnectionInfo? ReadForm()
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            Ui.Error("Le port doit être un nombre entre 1 et 65535.");
            return null;
        }
        if (HostBox.Text.Trim().Length == 0 || DatabaseBox.Text.Trim().Length == 0 || UserBox.Text.Trim().Length == 0)
        {
            Ui.Error("Renseignez le serveur, le nom de la base et l'utilisateur.");
            return null;
        }
        var info = new DbConnectionInfo
        {
            Provider = Provider, Host = HostBox.Text.Trim(), Port = port, Database = DatabaseBox.Text.Trim(),
            UserName = UserBox.Text.Trim(), UseSsl = SslBox.IsChecked == true
        };
        info.Password = PasswordBox.Password;
        return info;
    }

    private void SetBusy(bool busy, string? text = null)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = busy ? text ?? "" : "";
        IsEnabled = !busy;
    }

    private static string Describe(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var info = ReadForm();
        if (info is null) return;
        SetBusy(true, "Test de la connexion…");
        try
        {
            var r = await Task.Run(() => new DbContextFactory(DbTarget.ForServer(info)).TestConnection(timeoutSeconds: 8));
            ResultText.Foreground = (System.Windows.Media.Brush)FindResource("BrushSuccessDark");
            ResultText.Text = $"✔ Connexion établie ({r.ServerInfo}). " +
                (!r.DatabaseExists
                    ? "La base n'existe pas encore : elle sera créée" + (r.CanCreateDatabase == false ? " — ATTENTION : cet utilisateur n'a pas le droit de créer des bases." : ".")
                    : r.HasAppTables ? "La base contient déjà des données Tout Compte Fait." : "La base est vide : les tables seront créées.");
        }
        catch (Exception ex)
        {
            ResultText.Foreground = (System.Windows.Media.Brush)FindResource("BrushDangerDark");
            ResultText.Text = "✖ " + Describe(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Migrate_Click(object sender, RoutedEventArgs e)
    {
        var info = ReadForm();
        if (info is null) return;
        if (!Ui.Confirm("Copier toutes les données actuelles vers le serveur indiqué ?\n\n" +
                        "La base actuelle n'est pas modifiée. Le serveur doit être vide (ou vous serez averti).", "Copier vers le serveur"))
            return;

        var progress = new Progress<string>(m => BusyText.Text = m);
        var replace = false;
        while (true)
        {
            SetBusy(true, "Copie en cours…");
            try
            {
                var source = App.Target;
                var destination = DbTarget.ForServer(info);
                var report = await Task.Run(() => DatabaseCopier.Copy(source, destination, App.DisplayVersion, replace, progress));
                SetBusy(false);
                ResultText.Foreground = (System.Windows.Media.Brush)FindResource(report.IsComplete ? "BrushSuccessDark" : "BrushDangerDark");
                ResultText.Text = report.IsComplete
                    ? $"✔ Copie terminée et vérifiée : {report.TotalRows} lignes dans {report.Tables.Count} tables."
                    : "⚠ La copie est incomplète : " + string.Join(", ", report.Tables.Where(t => t.SourceRows != t.DestinationRows).Select(t => t.Table));
                return;
            }
            catch (DatabaseCopier.DestinationNotEmptyException ex)
            {
                SetBusy(false);
                if (!Ui.Confirm(ex.Message + "\n\nREMPLACER le contenu du serveur par les données actuelles ?", "Le serveur contient déjà des données"))
                    return;
                replace = true;
            }
            catch (Exception ex)
            {
                SetBusy(false);
                ResultText.Foreground = (System.Windows.Media.Brush)FindResource("BrushDangerDark");
                ResultText.Text = "✖ " + Describe(ex);
                return;
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ServerMode.IsChecked == true)
        {
            var info = ReadForm();
            if (info is null) return;
            if (!Ui.Confirm($"Utiliser le serveur {info.Describe()} ?\n\nLe logiciel va redémarrer. Si la base du serveur est vide, elle sera initialisée " +
                            "(pensez à copier d'abord les données actuelles).", "Changer de base de données"))
                return;
            App.Settings.DatabaseServer = info;
            App.Settings.UseDatabaseServer = true;
        }
        else
        {
            if (!App.Settings.UseDatabaseServer) { Ui.Info("Le logiciel utilise déjà le fichier SQLite."); return; }
            if (!Ui.Confirm("Revenir au fichier SQLite ?\n\nLe logiciel va redémarrer.", "Changer de base de données")) return;
            App.Settings.UseDatabaseServer = false;
        }
        App.Settings.Save();
        App.Restart();
    }
}
