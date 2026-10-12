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
    /// <summary>Relit les donnÃ©es et reconstruit l'affichage.</summary>
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
        Title = Editions.AppName;
        if (!Editions.Current.HasMemberRefunds) NavMembers.Visibility = Visibility.Collapsed;
        if (Editions.Current.IsBusiness)
        {
            // Onglet Reporting (dÃ©claration URSSAF) : propre Ã  la version Autoentreprise.
            var nav = new RadioButton
            {
                Content = "ðŸ“ˆ   Reporting URSSAF", GroupName = "nav", Tag = "reporting",
                Style = (Style)FindResource("NavButton")
            };
            nav.Checked += Nav_Checked;
            if (NavMembers.Parent is Panel navPanel) navPanel.Children.Insert(navPanel.Children.IndexOf(NavMembers) + 1, nav);
        }
        CoreVersionText.Text = $"v{App.DisplayVersion} Â· {Editions.Current.VersionMention}";
        CopyrightText.Text = "Â© TomLine prod&co by Thomas Guidez  Â·  " + Editions.AppName;

        App.DatabaseReopened += OnDatabaseReopened;
        UserNameText.Text = App.Session.DisplayName;
        UserRoleText.Text = RoleInfo.DisplayName(App.Session.Role) + " Â· " + App.Session.UserName;
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

        // DÃ©connexion automatique : toute action souris / clavier remet le compteur Ã  zÃ©ro.
        System.Windows.Input.InputManager.Current.PreProcessInput += OnPreProcessInput;
        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();
        RefreshIdleTimeout();
        Closed += (_, _) =>
        {
            _watchTimer.Stop();
            _idleTimer.Stop();
            System.Windows.Input.InputManager.Current.PreProcessInput -= OnPreProcessInput;
        };
    }

    // ------------------------------------------------------------------ dÃ©connexion automatique

    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private int _idleMinutes;
    private DateTime _lastActivity = DateTime.UtcNow;
    private DateTime _lastIdleRead = DateTime.UtcNow;
    private bool _loggingOut;

    private void OnPreProcessInput(object sender, System.Windows.Input.PreProcessInputEventArgs e)
    {
        var t = e.StagingItem.Input;
        if (t is System.Windows.Input.MouseEventArgs or System.Windows.Input.KeyboardEventArgs or System.Windows.Input.TouchEventArgs)
            _lastActivity = DateTime.UtcNow;
    }

    /// <summary>Relit le dÃ©lai d'inactivitÃ© dÃ©fini par l'administrateur (rÃ©glage de la base, commun Ã  tous).</summary>
    public void RefreshIdleTimeout()
    {
        _lastIdleRead = DateTime.UtcNow;
        try { _idleMinutes = App.Compta.GetIdleTimeoutMinutes(); }
        catch { /* base momentanÃ©ment indisponible : on garde la valeur connue */ }
    }

    private void CheckIdle()
    {
        if (_loggingOut) return;
        // Un autre poste ou un administrateur peut avoir changÃ© le dÃ©lai : relecture chaque minute.
        if (DateTime.UtcNow - _lastIdleRead > TimeSpan.FromMinutes(1)) RefreshIdleTimeout();
        if (_idleMinutes <= 0) return;
        if (DateTime.UtcNow - _lastActivity < TimeSpan.FromMinutes(_idleMinutes)) return;
        _loggingOut = true;
        _idleTimer.Stop();
        // MÃªme effet que Â« Se dÃ©connecter Â» : mot de passe mÃ©morisÃ© oubliÃ©, retour Ã  l'Ã©cran de connexion.
        App.Settings.SetRememberedPassword(null);
        App.Settings.Save();
        App.Restart();
    }

    /// <summary>Signature du dernier changement de la base : date de la derniÃ¨re saisie enregistrÃ©e par le poste
    /// rÃ©dacteur (table DbInfo, pour SQLite comme pour les serveurs), sinon date du fichier.</summary>
    private static DateTime SafeWriteTime()
    {
        try
        {
            using var ctx = App.DbFactory.Create();
            if (ToutCompteFait.Core.Services.DatabaseWriteTracking.ReadLastWrite(ctx) is { } at) return at;
            if (!App.Target.IsServer) return File.GetLastWriteTimeUtc(App.Target.FilePath!);
        }
        catch { /* base momentanÃ©ment indisponible */ }
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
        DbModeText.Text = "Â· " + App.DatabaseModeLabel;
        var who = $"{App.Session.DisplayName} ({RoleInfo.DisplayName(App.Session.Role)})";
        if (App.IsReadOnly)
        {
            var reader = App.Session.Role == Role.Reader && App.WriteLockOwner is null;
            AccessFooter.Text = $"{who} Â· lecture seule" + (App.WriteLockOwner is null ? "" : $" Â· Ã©criture en cours : {App.WriteLockOwner}");
            AccessFooter.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            // Un lecteur n'a rien Ã  Â« reprendre Â» : le bandeau n'est affichÃ© que pour un poste qui devrait pouvoir Ã©crire.
            ReadOnlyBanner.Visibility = reader ? Visibility.Collapsed : Visibility.Visible;
            ReadOnlyText.Text = App.ReadOnlyMessage;
            TakeOverButton.Visibility = App.HasRole(Permission.WriteLedger) ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            AccessFooter.Text = $"{who} Â· accÃ¨s en Ã©criture";
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
        if (Ui.Confirm("Se dÃ©connecter ? Le logiciel va se relancer sur l'Ã©cran de connexion.\n" +
                       "Le mot de passe mÃ©morisÃ© sur ce poste, s'il y en a un, sera oubliÃ©.", "DÃ©connexion"))
        {
            App.Settings.SetRememberedPassword(null);
            App.Settings.Save();
            App.Restart();
        }
    }

    private void TakeOver_Click(object sender, RoutedEventArgs e)
    {
        if (App.TryTakeOverWrite())
            Ui.Info("Ce poste a maintenant l'accÃ¨s en Ã©criture.");
        else
            Ui.Info("L'Ã©criture est toujours dÃ©tenue par " + (App.WriteLockOwner ?? "un autre poste") +
                    ".\nCe poste reste en lecture seule ; rÃ©essayez quand l'autre poste aura fermÃ© le logiciel.");
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
            YearStatus.Text = $"ðŸ”’ ValidÃ© le {y.ValidatedAt:dd/MM/yyyy}" + (y.ValidatedBy is null ? "" : $" par {y.ValidatedBy}");
            YearStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
        }
        else
        {
            YearStatus.Text = "âœ Ouvert : saisie possible";
            YearStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        }
    }

    /// <summary>Recharge la liste des exercices, la barre latÃ©rale et la page affichÃ©e.</summary>
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

    /// <summary>SÃ©lectionne un exercice par son identifiant (aprÃ¨s crÃ©ation, par exemple).</summary>
    public void SelectYear(long yearId)
    {
        var y = App.Compta.Year(yearId);
        LoadYears(y?.Label);
    }

    private void NewYear_Click(object sender, RoutedEventArgs e) => CreateYearInteractive();

    private void PromptFirstYear()
    {
        Ui.Info("Bienvenue ! CommenÃ§ons par crÃ©er le premier exercice (la pÃ©riode comptable).\n\n" +
                "Vous pourrez ensuite saisir les soldes de dÃ©part des comptes dans Â« ParamÃ¨tres Â», puis importer vos relevÃ©s bancaires depuis le Livre.",
            Editions.AppName);
        CreateYearInteractive();
    }

    public void CreateYearInteractive()
    {
        if (!App.Can(Permission.ManageStructure)) { Ui.Error("Vous n'avez pas le droit de crÃ©er un exercice sur ce poste (rÃ´le ou lecture seule)."); return; }
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
            Ui.Info($"L'exercice {created.Label} est crÃ©Ã©.\n\n" +
                    (years.Count > 0
                        ? "Les listes de produits et de charges ont Ã©tÃ© recopiÃ©es depuis l'exercice prÃ©cÃ©dent, et les soldes d'ouverture ainsi que les reports sont repris automatiquement."
                        : "Pensez Ã  saisir les soldes de dÃ©part des comptes dans Â« ParamÃ¨tres Â»."));
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

    /// <summary>Le profil de l'entreprise a changÃ© : le reporting est recalculÃ© s'il est affichÃ©.</summary>
    public void RefreshBusinessNav()
    {
        if (_page == "reporting") ShowPage("reporting");
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
                "reporting" => new ReportingView(),
                "settings" => new SettingsView(y),
                _ => new LivreView(y)
            };
        Host.Content = view;
    }

    /// <summary>Mode diagnostic (--shots dossier) : enregistre une capture de chaque Ã©cran puis quitte.</summary>
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

        foreach (var page in new[] { "livre", "bilan", "reports", "members", "settings", "help" }.Concat(Editions.Current.IsBusiness ? new[] { "reporting" } : Array.Empty<string>()))
        {
            ShowPage(page);
            await Shot(this, page);
        }

        foreach (var tab in new[] { "Utilisateurs", "Base de donnÃ©es" }.Concat(Editions.Current.IsBusiness ? new[] { "Entreprise" } : Array.Empty<string>()))
        {
            ShowPage("settings");
            if (Host.Content is Views.SettingsView sv) sv.SelectTab(tab);
            await Shot(this, "settings-" + (tab == "Utilisateurs" ? "users" : tab == "Entreprise" ? "business" : "db"));
        }

        if (CurrentYear is { } yy && App.Can(Permission.WriteLedger))
        {
            // RelevÃ© de test : une opÃ©ration nouvelle, une opÃ©ration dÃ©jÃ  saisie (intitulÃ© Ã  mettre Ã  jour).
            var rows0 = App.Compta.Entries(yy.Id).Where(r => r.Entry.Kind == EntryKind.Normal && r.Entry.AccountId != null).ToList();
            var known = rows0.FirstOrDefault(r => r.Allocations.Count > 0);
            var csv = new System.Text.StringBuilder("Date;LibellÃ©;DÃ©bit;CrÃ©dit\n");
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

    /// <summary>Test de fumÃ©e de la saisie : remplit le formulaire d'Ã©criture comme un utilisateur et enregistre.</summary>
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
            log.AppendLine($"categories proposÃ©es : {combo.Items.Count}; montant ligne auto = Â« {amount.Text} Â»");
            combo.SelectedIndex = 0;
            log.AppendLine("bandeau : " + dlg.BalanceText.Text);
            var save = typeof(Dialogs.EntryWindow).GetMethod("Save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            try { save.Invoke(dlg, new object[] { false }); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            { /* DialogResult : la fenÃªtre n'est pas modale en mode diagnostic */ }
            log.AppendLine("erreur affichÃ©e : Â« " + dlg.ErrorText.Text + " Â»");
            dlg.Close();

            var after = c.Entries(y.Id);
            log.AppendLine($"Ã©critures avant {before}, aprÃ¨s {after.Count}");
            var last = after.FirstOrDefault(r => r.Entry.Label == "TEST CB CARREFOUR");
            log.AppendLine(last is null ? "ECHEC : Ã©criture non enregistrÃ©e"
                : $"OK : {last.Entry.Kind} {last.Entry.Direction} {Money.Format(last.Entry.AmountCents)} â€” ventilation {string.Join(" + ", last.Allocations.Select(a => Money.Format(a.AmountCents)))}");
            var b = c.ComputeBilan(y.Id);
            log.AppendLine($"bilan Ã©quilibrÃ© : {b.BalanceDifference == 0}, Ã©cart de ventilation : {Money.Format(b.UnallocatedGap)}");

            // Virement banque -> livret
            var dlg2 = new Dialogs.EntryWindow(y) { Owner = this };
            dlg2.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            dlg2.ModeTransfer.IsChecked = true;
            dlg2.LabelBox.Text = "TEST VIREMENT";
            dlg2.AmountBox.Text = "50";
            try { save.Invoke(dlg2, new object[] { false }); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
            log.AppendLine("virement, erreur : Â« " + dlg2.ErrorText.Text + " Â»");
            dlg2.Close();
            var tr = c.Entries(y.Id).FirstOrDefault(r => r.Entry.Label == "TEST VIREMENT");
            log.AppendLine(tr is null ? "ECHEC : virement non enregistrÃ©" : $"OK : {tr.Entry.Kind} {tr.Entry.AccountId} -> {tr.Entry.ToAccountId} {Money.Format(tr.Entry.AmountCents)}");
        }
        catch (Exception ex)
        {
            log.AppendLine("EXCEPTION : " + ex);
        }
        File.WriteAllText(Path.Combine(dir, "selftest.txt"), log.ToString());
    }

    /// <summary>Recharge uniquement la page affichÃ©e.</summary>
    public void ReloadPage()
    {
        if (Host.Content is IPage p) p.Reload();
    }
}








