using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Security;
using ToutCompteFait.Dialogs;
using ToutCompteFait.Views;

namespace ToutCompteFait;

public interface IPage
{
    /// <summary>Relit les données et reconstruit l'affichage.</summary>
    void Reload();
}

public partial class MainWindow : Window
{
    public static MainWindow? Current { get; private set; }

    private bool _loading;
    private string _page = "livre";
    private DateTime _lastDbWrite;
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public FiscalYear? CurrentYear { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        Title = "Tout Compte Fait";

        App.DatabaseReopened += OnDatabaseReopened;
        UserNameText.Text = App.Session.DisplayName;
        UserRoleText.Text = RoleInfo.DisplayName(App.Session.Role) + " · " + App.Session.UserName;
        Loaded += (_, _) =>
        {
            UpdateFooter();
            LoadYears(App.Settings.LastYear);
            if (CurrentYear is null && App.Can(Permission.ManageStructure)) PromptFirstYear();
        };

        // Poste en lecture seule : on surveille le fichier pour afficher les modifications de l'autre poste.
        _lastDbWrite = SafeWriteTime();
        _watchTimer.Tick += (_, _) => WatchDatabase();
        _watchTimer.Start();
        Closed += (_, _) => _watchTimer.Stop();
    }

    /// <summary>Signature du dernier changement de la base : date de la dernière saisie enregistrée par le poste
    /// rédacteur (table DbInfo, pour SQLite comme pour les serveurs), sinon date du fichier.</summary>
    private static DateTime SafeWriteTime()
    {
        try
        {
            using var ctx = App.DbFactory.Create();
            if (ToutCompteFait.Core.Services.DatabaseWriteTracking.ReadLastWrite(ctx) is { } at) return at;
            if (!App.Target.IsServer) return File.GetLastWriteTimeUtc(App.Target.FilePath!);
        }
        catch { /* base momentanément indisponible */ }
        return DateTime.MinValue;
    }

    private void WatchDatabase()
    {
        if (!App.IsReadOnly) return;
        var t = SafeWriteTime();
        if (t == _lastDbWrite) return;
        _lastDbWrite = t;
        // Ne perturbe pas une saisie en cours : seules les vues de consultation se rechargent toutes seules.
        if (OwnedWindows.Count > 0) return;
        Refresh();
    }
    private void OnDatabaseReopened()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _lastDbWrite = SafeWriteTime();
            UpdateFooter();
            LoadYears(CurrentYear?.Label);
        });
    }

    // ------------------------------------------------------------------ pied de page et bandeau

    private void UpdateFooter()
    {
        DbVersionText.Text = $"DB: V{App.DatabaseVersion}";
        DbModeText.Text = "· " + App.DatabaseModeLabel;
        var who = $"{App.Session.DisplayName} ({RoleInfo.DisplayName(App.Session.Role)})";
        if (App.IsReadOnly)
        {
            var reader = App.Session.Role == Role.Reader && App.WriteLockOwner is null;
            AccessFooter.Text = $"{who} · lecture seule" + (App.WriteLockOwner is null ? "" : $" · écriture en cours : {App.WriteLockOwner}");
            AccessFooter.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            // Un lecteur n'a rien à « reprendre » : le bandeau n'est affiché que pour un poste qui devrait pouvoir écrire.
            ReadOnlyBanner.Visibility = reader ? Visibility.Collapsed : Visibility.Visible;
            ReadOnlyText.Text = App.ReadOnlyMessage;
            TakeOverButton.Visibility = App.HasRole(Permission.WriteLedger) ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            AccessFooter.Text = $"{who} · accès en écriture";
            AccessFooter.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
            ReadOnlyBanner.Visibility = Visibility.Collapsed;
        }
        NewYearButton.IsEnabled = App.Can(Permission.ManageStructure);
        _ = App.DatabaseHealth.CheckNowAsync();
    }
    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.WithWriteAccess(c =>
            {
                new ChangePasswordWindow(c, forced: false) { Owner = this }.ShowDialog();
                return 0;
            });
        }
        catch (ComptaException ex)
        {
            Ui.Error(ex.Message);
        }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.Confirm("Se déconnecter ? Le logiciel va se relancer sur l'écran de connexion.\n" +
                       "Le mot de passe mémorisé sur ce poste, s'il y en a un, sera oublié.", "Déconnexion"))
        {
            App.Settings.SetRememberedPassword(null);
            App.Settings.Save();
            App.Restart();
        }
    }

    private void TakeOver_Click(object sender, RoutedEventArgs e)
    {
        if (App.TryTakeOverWrite())
            Ui.Info("Ce poste a maintenant l'accès en écriture.");
        else
            Ui.Info("L'écriture est toujours détenue par " + (App.WriteLockOwner ?? "un autre poste") +
                    ".\nCe poste reste en lecture seule ; réessayez quand l'autre poste aura fermé le logiciel.");
    }

    // ------------------------------------------------------------------ exercices

    private void LoadYears(string? selectLabel)
    {
        _loading = true;
        var years = App.Compta.Years();
        YearCombo.ItemsSource = years;
        FiscalYear? pick = years.FirstOrDefault(y => y.Label == selectLabel)
                           ?? years.LastOrDefault(y => y.Open)
                           ?? years.LastOrDefault();
        YearCombo.SelectedItem = pick;
        _loading = false;
        SetCurrentYear(pick);
    }

    private void YearCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SetCurrentYear(YearCombo.SelectedItem as FiscalYear);
    }

    private void SetCurrentYear(FiscalYear? y)
    {
        CurrentYear = y;
        if (y is not null)
        {
            App.Settings.LastYear = y.Label;
            App.Settings.Save();
        }
        UpdateYearChrome();
        ShowPage(_page);
    }

    private void UpdateYearChrome()
    {
        var y = CurrentYear;
        if (y is null)
        {
            YearStatus.Text = "Aucun exercice";
            YearStatus.Foreground = Brushes.Gray;
            YearPeriod.Text = "";
            return;
        }
        YearPeriod.Text = $"du {y.Start:dd/MM/yyyy} au {y.End:dd/MM/yyyy}";
        if (y.Validated)
        {
            YearStatus.Text = $"🔒 Validé le {y.ValidatedAt:dd/MM/yyyy}" + (y.ValidatedBy is null ? "" : $" par {y.ValidatedBy}");
            YearStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
        }
        else
        {
            YearStatus.Text = "✏ Ouvert : saisie possible";
            YearStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        }
    }

    /// <summary>Recharge la liste des exercices, la barre latérale et la page affichée.</summary>
    public void Refresh()
    {
        var label = CurrentYear?.Label;
        _loading = true;
        var years = App.Compta.Years();
        YearCombo.ItemsSource = years;
        var pick = years.FirstOrDefault(y => y.Label == label) ?? years.LastOrDefault();
        YearCombo.SelectedItem = pick;
        _loading = false;
        CurrentYear = pick;
        UpdateYearChrome();
        ShowPage(_page);
    }

    /// <summary>Sélectionne un exercice par son identifiant (après création, par exemple).</summary>
    public void SelectYear(long yearId)
    {
        var y = App.Compta.Year(yearId);
        LoadYears(y?.Label);
    }

    private void NewYear_Click(object sender, RoutedEventArgs e) => CreateYearInteractive();

    private void PromptFirstYear()
    {
        Ui.Info("Bienvenue ! Commençons par créer le premier exercice (la période comptable).\n\n" +
                "Vous pourrez ensuite saisir les soldes de départ des comptes dans « Paramètres », puis importer vos relevés bancaires depuis le Livre.",
            "Tout Compte Fait");
        CreateYearInteractive();
    }

    public void CreateYearInteractive()
    {
        if (!App.Can(Permission.ManageStructure)) { Ui.Error("Vous n'avez pas le droit de créer un exercice sur ce poste (rôle ou lecture seule)."); return; }
        var years = App.Compta.Years();
        DateOnly start, end;
        if (years.Count > 0)
        {
            var last = years[^1];
            start = last.End.AddDays(1);
            end = start.AddYears(1).AddDays(-1);
        }
        else
        {
            var today = DateTime.Today;
            var y = today.Month >= 9 ? today.Year : today.Year - 1;
            start = new DateOnly(y, 9, 1);
            end = new DateOnly(y + 1, 8, 31);
        }
        var dlg = new NewYearWindow(start, end) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        FiscalYear? created = null;
        if (Ui.Try(() => created = App.Compta.CreateYear(dlg.Start, dlg.End, cloneStructure: true, label: dlg.YearLabel)) && created is not null)
        {
            LoadYears(created.Label);
            Ui.Info($"L'exercice {created.Label} est créé.\n\n" +
                    (years.Count > 0
                        ? "Les listes de produits et de charges ont été recopiées depuis l'exercice précédent, et les soldes d'ouverture ainsi que les reports sont repris automatiquement."
                        : "Pensez à saisir les soldes de départ des comptes dans « Paramètres »."));
        }
    }

    // ------------------------------------------------------------------ navigation

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && IsLoaded)
        {
            _page = tag;
            ShowPage(tag);
        }
    }

    private void ShowPage(string tag)
    {
        _page = tag;
        var y = CurrentYear;
        UserControl? view = null;
        if (tag == "help") view = new HelpView();
        else if (y is null)
            view = new HelpView();
        else
            view = tag switch
            {
                "bilan" => new BilanView(y),
                "reports" => new ReportsView(y),
                "members" => new MembersView(y),
                "settings" => new SettingsView(y),
                _ => new LivreView(y)
            };
        Host.Content = view;
    }

    /// <summary>Mode diagnostic (--shots dossier) : enregistre une capture de chaque écran puis quitte.</summary>
    public async Task RunScreenshotsAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(600);

        async Task Shot(Window w, string name)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(250);
            var root = (FrameworkElement)w.Content;
            var width = (int)Math.Max(1, root.ActualWidth);
            var height = (int)Math.Max(1, root.ActualHeight);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }

        foreach (var page in new[] { "livre", "bilan", "reports", "members", "settings", "help" })
        {
            ShowPage(page);
            await Shot(this, page);
        }

        foreach (var tab in new[] { "Utilisateurs", "Base de données" })
        {
            ShowPage("settings");
            if (Host.Content is Views.SettingsView sv) sv.SelectTab(tab);
            await Shot(this, "settings-" + (tab == "Utilisateurs" ? "users" : "db"));
        }

        if (CurrentYear is { } yy && App.Can(Permission.WriteLedger))
        {
            // Relevé de test : une opération nouvelle, une opération déjà saisie (intitulé à mettre à jour).
            var rows0 = App.Compta.Entries(yy.Id).Where(r => r.Entry.Kind == EntryKind.Normal && r.Entry.AccountId != null).ToList();
            var known = rows0.FirstOrDefault(r => r.Allocations.Count > 0);
            var csv = new System.Text.StringBuilder("Date;Libellé;Débit;Crédit\n");
            if (known is not null)
            {
                var k = known.Entry;
                var amt = (k.AmountCents / 100m).ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
                csv.Append($"{k.Date:dd/MM/yyyy};LIBELLE BANQUE TEST;{(k.Direction == Direction.Out ? amt : "")};{(k.Direction == Direction.In ? amt : "")}\n");
            }
            var shifted = rows0.Where(r => r.Allocations.Count > 0 && r.Entry.Direction == Direction.Out && r.Entry != known?.Entry).Skip(7).FirstOrDefault();
            if (shifted is not null)
            {
                var s = shifted.Entry;
                var amt2 = (s.AmountCents / 100m).ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
                csv.Append($"{s.Date.AddDays(2):dd/MM/yyyy};CB DATE DECALEE TEST;{amt2};\n");
            }
            csv.Append($"{yy.Start.AddDays(40):dd/MM/yyyy};PRELEVEMENT NOUVEAU TEST;12,34;\n");
            var tmp = Path.Combine(dir, "releve-test.csv");
            File.WriteAllText(tmp, csv.ToString(), new System.Text.UTF8Encoding(false));
            ShowPage("livre");
            var bw = new Dialogs.BankImportWindow { Owner = this };
            bw.Show();
            bw.LoadFromFile(tmp);
            await Shot(bw, "bank-import");
            bw.Close();
        }

        if (CurrentYear is { } yf)
        {
            ShowPage("livre");
            if (Host.Content is Views.LivreView lv) lv.ShowOnlyToFill();
            await Shot(this, "livre-tofill");
        }

        if (CurrentYear is { } y)
        {
            ShowPage("livre");
            var dlg = new Dialogs.EntryWindow(y) { Owner = this };
            dlg.Show();
            await Shot(dlg, "entry-new");
            dlg.Close();
            var rows = App.Compta.Entries(y.Id);
            var sample = rows.FirstOrDefault(r => r.Allocations.Count > 1) ?? rows.FirstOrDefault();
            if (sample is not null)
            {
                var d2 = new Dialogs.EntryWindow(y, sample) { Owner = this };
                d2.Show();
                await Shot(d2, "entry-edit");
                d2.Close();
            }
        }
        if (App.RunSelfTest) await SelfTestAsync(dir);
        Application.Current.Shutdown();
    }

    /// <summary>Test de fumée de la saisie : remplit le formulaire d'écriture comme un utilisateur et enregistre.</summary>
    private async Task SelfTestAsync(string dir)
    {
        var log = new System.Text.StringBuilder();
        try
        {
            var y = CurrentYear!;
            var c = App.Compta;
            var before = c.Entries(y.Id).Count;
            var dlg = new Dialogs.EntryWindow(y) { Owner = this };
            dlg.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            dlg.LabelBox.Text = "TEST CB CARREFOUR";
            dlg.AmountBox.Text = "123,45";
            var row = (Grid)dlg.AllocPanel.Children[0];
            var combo = (ComboBox)row.Children[0];
            var amount = (TextBox)row.Children[1];
            log.AppendLine($"categories proposées : {combo.Items.Count}; montant ligne auto = « {amount.Text} »");
            combo.SelectedIndex = 0;
            log.AppendLine("bandeau : " + dlg.BalanceText.Text);
            var save = typeof(Dialogs.EntryWindow).GetMethod("Save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            try { save.Invoke(dlg, new object[] { false }); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            { /* DialogResult : la fenêtre n'est pas modale en mode diagnostic */ }
            log.AppendLine("erreur affichée : « " + dlg.ErrorText.Text + " »");
            dlg.Close();

            var after = c.Entries(y.Id);
            log.AppendLine($"écritures avant {before}, après {after.Count}");
            var last = after.FirstOrDefault(r => r.Entry.Label == "TEST CB CARREFOUR");
            log.AppendLine(last is null ? "ECHEC : écriture non enregistrée"
                : $"OK : {last.Entry.Kind} {last.Entry.Direction} {Money.Format(last.Entry.AmountCents)} — ventilation {string.Join(" + ", last.Allocations.Select(a => Money.Format(a.AmountCents)))}");
            var b = c.ComputeBilan(y.Id);
            log.AppendLine($"bilan équilibré : {b.BalanceDifference == 0}, écart de ventilation : {Money.Format(b.UnallocatedGap)}");

            // Virement banque -> livret
            var dlg2 = new Dialogs.EntryWindow(y) { Owner = this };
            dlg2.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            dlg2.ModeTransfer.IsChecked = true;
            dlg2.LabelBox.Text = "TEST VIREMENT";
            dlg2.AmountBox.Text = "50";
            try { save.Invoke(dlg2, new object[] { false }); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
            log.AppendLine("virement, erreur : « " + dlg2.ErrorText.Text + " »");
            dlg2.Close();
            var tr = c.Entries(y.Id).FirstOrDefault(r => r.Entry.Label == "TEST VIREMENT");
            log.AppendLine(tr is null ? "ECHEC : virement non enregistré" : $"OK : {tr.Entry.Kind} {tr.Entry.AccountId} -> {tr.Entry.ToAccountId} {Money.Format(tr.Entry.AmountCents)}");
        }
        catch (Exception ex)
        {
            log.AppendLine("EXCEPTION : " + ex);
        }
        File.WriteAllText(Path.Combine(dir, "selftest.txt"), log.ToString());
    }

    /// <summary>Recharge uniquement la page affichée.</summary>
    public void ReloadPage()
    {
        if (Host.Content is IPage p) p.Reload();
    }
}








