using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Business;
using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Security;
using ToutCompteFait.Core.Services;

namespace ToutCompteFait.Dialogs;

/// <summary>Assistant de première configuration : une seule fenêtre, des étapes qui défilent (Retour / Continuer) au lieu
/// d'une succession de fenêtres. Étapes : choix des données → compte administrateur → activité (Autoentreprise) → première
/// période → soldes de départ → récapitulatif. Rien n'est écrit avant le dernier bouton ; les autres choix de départ (fichier
/// existant, serveur, configuration, démonstration) sont rendus à l'appelant comme avec l'ancien écran d'accueil.</summary>
public sealed class SetupWizardWindow : Window
{
    private sealed class Step
    {
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public Func<UIElement> Build { get; init; } = () => new Border();
        /// <summary>Contrôle de l'étape ; renvoie un message d'erreur, ou null si on peut continuer.</summary>
        public Func<string?>? Validate { get; init; }
        public string NextText { get; init; } = "Continuer";
        public bool HideNext { get; init; }
    }

    private readonly string _dbPath;
    private readonly bool _business = Editions.Current.IsBusiness;
    private readonly List<Step> _steps = new();
    private int _index;

    private readonly TextBlock _stepText = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)), FontSize = 12.5 };
    private readonly ProgressBar _progress = new() { Height = 6, Minimum = 0, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14) };
    private readonly ContentControl _body = new();
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold };
    private readonly Button _back = new() { Content = "Retour" };
    private readonly Button _next = new() { Content = "Continuer", IsDefault = true };

    // Réponses
    private readonly TextBox _user = new() { Text = "admin" };
    private readonly TextBox _display = new();
    private readonly PasswordBox _pwd = new();
    private readonly PasswordBox _pwd2 = new();
    private readonly TextBox _companyName = new();
    private readonly List<(RadioButton Radio, string TemplateId)> _activities = new();
    private readonly RadioButton _quarterly = new() { Content = "Par trimestre (le plus courant)", IsChecked = true, GroupName = "wmode" };
    private readonly RadioButton _monthly = new() { Content = "Par mois", GroupName = "wmode" };
    private readonly RadioButton _assetsYes = new() { Content = "Oui, suivre mon matériel et l'amortir", GroupName = "wassets" };
    private readonly RadioButton _assetsNo = new() { Content = "Non, pas pour le moment", IsChecked = true, GroupName = "wassets" };
    private readonly DatePicker _start = new();
    private readonly DatePicker _end = new();
    private readonly TextBox _yearLabel = new();
    private bool _labelEdited;
    private readonly TextBox _openBank = new() { HorizontalContentAlignment = HorizontalAlignment.Right };
    private readonly TextBox _openSavings = new() { HorizontalContentAlignment = HorizontalAlignment.Right };

    // État de la création (reprise possible en cas d'erreur)
    private AppUser? _createdUser;
    private Compta? _compta;
    private FiscalYear? _createdYear;

    /// <summary>Choix de départ ; <see cref="FirstRunChoice.CreateLocal"/> + <see cref="Completed"/> = dossier créé et configuré.</summary>
    public FirstRunChoice Choice { get; private set; } = FirstRunChoice.Cancel;
    public bool Completed { get; private set; }
    public string? DroppedConfigPath { get; private set; }

    /// <summary>Identifiants du compte créé : la connexion se fait automatiquement ensuite.</summary>
    public string? AdminUserName { get; private set; }
    public string? AdminPassword { get; private set; }

    private static Style Sty(string key) => (Style)Application.Current.FindResource(key);

    public SetupWizardWindow(string expectedPath)
    {
        _dbPath = expectedPath;
        Title = Editions.AppName + " — Première utilisation";
        Width = 740;
        Height = 700;
        MinHeight = 560;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("BrushSurface");

        BuildSteps(expectedPath);
        _title.Style = Sty("TitleText");
        _subtitle.Style = Sty("MutedText");
        _back.Style = Sty("SecondaryButton");
        _next.Style = Sty("PrimaryButton");
        _next.Margin = new Thickness(0);
        _back.Click += (_, _) => Go(_index - 1);
        _next.Click += (_, _) => Advance();
        _progress.Maximum = _steps.Count - 1;

        var quit = new Button { Content = "Quitter", Style = Sty("SecondaryButton"), IsCancel = true, Padding = new Thickness(18, 7, 18, 7) };
        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 14, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_back);
        right.Children.Add(_next);
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);
        footer.Children.Add(quit);

        var head = new DockPanel();
        var logo = new ToutCompteFait.Controls.AppLogo { Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 8) };
        DockPanel.SetDock(logo, Dock.Left);
        head.Children.Add(logo);
        var headText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headText.Children.Add(_stepText);
        headText.Children.Add(_progress);
        head.Children.Add(headText);

        var root = new DockPanel { Margin = new Thickness(26, 20, 26, 18) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(footer);
        var content = new StackPanel();
        content.Children.Add(_title);
        content.Children.Add(_subtitle);
        content.Children.Add(_body);
        content.Children.Add(_error);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0), Content = content });
        Content = root;

        AllowDrop = true;
        PreviewDragOver += (_, e) => FileDrop.Accept(e);
        PreviewDrop += (_, e) =>
        {
            e.Handled = true;
            var file = FileDrop.Files(e).FirstOrDefault();
            if (file is null) return;
            DroppedConfigPath = file;
            Choice = FirstRunChoice.LoadConfig;
            DialogResult = true;
        };
        Go(0);
    }

    // ------------------------------------------------------------------ étapes

    private void BuildSteps(string expectedPath)
    {
        _steps.Add(new Step
        {
            Title = "Bienvenue !",
            Subtitle = "Commençons : où voulez-vous garder vos données ? Pour une première utilisation seul(e) sur cet ordinateur, le premier choix convient. " +
                       "Ce choix pourra être modifié plus tard dans Paramétrage appli → Base de données.",
            Build = () => ChoicesPanel(), HideNext = true
        });
        _steps.Add(new Step
        {
            Title = "Votre compte",
            Subtitle = "Ce compte administrateur protège l'accès au logiciel et sert à vous connecter. Notez bien le mot de passe : il n'y a pas de récupération automatique.",
            Build = AccountPanel, Validate = ValidateAccount
        });
        if (_business)
            _steps.Add(new Step
            {
                Title = "Votre activité",
                Subtitle = "Trois questions pour préparer vos produits et charges habituels, le calendrier de vos déclarations URSSAF et vos réglages. Tout reste modifiable ensuite.",
                Build = ActivityPanel
            });
        _steps.Add(new Step
        {
            Title = "Votre première période",
            Subtitle = _business
                ? "L'année fiscale regroupe toutes vos écritures. Pour une auto-entreprise, c'est en général l'année civile ; vous pouvez ajuster les dates."
                : "L'exercice regroupe toutes vos écritures (souvent de septembre à août pour une association) ; vous pouvez ajuster les dates.",
            Build = PeriodPanel, Validate = ValidatePeriod
        });
        _steps.Add(new Step
        {
            Title = "Vos soldes de départ",
            Subtitle = "Le solde de vos comptes à la première date de la période, pour que les soldes affichés soient justes. Laissez vide ou à 0 si vous n'êtes pas sûr(e) : cela se règle plus tard.",
            Build = OpeningPanel, Validate = ValidateOpening
        });
        _steps.Add(new Step
        {
            Title = "Tout est prêt",
            Subtitle = "Voici ce que nous allons préparer. Cliquez sur le bouton pour créer votre dossier : vous arriverez ensuite sur la page « Premiers pas ».",
            Build = RecapPanel, NextText = "Créer mon dossier et commencer", Validate = Finish
        });
    }

    private UIElement ChoicesPanel()
    {
        var p = new StackPanel();
        p.Children.Add(Choice0("Je démarre : créer mes données sur cet ordinateur (recommandé)",
            "Une base vide est créée ici, sans rien à installer.", FirstRunChoice.CreateLocal, primary: true));
        p.Children.Add(Choice0("J'ai déjà des données : ouvrir un fichier existant",
            "Un fichier .db déjà utilisé : copie, sauvegarde ou dossier partagé sur le réseau.", FirstRunChoice.OpenFile, false));
        p.Children.Add(Choice0("Nous sommes plusieurs : me connecter à un serveur partagé",
            "Les données sont sur un serveur (PostgreSQL, MariaDB / MySQL ou SQL Server) partagé entre les postes.", FirstRunChoice.ConnectServer, false));
        p.Children.Add(Choice0("J'ai reçu un fichier de configuration",
            "Fichier .tcfconfig remis par l'administrateur. Vous pouvez aussi le glisser dans cette fenêtre.", FirstRunChoice.LoadConfig, false));
        p.Children.Add(Choice0("Je veux d'abord essayer : ouvrir un dossier de démonstration",
            "Des données entièrement fictives, séparées de vos vraies données : vous pourrez revenir à tout moment.", FirstRunChoice.Demo, false));
        p.Children.Add(new TextBlock
        {
            Text = "Emplacement prévu : " + _dbPath, Style = Sty("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 11.5
        });
        return p;
    }

    private UIElement Choice0(string title, string description, FirstRunChoice choice, bool primary)
    {
        var text = new StackPanel { Width = 610, HorizontalAlignment = HorizontalAlignment.Left };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        text.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Opacity = .8 });
        var button = new Button
        {
            Content = text, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8),
            Style = Sty(primary ? "PrimaryButton" : "SecondaryButton")
        };
        button.Click += (_, _) =>
        {
            if (choice == FirstRunChoice.CreateLocal) { Go(1); return; }
            Choice = choice;
            DialogResult = true;
        };
        return button;
    }

    private static UIElement Field(string label, UIElement control, string? hint = null)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 12), MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };
        p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
        if (control is TextBox or PasswordBox) { ((FrameworkElement)control).Width = 380; ((FrameworkElement)control).HorizontalAlignment = HorizontalAlignment.Left; }
        if (control is PasswordBox pb) { pb.Padding = new Thickness(8, 7, 8, 7); pb.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)); }
        p.Children.Add(control);
        if (hint is not null) p.Children.Add(new TextBlock { Text = hint, Style = Sty("MutedText"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
        return p;
    }

    private UIElement AccountPanel()
    {
        var p = new StackPanel();
        p.Children.Add(Field("Identifiant de connexion", _user, "Au moins 3 caractères (lettres, chiffres)."));
        p.Children.Add(Field("Nom affiché", _display, "Votre nom, tel qu'il apparaîtra dans le journal des opérations."));
        p.Children.Add(Field("Mot de passe", _pwd, "8 caractères minimum, avec au moins une lettre et un chiffre."));
        p.Children.Add(Field("Confirmer le mot de passe", _pwd2));
        return p;
    }

    private string? ValidateAccount()
    {
        var user = _user.Text.Trim();
        if (user.Length < 3) return "L'identifiant doit faire au moins 3 caractères.";
        if (_display.Text.Trim().Length == 0) _display.Text = user;
        if (_pwd.Password != _pwd2.Password) return "Les deux mots de passe ne sont pas identiques.";
        return PasswordPolicy.Validate(_pwd.Password, user);
    }

    private UIElement ActivityPanel()
    {
        var p = new StackPanel();
        p.Children.Add(Field("Nom de votre entreprise (facultatif)", _companyName));
        p.Children.Add(new TextBlock { Text = "1. Quelle est votre activité ?", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
        if (_activities.Count == 0)
        {
            var first = true;
            foreach (var t in CategoryTemplates.All)
            {
                var radio = new RadioButton { GroupName = "wactivity", IsChecked = first, Margin = new Thickness(0, 3, 0, 3) };
                radio.Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = t.Description, TextWrapping = TextWrapping.Wrap, Opacity = .75, FontSize = 12.5 }
                    }
                };
                _activities.Add((radio, t.Id));
                first = false;
            }
        }
        foreach (var a in _activities) p.Children.Add(a.Radio);
        p.Children.Add(new TextBlock { Text = "2. Comment déclarez-vous votre chiffre d'affaires à l'URSSAF ?", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        p.Children.Add(_quarterly);
        p.Children.Add(_monthly);
        p.Children.Add(new TextBlock { Text = "3. Achetez-vous du matériel durable (ordinateur, machine, outillage) ?", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        p.Children.Add(_assetsYes);
        p.Children.Add(_assetsNo);
        return p;
    }

    private UIElement PeriodPanel()
    {
        if (_start.SelectedDate is null)
        {
            var today = DateTime.Today;
            DateTime s = _business ? new DateTime(today.Year, 1, 1) : new DateTime(today.Month >= 9 ? today.Year : today.Year - 1, 9, 1);
            _start.SelectedDate = s;
            _end.SelectedDate = s.AddYears(1).AddDays(-1);
            _yearLabel.Text = Compta.DefaultLabel(DateOnly.FromDateTime(s), DateOnly.FromDateTime(s.AddYears(1).AddDays(-1)));
            _yearLabel.TextChanged += (_, _) => { if (_yearLabel.IsKeyboardFocused) _labelEdited = true; };
            void Dates(object? sender, SelectionChangedEventArgs e)
            {
                if (_labelEdited || _start.SelectedDate is not { } a || _end.SelectedDate is not { } b) return;
                _yearLabel.Text = Compta.DefaultLabel(DateOnly.FromDateTime(a), DateOnly.FromDateTime(b));
            }
            _start.SelectedDateChanged += Dates;
            _end.SelectedDateChanged += Dates;
        }
        var p = new StackPanel();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _start.Width = 190;
        _end.Width = 190;
        row.Children.Add(Field("Début", _start));
        row.Children.Add(new Border { Width = 16 });
        row.Children.Add(Field("Fin", _end));
        p.Children.Add(row);
        p.Children.Add(Field(_business ? "Nom de l'année fiscale" : "Nom de l'exercice", _yearLabel));
        return p;
    }

    private string? ValidatePeriod()
    {
        if (_start.SelectedDate is not { } s || _end.SelectedDate is not { } e) return "Renseignez les deux dates.";
        if (e <= s) return "La date de fin doit être après la date de début.";
        if (_yearLabel.Text.Trim().Length == 0) return _business ? "Donnez un nom à l'année fiscale." : "Donnez un nom à l'exercice.";
        return null;
    }

    private UIElement OpeningPanel()
    {
        var p = new StackPanel();
        p.Children.Add(Field("Compte bancaire principal (€)", _openBank, "Le solde affiché par votre banque à la première date de la période."));
        p.Children.Add(Field("Livret A (€) — facultatif", _openSavings, "Laissez vide si vous n'avez pas de livret."));
        return p;
    }

    private string? ValidateOpening()
    {
        foreach (var box in new[] { _openBank, _openSavings })
            if (box.Text.Trim().Length > 0 && !Money.TryParse(box.Text, out _)) return "Montant invalide : saisissez par exemple 1 250,00.";
        return null;
    }

    private UIElement RecapPanel()
    {
        var p = new StackPanel();
        void Line(string label, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var l = new TextBlock { Text = label, Width = 200, Style = Sty("MutedText") };
            DockPanel.SetDock(l, Dock.Left);
            row.Children.Add(l);
            row.Children.Add(new TextBlock { Text = value, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            p.Children.Add(row);
        }
        Line("Compte administrateur", $"{_display.Text.Trim()} ({_user.Text.Trim()})");
        if (_business)
        {
            Line("Activité", CategoryTemplates.Find(Template ?? "")?.Name ?? "—");
            Line("Déclaration URSSAF", _monthly.IsChecked == true ? "Par mois" : "Par trimestre");
            Line("Matériel durable", _assetsYes.IsChecked == true ? "Suivi activé" : "Non");
        }
        if (_start.SelectedDate is { } s && _end.SelectedDate is { } e)
            Line(_business ? "Année fiscale" : "Exercice", $"{_yearLabel.Text.Trim()} ({s:dd/MM/yyyy} → {e:dd/MM/yyyy})");
        Line("Solde de départ", _openBank.Text.Trim().Length > 0 ? _openBank.Text.Trim() + " €" : "à renseigner plus tard");
        p.Children.Add(new TextBlock
        {
            Text = "Étapes suivantes : importer le relevé de votre banque, classer vos écritures, puis consulter le bilan et les statistiques. La page « Premiers pas » vous guide.",
            Style = Sty("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0)
        });
        return p;
    }

    private string? Template => _activities.FirstOrDefault(a => a.Radio.IsChecked == true).TemplateId;

    // ------------------------------------------------------------------ création

    /// <summary>Dernière étape : crée la base, le compte, la période, les soldes et le profil. Reprend où elle s'était arrêtée
    /// si une étape échoue (le message s'affiche et l'on peut corriger puis réessayer).</summary>
    private string? Finish()
    {
        try
        {
            var factory = new DbContextFactory(_dbPath);
            if (_createdUser is null)
            {
                factory.EnsureDatabaseUpToDate(App.DisplayVersion);
                _createdUser = new AuthService(factory).CreateFirstAdmin(_user.Text.Trim(), _display.Text.Trim(), _pwd.Password);
            }
            _compta ??= new Compta(factory, new Session(_createdUser.Id, _createdUser.UserName, _createdUser.DisplayName, _createdUser.Role, _createdUser.IsDeveloper));
            if (_createdYear is null)
            {
                _createdYear = _compta.CreateYear(DateOnly.FromDateTime(_start.SelectedDate!.Value), DateOnly.FromDateTime(_end.SelectedDate!.Value), true, _yearLabel.Text.Trim());
                SetOpening("Banque", _openBank.Text);
                SetOpening("Livret A", _openSavings.Text);
                if (_business)
                {
                    var name = _companyName.Text.Trim();
                    if (name.Length > 0) _compta.SaveBusinessProfile(_compta.GetBusinessProfile() with { Name = name });
                    _compta.CompleteBusinessOnboarding(_createdYear.Id, Template,
                        _monthly.IsChecked == true ? DeclarationMode.Monthly : DeclarationMode.Quarterly, _assetsYes.IsChecked == true);
                }
            }
            AdminUserName = _user.Text.Trim();
            AdminPassword = _pwd.Password;
            Choice = FirstRunChoice.CreateLocal;
            Completed = true;
            DialogResult = true;
            return null;
        }
        catch (ComptaException ex)
        {
            return ex.Message;
        }
    }

    private void SetOpening(string accountName, string text)
    {
        if (text.Trim().Length == 0 || !Money.TryParse(text, out var cents) || cents == 0) return;
        var account = _compta!.Accounts().FirstOrDefault(a => a.Name == accountName);
        if (account is not null) _compta.SetOpening(_createdYear!.Id, account.Id, cents);
    }

    // ------------------------------------------------------------------ navigation

    private void Advance()
    {
        var step = _steps[_index];
        var error = step.Validate?.Invoke();
        if (error is not null)
        {
            _error.Text = error;
            return;
        }
        if (_index < _steps.Count - 1) Go(_index + 1);
    }

    /// <summary>Diagnostic (captures d'écran) : affiche une étape donnée.</summary>
    public int StepCount => _steps.Count;
    public void ShowStep(int index) => Go(index);

    private void Go(int index)
    {
        _index = Math.Clamp(index, 0, _steps.Count - 1);
        var s = _steps[_index];
        _error.Text = "";
        _title.Text = s.Title;
        _subtitle.Text = s.Subtitle;
        _body.Content = s.Build();
        _stepText.Text = $"Étape {_index + 1} sur {_steps.Count}";
        _progress.Value = _index;
        _back.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        _next.Visibility = s.HideNext ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = s.NextText;
    }
}
