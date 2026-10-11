using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ComptaAepby.Core;
using ComptaAepby.Core.Import;
using ComptaAepby.Core.Security;
using ComptaAepby.Core.Services;
using ComptaAepby.Dialogs;
using Microsoft.EntityFrameworkCore;

namespace ComptaAepby.Views;

public sealed class CategoryRow
{
    public required Category Cat { get; init; }
    public string Name => Cat.Name;
    public string Operation { get; init; } = "";
    public int Uses { get; init; }
}

public sealed class AccountRow
{
    public required Account Account { get; init; }
    public string Name => Account.Name;
    public string KindText { get; init; } = "";
    public string ActiveText { get; init; } = "";
    public string OpeningText { get; init; } = "";
    public string ClosingText { get; init; } = "";
}

public sealed class UserRow
{
    public required AppUser User { get; init; }
    public string UserName => User.UserName;
    public string DisplayName => User.DisplayName;
    public string RoleText => User.RoleText;
    public string StateText { get; init; } = "";
    public string LastLoginText { get; init; } = "";
    public string CreatedBy => User.CreatedBy ?? "";
}

public partial class SettingsView : UserControl, IPage
{
    private readonly FiscalYear _year;
    private readonly Compta _c = App.Compta;
    private readonly bool _canEdit;
    private readonly bool _canAccounts;
    private bool _loading;

    public SettingsView(FiscalYear year)
    {
        InitializeComponent();
        InitUpdates();
        _year = year;
        _canEdit = App.Can(Permission.ManageStructure) && year.Open;
        _canAccounts = App.Can(Permission.ManageStructure);
        TitleText.Text = $"Paramètres — exercice {year.Label}";
        LockText.Text = year.Validated
            ? "🔒 Exercice validé : les listes, opérations et reports sont verrouillés."
            : !App.HasRole(Permission.ManageStructure) ? "Votre rôle (" + RoleInfo.DisplayName(App.Session.Role) + ") donne un accès en consultation seule à ces réglages."
            : App.IsReadOnly ? "Poste en lecture seule : aucune modification possible." : "";

        foreach (var b in new Button[] { AddProduct, RenameProduct, UpProduct, DownProduct, DeleteProduct,
                                         AddCharge, RenameCharge, UpCharge, DownCharge, DeleteCharge,
                                         SavePeriodButton, DeleteYearButton })
            b.IsEnabled = _canEdit;
        AddAccountButton.IsEnabled = _canAccounts;
        EditAccountButton.IsEnabled = _canAccounts;
        DeleteAccountButton.IsEnabled = _canAccounts;
        StartPicker.IsEnabled = _canEdit;
        EndPicker.IsEnabled = _canEdit;
        YearLabelBox.IsEnabled = _canEdit;

        // Onglets réservés à l'administrateur.
        UsersTab.Visibility = App.HasRole(Permission.ManageUsers) ? Visibility.Visible : Visibility.Collapsed;
        DbTab.Visibility = App.HasRole(Permission.ManageDatabase) ? Visibility.Visible : Visibility.Collapsed;
        ResetDataButton.IsEnabled = App.Can(Permission.ManageDatabase);
        ServerButton.IsEnabled = App.HasRole(Permission.ManageDatabase);
        ChangeDbButton.IsEnabled = App.HasRole(Permission.ManageDatabase);
        BackupNowButton.IsEnabled = App.Can(Permission.ManageDatabase);
        RestoreButton.IsEnabled = App.Can(Permission.ManageDatabase);

        _loading = true;
        FrequencyBox.ItemsSource = new[]
        {
            new FrequencyItem(BackupSchedule.Daily, "Tous les jours"),
            new FrequencyItem(BackupSchedule.Weekly, "Toutes les semaines"),
            new FrequencyItem(BackupSchedule.Monthly, "Tous les mois")
        };
        _loading = false;
        Reload();
    }

    /// <summary>Sélectionne un onglet par son titre (mode diagnostic).</summary>
    public void SelectTab(string header)
    {
        foreach (var t in Tabs.Items.OfType<TabItem>())
            if (string.Equals(t.Header as string, header, StringComparison.Ordinal)) Tabs.SelectedItem = t;
    }

    private sealed record FrequencyItem(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    public void Reload()
    {
        _loading = true;
        var cats = _c.Categories(_year.Id);
        var ops = _c.Operations(_year.Id);
        var opName = ops.ToDictionary(o => o.Id, o => o.Name);
        var uses = new Dictionary<long, int>();
        foreach (var r in _c.Entries(_year.Id))
            foreach (var a in r.Allocations) uses[a.CategoryId] = uses.GetValueOrDefault(a.CategoryId) + 1;
        foreach (var a in _c.Adjustments(_year.Id))
            if (a.CategoryId is { } id) uses[id] = uses.GetValueOrDefault(id) + 1;

        CategoryRow Row(Category c) => new()
        {
            Cat = c,
            Operation = c.OperationId is { } o && opName.TryGetValue(o, out var n) ? n : "",
            Uses = uses.GetValueOrDefault(c.Id)
        };
        ProductsGrid.ItemsSource = cats.Where(c => c.Kind == CategoryKind.Product).Select(Row).ToList();
        ChargesGrid.ItemsSource = cats.Where(c => c.Kind == CategoryKind.Charge).Select(Row).ToList();

        var selOp = (OperationsBox.SelectedItem as Operation)?.Id;
        OperationsBox.ItemsSource = ops;
        OperationsBox.SelectedItem = ops.FirstOrDefault(o => o.Id == selOp) ?? ops.FirstOrDefault();
        BuildOperationLinks();

        // Comptes bancaires
        var autoOpening = _c.OpeningIsAutomatic(_year.Id);
        AccountsGrid.ItemsSource = _c.Accounts(includeInactive: true).Select(a => new AccountRow
        {
            Account = a,
            KindText = Labels.Of(a.Kind),
            ActiveText = a.Active ? "Oui" : "Non",
            OpeningText = Money.Format(_c.Opening(_year.Id, a.Id)) + (autoOpening ? "  (auto)" : ""),
            ClosingText = Money.Format(_c.Closing(_year.Id, a.Id))
        }).ToList();
        OpeningButton.IsEnabled = _canEdit && !autoOpening;

        // Exercice
        StartPicker.SelectedDate = _year.Start.ToDateTime(TimeOnly.MinValue);
        EndPicker.SelectedDate = _year.End.ToDateTime(TimeOnly.MinValue);
        YearLabelBox.Text = _year.Label;
        YearInfo.Text = _year.Validated
            ? $"Exercice validé le {_year.ValidatedAt:dd/MM/yyyy HH:mm} par {_year.ValidatedBy}. Il ne peut plus être modifié."
            : "Exercice ouvert. Il se valide depuis le menu « Bilan et opérations ».";

        if (App.HasRole(Permission.ManageUsers)) LoadUsers();
        if (App.HasRole(Permission.ManageDatabase)) LoadDatabasePanel();
        _loading = false;
    }

    // ------------------------------------------------------------------ catégories

    private DataGrid GridFor(object sender) => (sender as FrameworkElement)?.Tag as string == "P" ? ProductsGrid : ChargesGrid;
    private static CategoryKind KindOf(object sender) =>
        (sender as FrameworkElement)?.Tag as string == "P" ? CategoryKind.Product : CategoryKind.Charge;

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var kind = KindOf(sender);
        var name = PromptWindow.Ask(Window.GetWindow(this), kind == CategoryKind.Product ? "Nouveau produit" : "Nouvelle charge",
            "Nom :", okText: "Ajouter");
        if (name is null) return;
        if (Ui.Try(() => _c.AddCategory(_year.Id, kind, name))) Reload();
    }

    private void RenameCategory_Click(object sender, RoutedEventArgs e)
    {
        if (GridFor(sender).SelectedItem is not CategoryRow row) { Ui.Info("Sélectionnez d'abord une ligne."); return; }
        var name = PromptWindow.Ask(Window.GetWindow(this), "Renommer", "Nouveau nom :", row.Cat.Name);
        if (name is null) return;
        if (Ui.Try(() => _c.RenameCategory(row.Cat.Id, name))) Reload();
    }

    private void UpCategory_Click(object sender, RoutedEventArgs e) => Move(sender, -1);
    private void DownCategory_Click(object sender, RoutedEventArgs e) => Move(sender, 1);

    private void Move(object sender, int delta)
    {
        var grid = GridFor(sender);
        if (grid.SelectedItem is not CategoryRow row) return;
        var id = row.Cat.Id;
        if (!Ui.Try(() => _c.MoveCategory(id, delta))) return;
        Reload();
        grid.SelectedItem = grid.Items.OfType<CategoryRow>().FirstOrDefault(r => r.Cat.Id == id);
    }

    private void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (GridFor(sender).SelectedItem is not CategoryRow row) { Ui.Info("Sélectionnez d'abord une ligne."); return; }
        if (!Ui.Confirm($"Supprimer « {row.Cat.Name} » ?")) return;
        if (Ui.Try(() => _c.DeleteCategory(row.Cat.Id))) Reload();
    }

    // ------------------------------------------------------------------ opérations

    private void AddOperation_Click(object sender, RoutedEventArgs e)
    {
        if (!_canEdit) return;
        var name = PromptWindow.Ask(Window.GetWindow(this), "Nouvelle opération", "Nom de l'opération (ex. Loto Octobre) :", okText: "Créer");
        if (name is null) return;
        if (Ui.Try(() => _c.AddOperation(_year.Id, name))) Reload();
    }

    private void RenameOperation_Click(object sender, RoutedEventArgs e)
    {
        if (!_canEdit || OperationsBox.SelectedItem is not Operation op) return;
        var name = PromptWindow.Ask(Window.GetWindow(this), "Renommer l'opération", "Nouveau nom :", op.Name);
        if (name is null) return;
        if (Ui.Try(() => _c.RenameOperation(op.Id, name))) Reload();
    }

    private void DeleteOperation_Click(object sender, RoutedEventArgs e)
    {
        if (!_canEdit || OperationsBox.SelectedItem is not Operation op) return;
        if (!Ui.Confirm($"Supprimer l'opération « {op.Name} » ?\nLes produits et charges ne sont pas supprimés, seulement déliés.")) return;
        if (Ui.Try(() => _c.DeleteOperation(op.Id))) Reload();
    }

    private void OperationsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) BuildOperationLinks();
    }

    private void BuildOperationLinks()
    {
        OperationLinks.Children.Clear();
        if (OperationsBox.SelectedItem is not Operation op)
        {
            OperationLinksTitle.Text = "Créez une opération pour y lier des produits et des charges.";
            return;
        }
        OperationLinksTitle.Text = $"Produits et charges liés à « {op.Name} » (cochez pour lier) :";
        var cats = _c.Categories(_year.Id);
        var opName = _c.Operations(_year.Id).ToDictionary(o => o.Id, o => o.Name);

        void Section(string title, CategoryKind kind)
        {
            OperationLinks.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
            foreach (var c in cats.Where(x => x.Kind == kind))
            {
                var elsewhere = c.OperationId is { } o && o != op.Id && opName.TryGetValue(o, out var n) ? n : null;
                var cb = new CheckBox
                {
                    Content = c.Name + (elsewhere is null ? "" : $"   (actuellement dans « {elsewhere} »)"),
                    IsChecked = c.OperationId == op.Id,
                    IsEnabled = _canEdit,
                    Margin = new Thickness(0, 3, 0, 3),
                    Tag = c
                };
                cb.Click += LinkClick;
                OperationLinks.Children.Add(cb);
            }
        }
        Section("Produits", CategoryKind.Product);
        Section("Charges", CategoryKind.Charge);
    }

    private void LinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: Category c } cb || OperationsBox.SelectedItem is not Operation op) return;
        var link = cb.IsChecked == true;
        if (Ui.Try(() => _c.SetCategoryOperation(c.Id, link ? op.Id : null))) Reload();
        else cb.IsChecked = !link;
    }

    // ------------------------------------------------------------------ comptes bancaires

    private AccountRow? SelectedAccount => AccountsGrid.SelectedItem as AccountRow;

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AccountWindow(null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) Reload();
    }

    private void EditAccount_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount is not { } row) { Ui.Info("Sélectionnez d'abord un compte."); return; }
        var dlg = new AccountWindow(row.Account) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) Reload();
    }

    private void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount is not { } row) { Ui.Info("Sélectionnez d'abord un compte."); return; }
        if (!Ui.Confirm($"Supprimer le compte « {row.Name} » ?")) return;
        if (Ui.Try(() => _c.DeleteAccount(row.Account.Id))) Reload();
    }

    private void Opening_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount is not { } row) { Ui.Info("Sélectionnez d'abord un compte."); return; }
        var text = PromptWindow.Ask(Window.GetWindow(this), "Solde d'ouverture",
            $"Solde du compte « {row.Name} » au premier jour de l'exercice {_year.Label} (en €) :",
            Money.Format(_c.Opening(_year.Id, row.Account.Id)));
        if (text is null) return;
        if (!Money.TryParse(text, out var cents)) { Ui.Error("Montant invalide."); return; }
        if (Ui.Try(() => _c.SetOpening(_year.Id, row.Account.Id, cents))) Reload();
    }

    // ------------------------------------------------------------------ exercice

    private void SavePeriod_Click(object sender, RoutedEventArgs e)
    {
        if (StartPicker.SelectedDate is not { } s || EndPicker.SelectedDate is not { } en) { Ui.Error("Renseignez les deux dates."); return; }
        if (Ui.Try(() => _c.SetYearPeriod(_year.Id, DateOnly.FromDateTime(s), DateOnly.FromDateTime(en), YearLabelBox.Text)))
        {
            Ui.Info("Période enregistrée.");
            MainWindow.Current?.Refresh();
        }
    }

    private void DeleteYear_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm($"Supprimer l'exercice {_year.Label} ? (possible seulement s'il ne contient aucune écriture)")) return;
        if (Ui.Try(() => _c.DeleteYear(_year.Id)))
            MainWindow.Current?.Refresh();
    }

    // ------------------------------------------------------------------ utilisateurs

    private void LoadUsers()
    {
        UsersGrid.ItemsSource = _c.Users().Select(u => new UserRow
        {
            User = u,
            StateText = (u.Active ? "Actif" : "Désactivé") + (u.MustChangePassword ? " · mot de passe provisoire" : ""),
            LastLoginText = u.LastLoginAt is { } d ? d.ToString("dd/MM/yyyy HH:mm") : "jamais"
        }).ToList();
    }

    private UserRow? SelectedUser => UsersGrid.SelectedItem as UserRow;

    private void AddUser_Click(object sender, RoutedEventArgs e)
    {
        if (App.IsReadOnly) { Ui.Error("Ce poste est en lecture seule : un autre poste détient l'écriture."); return; }
        var dlg = new UserWindow(UserWindow.UserMode.Create) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) Reload();
    }

    private void EditUser_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUser is not { } row) { Ui.Info("Sélectionnez d'abord un utilisateur."); return; }
        if (App.IsReadOnly) { Ui.Error("Ce poste est en lecture seule : un autre poste détient l'écriture."); return; }
        var dlg = new UserWindow(UserWindow.UserMode.Edit, row.User) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) Reload();
    }

    private void UsersGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedUser is not null) EditUser_Click(sender, e);
    }

    private void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedUser is not { } row) { Ui.Info("Sélectionnez d'abord un utilisateur."); return; }
        if (App.IsReadOnly) { Ui.Error("Ce poste est en lecture seule : un autre poste détient l'écriture."); return; }
        var dlg = new UserWindow(UserWindow.UserMode.ResetPassword, row.User) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            Ui.Info($"Mot de passe provisoire enregistré pour « {row.UserName} ». Communiquez-le lui : il devra le changer à sa prochaine connexion.");
            Reload();
        }
    }

    // ------------------------------------------------------------------ base de données

    private void LoadDatabasePanel()
    {
        DbDescription.Text = $"{App.DatabaseModeLabel} — {App.DatabasePath}";
        string version = "?", last = "—";
        try
        {
            version = App.DbFactory.GetDatabaseVersion().ToString();
            using var ctx = App.DbFactory.Create();
            if (DatabaseWriteTracking.ReadLastWrite(ctx) is { } at) last = at.ToString("dd/MM/yyyy HH:mm");
        }
        catch { /* base momentanément indisponible */ }
        DbDetails.Text = $"Version du schéma : {version} · dernière saisie enregistrée : {last} · " +
                         (App.IsReadOnly ? "ce poste est en lecture seule" : "ce poste a l'accès en écriture");

        BackupsGrid.ItemsSource = BackupService.List();

        var s = App.Settings;
        AutoBackupBox.IsChecked = s.AutoBackupEnabled;
        FrequencyBox.SelectedItem = FrequencyBox.Items.OfType<FrequencyItem>().FirstOrDefault(f => f.Key == s.AutoBackupFrequency)
                                    ?? FrequencyBox.Items.OfType<FrequencyItem>().First();
        BackupTimeBox.Text = s.AutoBackupTime;
        KeepBox.Text = s.MaxBackupsToKeep.ToString();
        UpdateAutoBackupInfo();

        AuditGrid.ItemsSource = _c.AuditLog(300).Select(l => new
        {
            At = l.At.ToString("dd/MM/yyyy HH:mm"), l.User, l.Action, l.Detail
        }).ToList();
    }

    private void UpdateAutoBackupInfo()
    {
        var s = App.Settings;
        AutoBackupInfo.Text = !s.AutoBackupEnabled
            ? "Sauvegarde automatique désactivée."
            : $"Prochaine sauvegarde : {BackupSchedule.Next(s, DateTime.Now):dddd dd/MM/yyyy à HH:mm}" +
              (s.AutoBackupLastRun is { } d ? $" · dernière : {d:dd/MM/yyyy HH:mm}" : "") +
              " · réalisée par le poste qui a l'accès en écriture, dossier « Sauvegardes ».";
    }

    private void AutoBackup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.AutoBackupEnabled = AutoBackupBox.IsChecked == true;
        App.Settings.Save();
        UpdateAutoBackupInfo();
    }

    private void AutoBackup_Selection(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FrequencyBox.SelectedItem is not FrequencyItem f) return;
        App.Settings.AutoBackupFrequency = f.Key;
        App.Settings.Save();
        UpdateAutoBackupInfo();
    }

    private void BackupTime_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (TimeSpan.TryParseExact(BackupTimeBox.Text.Trim(), @"hh\:mm", null, out var t) && t < TimeSpan.FromDays(1))
            App.Settings.AutoBackupTime = t.ToString(@"hh\:mm");
        BackupTimeBox.Text = App.Settings.AutoBackupTime;
        App.Settings.Save();
        UpdateAutoBackupInfo();
    }

    private void Keep_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(KeepBox.Text.Trim(), out var n) && n >= 0) App.Settings.MaxBackupsToKeep = n;
        KeepBox.Text = App.Settings.MaxBackupsToKeep.ToString();
        BackupService.MaxToKeep = App.Settings.MaxBackupsToKeep;
        App.Settings.Save();
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Cursor = Cursors.Wait;
            var path = await App.AutoBackup.BackupNowAsync("manuelle");
            Cursor = null;
            LoadDatabasePanel();
            Ui.Info("Sauvegarde créée :\n" + path);
        }
        catch (Exception ex)
        {
            Cursor = null;
            Ui.Error("Sauvegarde impossible :\n" + ex.Message);
        }
    }

    private void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupEntry b) { Ui.Info("Sélectionnez d'abord une sauvegarde."); return; }
        if (!Ui.Confirm($"Supprimer la sauvegarde du {b.DateText} ?")) return;
        try { BackupService.Delete(b.Path); }
        catch (IOException ex) { Ui.Error(ex.Message); }
        LoadDatabasePanel();
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = DbContextFactory.BackupDirectory, UseShellExecute = true }); }
        catch (Exception ex) { Ui.Error(ex.Message); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsGrid.SelectedItem is not BackupEntry b) { Ui.Info("Sélectionnez d'abord une sauvegarde."); return; }
        if (!Ui.Confirm(
                $"Restaurer la sauvegarde du {b.DateText} ?\n\n" +
                "TOUTES les données actuelles seront remplacées par celles de la sauvegarde (une sauvegarde de l'état actuel est faite avant). " +
                "Le logiciel redémarrera.", "Restaurer une sauvegarde"))
            return;
        try
        {
            Cursor = Cursors.Wait;
            var safety = await Task.Run(() => App.DbFactory.CreateBackup("avant-restauration"));
            var bak = b.Path;
            if (App.Target.IsServer)
            {
                await Task.Run(() => DatabaseCopier.Copy(DbTarget.ForFile(bak), App.Target, App.DisplayVersion, replaceDestination: true));
            }
            else
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                File.Copy(bak, App.Target.FilePath!, overwrite: true);
            }
            Cursor = null;
            Ui.Info("Sauvegarde restaurée. L'état précédent est conservé dans :\n" + safety + "\n\nLe logiciel va redémarrer.");
            App.Restart();
        }
        catch (Exception ex)
        {
            Cursor = null;
            Ui.Error("Restauration impossible :\n" + ex.Message);
        }
    }

    private void Server_Click(object sender, RoutedEventArgs e)
    {
        new DatabaseServerWindow { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void ChangeDb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir (ou saisir le nom d'une nouvelle) base de données SQLite",
            Filter = "Base Tout Compte Fait (*.db)|*.db|Tous les fichiers (*.*)|*.*",
            CheckFileExists = false
        };
        if (!App.Target.IsServer)
        {
            dlg.FileName = Path.GetFileName(App.Target.FilePath);
            dlg.InitialDirectory = Path.GetDirectoryName(App.Target.FilePath);
        }
        if (dlg.ShowDialog() != true) return;
        var path = dlg.FileName;
        try
        {
            if (!File.Exists(path))
            {
                if (!Ui.Confirm($"Ce fichier n'existe pas.\nCréer une nouvelle base vide ici ?\n\n{path}")) return;
                new DbContextFactory(path).EnsureDatabaseUpToDate(App.DisplayVersion);
            }
            if (!Ui.Confirm("Utiliser ce fichier ?\n\n" + path + "\n\nLe logiciel va redémarrer.")) return;
            App.Settings.UseDatabaseServer = false;
            App.Settings.DatabasePath = path;
            App.Settings.Save();
            App.Restart();
        }
        catch (Exception ex)
        {
            Ui.Error("Impossible d'utiliser ce fichier :\n" + ex.Message);
        }
    }

    private void ExportDb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exporter une copie de la base",
            Filter = "Base Tout Compte Fait (*.db)|*.db",
            FileName = $"compta-copie-{DateTime.Now:yyyyMMdd-HHmm}.db"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
            App.DbFactory.ExportTo(dlg.FileName);
            Ui.Info("Copie enregistrée :\n" + dlg.FileName);
        }
        catch (Exception ex)
        {
            Ui.Error("Copie impossible :\n" + ex.Message);
        }
    }

    private async void SqlScript_Click(object sender, RoutedEventArgs e)
    {
        if (App.IsReadOnly) { Ui.Error("Ce poste est en lecture seule : un autre poste détient l'écriture."); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir le script SQL à exécuter",
            Filter = "Scripts SQL (*.sql)|*.sql|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        string script;
        try { script = File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8); }
        catch (Exception ex) { Ui.Error("Lecture du fichier impossible :\n" + ex.Message); return; }
        if (!Ui.Confirm(
                $"Exécuter « {Path.GetFileName(dlg.FileName)} » ({script.Length / 1024 + 1} Ko) sur la base actuelle ?\n\n" +
                "Selon le script, les données actuelles peuvent être REMPLACÉES. Une sauvegarde de l'état actuel est faite avant ; " +
                "en cas d'erreur, rien n'est modifié. Le logiciel redémarrera.", "Exécuter un script SQL"))
            return;
        try
        {
            Cursor = Cursors.Wait;
            var safety = await Task.Run(() => App.DbFactory.CreateBackup("avant-script-sql"));
            await Task.Run(() => SqlScriptRunner.Run(App.DbFactory, script));
            Cursor = null;
            Ui.Info("Script exécuté. L'état précédent est conservé dans :\n" + safety + "\n\nLe logiciel va redémarrer.");
            App.Restart();
        }
        catch (Exception ex)
        {
            Cursor = null;
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            Ui.Error("Le script a échoué, la base n'a pas été modifiée :\n" + inner.Message);
        }
    }

    private async void ResetData_Click(object sender, RoutedEventArgs e)
    {
        if (App.IsReadOnly) { Ui.Error("Ce poste est en lecture seule : un autre poste détient l'écriture."); return; }
        if (App.Session.Role != ComptaAepby.Core.Security.Role.Admin) { Ui.Error("Réservé aux administrateurs."); return; }
        if (!Ui.Confirm(
                "VIDER la base de données ?\n\n" +
                "Tous les exercices, comptes bancaires, écritures, ventilations, justificatifs et reports seront SUPPRIMÉS, " +
                "y compris les exercices validés. Les comptes utilisateurs et le journal sont conservés.\n\n" +
                "Une sauvegarde est faite avant. Le logiciel redémarrera.", "Vider la base de données"))
            return;
        var word = PromptWindow.Ask(Window.GetWindow(this), "Confirmation",
            "Pour confirmer, tapez le mot VIDER (en majuscules) :", okText: "Vider la base");
        if (word is null || word.Trim() != "VIDER") { Ui.Info("Confirmation incorrecte : rien n'a été modifié."); return; }
        try
        {
            Cursor = Cursors.Wait;
            string safety;
            try { safety = await Task.Run(() => App.DbFactory.CreateBackup("avant-vidage")); }
            catch (Exception ex)
            {
                Cursor = null;
                var inner = ex;
                while (inner.InnerException is not null) inner = inner.InnerException;
                Ui.Error("Sauvegarde préalable impossible : opération annulée, rien n'a été modifié.\n" + inner.Message);
                return;
            }
            await Task.Run(() => _c.ResetDatabaseData());
            Cursor = null;
            Ui.Info("La base a été vidée (les comptes utilisateurs sont conservés).\nL'état précédent est sauvegardé dans :\n" + safety +
                    "\n\nLe logiciel va redémarrer.");
            App.Restart();
        }
        catch (Exception ex)
        {
            Cursor = null;
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            Ui.Error("Le vidage a échoué, la base n'a pas été modifiée :\n" + inner.Message);
        }
    }
}
