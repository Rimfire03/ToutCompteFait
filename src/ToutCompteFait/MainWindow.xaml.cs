using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Security;
using ToutCompteFait.Core.Services;
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
        Title = Editions.AppName;
        if (!Editions.Current.HasMemberRefunds) NavMembers.Visibility = Visibility.Collapsed;
        if (Editions.Current.IsBusiness)
        {
            // Onglet Reporting (déclaration URSSAF) : propre à la version Autoentreprise.
            var nav = new RadioButton
            {
                Content = "📈   Reporting URSSAF", GroupName = "nav", Tag = "reporting",
                Style = (Style)FindResource("NavButton")
            };
            nav.Checked += Nav_Checked;
            if (NavMembers.Parent is Panel navPanel) navPanel.Children.Insert(navPanel.Children.IndexOf(NavMembers) + 1, nav);
        }
        // Onglet Immobilisations : visible seulement si la fonction est activée dans les paramètres.
        _navAssets = new RadioButton
        {
            Content = "🏭   Immobilisations", GroupName = "nav", Tag = "assets",
            Style = (Style)FindResource("NavButton"), Visibility = Visibility.Collapsed
        };
        _navAssets.Checked += Nav_Checked;
        if (NavMembers.Parent is Panel assetsPanel)
            assetsPanel.Children.Insert(assetsPanel.Children.IndexOf(NavMembers) + 1, _navAssets);
        RefreshAssetsNav();
        // Onglet Statistique (produits, charges, opérations) : juste après les remboursements des membres.
        var navCompare = new RadioButton
        {
            Content = "📉   Statistiques", GroupName = "nav", Tag = "compare",
            Style = (Style)FindResource("NavButton")
        };
        navCompare.Checked += Nav_Checked;
        if (NavMembers.Parent is Panel comparePanel)
            comparePanel.Children.Insert(comparePanel.Children.IndexOf(NavMembers) + 1, navCompare);
        // Centre de notifications : la cloche est dans le XAML ; le comptage est mis à jour régulièrement.
        _navNotif = NavNotif;
        DemoBanner.Visibility = App.Settings.DemoMode ? Visibility.Visible : Visibility.Collapsed;
        var notifTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        notifTimer.Tick += (_, _) => RefreshNotificationCount();
        notifTimer.Start();
        FillConfigurations();
        CoreVersionText.Text = $"v{App.DisplayVersion} · {Editions.Current.VersionMention}";
        CopyrightText.Text = "© TomLine prod&co by Thomas Guidez  ·  " + Editions.AppName;

        App.DatabaseReopened += OnDatabaseReopened;
        UserNameText.Text = App.Session.DisplayName;
        UserRoleText.Text = RoleInfo.DisplayName(App.Session.Role) + (App.Session.IsDeveloper ? " + Développeur" : "") + " · " + App.Session.UserName;
        Loaded += (_, _) =>
        {
            UpdateFooter();
            LoadYears(App.Settings.LastYear);
            ApplyNavigationPreferences();
            OpenStartPage();
            if (CurrentYear is null && App.Can(Permission.ManageStructure)) PromptFirstYear();
            Dispatcher.BeginInvoke(new Action(ShowDevLogIfNeeded), DispatcherPriority.ApplicationIdle);
            Dispatcher.BeginInvoke(new Action(ShowBusinessOnboardingIfNeeded), DispatcherPriority.ApplicationIdle);
            Dispatcher.BeginInvoke(new Action(ShowUrssafAlertIfNeeded), DispatcherPriority.ApplicationIdle);
        };

        // Poste en lecture seule : on surveille le fichier pour afficher les modifications de l'autre poste.
        _lastDbWrite = SafeWriteTime();
        _watchTimer.Tick += (_, _) => WatchDatabase();
        _watchTimer.Start();

        // Déconnexion automatique : toute action souris / clavier remet le compteur à zéro.
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

    // ------------------------------------------------------------------ déconnexion automatique

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

    /// <summary>Relit le délai d'inactivité défini par l'administrateur (réglage de la base, commun à tous).</summary>
    public void RefreshIdleTimeout()
    {
        _lastIdleRead = DateTime.UtcNow;
        try { _idleMinutes = App.Compta.GetIdleTimeoutMinutes(); }
        catch { /* base momentanément indisponible : on garde la valeur connue */ }
    }

    private void CheckIdle()
    {
        if (_loggingOut) return;
        // Un autre poste ou un administrateur peut avoir changé le délai : relecture chaque minute.
        if (DateTime.UtcNow - _lastIdleRead > TimeSpan.FromMinutes(1)) RefreshIdleTimeout();
        if (_idleMinutes <= 0) return;
        if (DateTime.UtcNow - _lastActivity < TimeSpan.FromMinutes(_idleMinutes)) return;
        _loggingOut = true;
        _idleTimer.Stop();
        // Même effet que « Se déconnecter » : mot de passe mémorisé oublié, retour à l'écran de connexion.
        App.Settings.SetRememberedPassword(null);
        App.Settings.Save();
        App.Restart();
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
        // Un administrateur a demandé la reprise de l'écriture : alerte puis lecture seule.
        if (App.HonorTakeoverRequest()) return;
        if (!App.IsReadOnly) return;
        var t = SafeWriteTime();
        if (t == _lastDbWrite) return;
        _lastDbWrite = t;
        // Ne perturbe pas une saisie en cours : seules les vues de consultation se rechargent toutes seules.
        if (OwnedWindows.Count > 0) return;
        Refresh();
    }
    private void LeaveDemo_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.Confirm("Quitter la démonstration et retrouver vos données ?\n\nLe logiciel va redémarrer.", "Mode démonstration")) App.LeaveDemo();
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
            ForceTakeOverButton.Visibility = App.HasRole(Permission.ManageUsers) && App.WriteLockOwner is not null
                ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            AccessFooter.Text = $"{who} · accès en écriture";
            AccessFooter.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
            ReadOnlyBanner.Visibility = Visibility.Collapsed;
        }

        // Base d'essai : bandeau rouge sur tous les écrans, et marque [TEST] dans le titre de la fenêtre.
        if (App.TestDatabaseName is { } testName)
        {
            TestDbText.Text = $"ATTENTION : vous êtes connecté à une base de données de TEST (« {testName} »). " +
                              "Les écritures saisies ici ne sont pas celles de la comptabilité réelle et peuvent être effacées à tout moment. " +
                              "Ne l'utilisez pas pour une déclaration, une clôture d'exercice ou un export destiné à un tiers (comptable, administration). " +
                              "Pour travailler sur les vraies données, changez de base dans Paramétrage appli › Base de données.";
            TestDbBanner.Visibility = Visibility.Visible;
            Title = Editions.AppName + " — BASE DE TEST";
        }
        else
        {
            TestDbBanner.Visibility = Visibility.Collapsed;
            Title = Editions.AppName;
        }
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

    // ------------------------------------------------------------------ configurations (statut Développeur)

    private bool _configLoading;

    /// <summary>Liste déroulante des configurations enregistrées, visible seulement pour un développeur.</summary>
    private void FillConfigurations()
    {
        var allowed = App.Session.Can(Permission.ManageConfigurations);
        ConfigPanel.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        if (!allowed) return;
        _configLoading = true;
        var profiles = ConfigProfiles.List();
        ConfigCombo.ItemsSource = profiles;
        ConfigCombo.DisplayMemberPath = nameof(ConfigProfile.Name);
        ConfigCombo.SelectedItem = profiles.FirstOrDefault(p => ConfigProfiles.IsCurrent(p, App.Settings));
        ConfigCombo.ToolTip = profiles.Count == 0
            ? "Aucune configuration enregistrée : créez-en dans Paramétrage appli > Configurations."
            : "Bascule vers une autre base de données (le logiciel redémarre sur l'écran de connexion).";
        _configLoading = false;
    }

    private void ConfigCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_configLoading || ConfigCombo.SelectedItem is not ConfigProfile p) return;
        if (ConfigProfiles.IsCurrent(p, App.Settings)) return;
        if (!Ui.Confirm($"Basculer vers la configuration « {p.Name} » ?\n\n{p.Describe()}\n\nLe logiciel va redémarrer sur l'écran de connexion.", "Changer de configuration"))
        {
            FillConfigurations();
            return;
        }
        var error = App.SwitchConfiguration(p);
        if (error is not null) { Ui.Error(error); FillConfigurations(); }
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

    private void ForceTakeOver_Click(object sender, RoutedEventArgs e)
    {
        var owner = App.WriteLockOwner ?? "un autre poste";
        if (!Ui.Confirm($"Reprendre de force l'accès en écriture à {owner} ?\n\n" +
                        "Ce poste recevra une alerte et passera en lecture seule ; ses modifications non enregistrées seront perdues. " +
                        "L'action est consignée au journal d'audit.", "Reprise forcée")) return;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        string? error;
        try { error = App.ForceTakeOverWrite(); }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
        if (error is not null) { Ui.Error(error, "Reprise forcée"); return; }
        UpdateFooter();
        Ui.Info("Ce poste a maintenant l'accès en écriture.");
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

    /// <summary>Devlog (nouveautés) : présenté une seule fois par poste après une mise à jour, si la version le demande
    /// (champ « show » de devlog.json, décidé à chaque release).</summary>
    private void ShowDevLogIfNeeded()
    {
        if (App.ShotsMode || App.RunSelfTest) return;
        var current = App.DisplayVersion;
        if (App.Settings.LastDevLogVersion == current) return;
        var pending = DevLog.Pending(DevLogWindow.LoadEmbedded(), current, App.Settings.LastDevLogVersion);
        App.Settings.LastDevLogVersion = current;
        try { App.Settings.Save(); } catch { /* confort : sera représenté au prochain lancement */ }
        if (pending.Count > 0) new DevLogWindow(pending) { Owner = this }.ShowDialog();
    }

    /// <summary>Autoentreprise, dossier neuf : propose le profil court (activité, déclaration, matériel) qui prépare les
    /// catégories et les réglages. Une seule fois ; « Plus tard » la repropose au prochain lancement.</summary>
    private void ShowBusinessOnboardingIfNeeded()
    {
        if (!Editions.Current.IsBusiness || App.ShotsMode || App.RunSelfTest || CurrentYear is not { Open: true } y) return;
        if (!App.Can(Permission.ManageStructure) || App.IsReadOnly) return;
        try { if (!App.Compta.ShouldOfferBusinessOnboarding(y.Id)) return; }
        catch { return; }
        var dlg = new ProfileWindow { Owner = this };
        var yes = dlg.ShowDialog() == true;
        if (yes)
        {
            if (Ui.Try(() => App.Compta.CompleteBusinessOnboarding(y.Id, dlg.TemplateId, dlg.Mode, dlg.UseAssets)))
            {
                RefreshAssetsNav();
                ShowPage(_page);
                Ui.Info("Votre dossier est préparé : produits et charges habituels ajoutés. Ils se modifient dans « Gestion de l'entreprise » → Produits et charges.");
            }
        }
        else if (dlg.NeverAskAgain) Ui.Try(() => App.Compta.SkipBusinessOnboarding());
    }

    /// <summary>Autoentreprise : à l'ouverture, alerte si une déclaration URSSAF (mensuelle ou trimestrielle) est à faire,
    /// c'est-à-dire une période terminée non marquée « déclarée » dans le Reporting URSSAF.</summary>
    private void ShowUrssafAlertIfNeeded()
    {
        if (!Editions.Current.IsBusiness || App.ShotsMode || App.RunSelfTest || CurrentYear is null) return;
        List<ToutCompteFait.Core.Business.UrssafPeriod> pending;
        try { pending = App.Compta.PendingUrssafDeclarations(DateOnly.FromDateTime(DateTime.Today)); }
        catch { return; }   // base momentanément indisponible : pas d'alerte
        if (pending.Count == 0) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (new UrssafAlertWindow(pending, today) { Owner = this }.ShowDialog() != true) return;
        if (NavMembers.Parent is Panel panel)
            foreach (var b in panel.Children.OfType<RadioButton>().Where(b => b.Tag as string == "reporting"))
                b.IsChecked = true;
    }

    private void PromptFirstYear()
    {
        Ui.Info("Bienvenue ! Commençons par créer le premier exercice (la période comptable).\n\n" +
                "Vous pourrez ensuite saisir les soldes de départ des comptes dans le Paramétrage du dossier, puis importer vos relevés bancaires depuis les Opérations bancaires.",
            Editions.AppName);
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
                        : "Pensez à saisir les soldes de départ des comptes dans le Paramétrage du dossier."));
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

    // ------------------------------------------------------------------ préférences de navigation

    /// <summary>Entrées du menu de gauche (étiquette interne, libellé sans icône, visible ou non), dans l'ordre affiché.</summary>
    public List<(string Tag, string Label, bool Visible)> NavigationItems()
    {
        if (NavMembers.Parent is not Panel panel) return new();
        return panel.Children.OfType<RadioButton>().Where(b => b.Tag is string)
            .Select(b => ((string)b.Tag, NavLabel(b), b.Visibility == Visibility.Visible)).ToList();
    }

    private static string NavLabel(RadioButton b)
    {
        var s = (b.Content as string ?? "").Trim();
        // Retire l'icône (émoji) et les espaces qui la suivent.
        var i = s.IndexOf("   ", StringComparison.Ordinal);
        return i >= 0 ? s[(i + 3)..].Trim() : s;
    }

    /// <summary>Applique l'ordre du menu choisi dans Paramétrage appli → Affichage (les entrées inconnues restent à leur place).</summary>
    public void ApplyNavigationPreferences()
    {
        if (NavMembers.Parent is not Panel panel) return;
        var buttons = panel.Children.OfType<RadioButton>().ToList();
        if (buttons.Count == 0) return;
        _defaultNavOrder ??= buttons.Select(b => b.Tag as string ?? "").ToList();
        var wanted = App.Settings.NavOrder.Count > 0 ? App.Settings.NavOrder : _defaultNavOrder;
        var ordered = wanted.Select(t => buttons.FirstOrDefault(b => b.Tag as string == t)).Where(b => b is not null).Cast<RadioButton>()
            .Concat(buttons.Where(b => !wanted.Contains(b.Tag as string ?? ""))).ToList();
        foreach (var b in buttons) panel.Children.Remove(b);
        for (var i = 0; i < ordered.Count; i++) panel.Children.Insert(i, ordered[i]);
    }

    /// <summary>Ouvre la page choisie comme écran d'accueil (si elle existe et est visible).</summary>
    private void OpenStartPage()
    {
        var tag = App.Settings.StartPage;
        if (string.IsNullOrEmpty(tag) || tag == "livre" || NavMembers.Parent is not Panel panel) return;
        var b = panel.Children.OfType<RadioButton>().FirstOrDefault(x => x.Tag as string == tag && x.Visibility == Visibility.Visible);
        if (b is not null) b.IsChecked = true;
    }
    private List<string>? _defaultNavOrder;
    private RadioButton? _navAssets;
    private RadioButton? _navNotif;

    /// <summary>Met à jour le nombre d'alertes affiché sur « Notifications » (silencieux si la base est indisponible).</summary>
    public void RefreshNotificationCount()
    {
        if (_navNotif is null) return;
        try
        {
            var count = CurrentYear is { } y
                ? App.Compta.BuildNotifications(DateOnly.FromDateTime(DateTime.Today), y.Id).Count
                : 0;
            _navNotif.Content = count > 0 ? $"🔔 {count}" : "🔔";
            if (count > 0 && _navNotif.IsChecked != true) _navNotif.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            else _navNotif.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
        }
        catch
        {
            // Base momentanément indisponible : le comptage est ignoré.
        }
    }

    /// <summary>Ouvre la page demandée (menu de gauche) à partir d'une alerte.</summary>
    private void OpenPageFromNotification(string tag) => OpenPage(tag);

    /// <summary>Ouvre une page : l'entrée du menu correspondante est cochée ; à défaut (page sans entrée de menu, comme
    /// « Premiers pas »), la page est affichée directement.</summary>
    public void OpenPage(string tag)
    {
        var buttons = new[] { NavMembers.Parent, NavDossier.Parent, NavSettings.Parent }.OfType<Panel>().Distinct()
            .SelectMany(p => p.Children.OfType<RadioButton>()).Where(b => b.Tag as string == tag && b.Visibility == Visibility.Visible).ToList();
        if (buttons.Count > 0) buttons[0].IsChecked = true;
        else ShowPage(tag);
    }

    /// <summary>Affiche ou masque l'onglet Immobilisations selon le réglage de la base.</summary>
    public void RefreshAssetsNav()
    {
        if (_navAssets is null) return;
        var on = false;
        try { on = App.Compta.AssetsEnabled(); } catch { /* base momentanément indisponible */ }
        _navAssets.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on && _page == "assets") { _page = "livre"; ShowPage("livre"); }
    }

    /// <summary>Le profil de l'entreprise a changé : le reporting est recalculé s'il est affiché.</summary>
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
        else if (tag == "start") view = new GettingStartedView(y, OpenPageFromNotification);
        else if (y is null)
            view = new HelpView();
        else
            view = tag switch
            {
                "bilan" => new BilanView(y),
                "compare" => new StatisticsView(y),
                "reports" => new ReportsView(y),
                "members" => new MembersView(y),
                "reporting" => new ReportingView(),
                "notifs" => new NotificationsView(y, OpenPageFromNotification),
                "assets" => new AssetsView(y),
                "settings" => new SettingsView(y, SettingsScope.Application),
                "dossier" => new SettingsView(y, SettingsScope.Dossier),
                _ => new LivreView(y)
            };
        Host.Content = view;
        RefreshNotificationCount();
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
            // On capture la zone cliente entière (marges du contenu comprises), pas seulement l'élément racine.
            var root = (FrameworkElement)w.Content;
            if (System.Windows.Media.VisualTreeHelper.GetParent(root) is FrameworkElement host && host.ActualWidth > 0) root = host;
            var width = (int)Math.Max(1, root.ActualWidth);
            var height = (int)Math.Max(1, root.ActualHeight);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            // Le fond de la fenêtre n'appartient pas au contenu : on le peint d'abord pour que la capture soit fidèle.
            var back = new System.Windows.Media.DrawingVisual();
            using (var dc = back.RenderOpen())
                dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
            rtb.Render(back);
            rtb.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }

        foreach (var page in new[] { "livre", "notifs", "start", "bilan", "compare", "reports", "members", "dossier", "settings", "help" }
            .Concat(Editions.Current.IsBusiness ? new[] { "reporting" } : Array.Empty<string>())
            .Concat(App.Compta.AssetsEnabled() ? new[] { "assets" } : Array.Empty<string>()))
        {
            ShowPage(page);
            await Shot(this, page);
        }

        foreach (var tab in new[] { "Utilisateurs", "Base de données" }.Concat(Editions.Current.IsBusiness ? new[] { "Entreprise" } : Array.Empty<string>()))
        {
            ShowPage(tab == "Entreprise" ? "dossier" : "settings");
            if (Host.Content is Views.SettingsView sv) sv.SelectTab(tab);
            await Shot(this, "settings-" + (tab == "Utilisateurs" ? "users" : tab == "Entreprise" ? "business" : "db"));
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
            // Recette ventilée sur plusieurs lignes : on coche « Recette », saisit un montant puis ajoute deux lignes.
            dlg.ModeIncome.IsChecked = true;
            dlg.AmountBox.Text = "300";
            var add = typeof(Dialogs.EntryWindow).GetMethod("AddAlloc_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            add?.Invoke(dlg, new object[] { dlg, new RoutedEventArgs() });
            add?.Invoke(dlg, new object[] { dlg, new RoutedEventArgs() });
            await Shot(dlg, "entry-income-lines");
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
        // Tous les onglets des deux menus de paramétrage.
        foreach (var scope in new[] { "dossier", "settings" })
        {
            ShowPage(scope);
            var count = Host.Content is Views.SettingsView sv0 ? sv0.TabCount : 0;
            for (var i = 0; i < count; i++)
            {
                ShowPage(scope);
                if (Host.Content is Views.SettingsView sv1) sv1.SelectTabIndex(i);
                await Shot(this, $"tab-{scope}-{i}");
            }
        }

        // Statistiques : types d'élément, modes mensuel / annuel, tableau de bord et onglet d'un élément.
        foreach (var (k, annual, tab) in new[]
                 {
                     (0, false, 0), (0, true, 0), (0, false, 1), (0, true, 1), (1, false, 0), (1, false, 1), (2, false, 0), (2, true, 0), (2, false, 1)
                 })
        {
            ShowPage("compare");
            if (Host.Content is Views.StatisticsView st)
            {
                st.ShowForShot(k, annual, tab);
                await Shot(this, $"stat-{k}-{(annual ? "a" : "m")}-{tab}");
            }
        }

        // Boîtes de dialogue communes.
        var today = DateOnly.FromDateTime(DateTime.Today);
        var periods = new[]
        {
            ToutCompteFait.Core.Business.UrssafPeriod.Quarterly(today.Year - 1, 4),
            ToutCompteFait.Core.Business.UrssafPeriod.Quarterly(today.Year, 1),
            ToutCompteFait.Core.Business.UrssafPeriod.Quarterly(today.Year, 2)
        };
        var ua = new Dialogs.UrssafAlertWindow(periods, today) { Owner = this };
        ua.Show();
        await Shot(ua, "dlg-urssaf");
        ua.Close();
        DemoBanner.Visibility = Visibility.Visible;
        ShowPage("livre");
        await Shot(this, "demo-banner");
        DemoBanner.Visibility = App.Settings.DemoMode ? Visibility.Visible : Visibility.Collapsed;
        var tw = new Dialogs.TourWindow { Owner = this };
        tw.Show();
        await Shot(tw, "dlg-visite");
        tw.Close();
        var pw = new Dialogs.ProfileWindow { Owner = this };
        pw.Show();
        await Shot(pw, "dlg-profil");
        pw.Close();
        var ct = new Dialogs.CategoryTemplateWindow(App.Compta.Categories(CurrentYear?.Id ?? 0)) { Owner = this };
        ct.Show();
        await Shot(ct, "dlg-modeles");
        ct.Close();
        var md = new Dialogs.MessageDialog(
            "Valider l'exercice 2025-2026 avec une clôture au 31/08/2026 ?\n\n • les écritures datées après la clôture passent dans l'exercice suivant ;\n" +
            " • les soldes et les reports sont transmis automatiquement à l'exercice suivant ;\n • l'exercice devient verrouillé (modifiable seulement en le rouvrant).\n\n" +
            "Vérifiez le bilan avant de continuer.", "Valider l'exercice", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) { Owner = this };
        md.Show();
        await Shot(md, "dlg-message");
        md.Close();

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








