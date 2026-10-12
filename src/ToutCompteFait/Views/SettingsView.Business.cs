using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Business;
using ToutCompteFait.Core.Security;

namespace ToutCompteFait.Views;

/// <summary>Onglet « Entreprise » (version Autoentreprise) : identification de l'entreprise (récupérée à partir du SIREN
/// ou du SIRET), codes APE et types de déclaration proposés, mode de déclaration, taux de cotisations, plafonds.</summary>
public partial class SettingsView
{
    private readonly TextBox _bizSiren = new();
    private readonly TextBox _bizName = new();
    private readonly TextBox _bizAddress = new();
    private readonly TextBox _bizPostal = new();
    private readonly TextBox _bizCity = new();
    private readonly TextBox _bizNature = new();
    // Codes APE : une entreprise peut en avoir plusieurs (activités différentes) ; tous sont listés et modifiables.
    private readonly List<string> _apeCodes = new();
    private readonly DataGrid _apeGrid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 60, MaxHeight = 220
    };
    private readonly TextBox _apeNew = new() { Width = 110, MaxLength = 12 };
    private sealed record ApeRow(string Code, string Type, string Note);
    private readonly DatePicker _bizCreated = new();
    private readonly ComboBox _bizMode = new();
    private readonly TextBlock _bizStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Dictionary<UrssafKind, TextBox> _bizRates = new();
    private readonly TextBox _bizCeilTotal = new(), _bizCeilServices = new(), _bizVatSale = new(), _bizVatSaleMax = new(),
        _bizVatService = new(), _bizVatServiceMax = new(), _bizAcreDiscount = new();
    private readonly DatePicker _bizAcreUntil = new();
    private Button? _bizLookupButton;
    private BusinessProfile _bizLoaded = new();

    private static Style Sty(string key) => (Style)Application.Current.FindResource(key);

    private void InitBusiness()
    {
        if (!Editions.Current.IsBusiness) return;
        // Colonne « Type URSSAF » dans la liste des produits.
        ProductsGrid.Columns.Insert(1, new DataGridTextColumn
        {
            Header = "Type URSSAF", Binding = new System.Windows.Data.Binding("Type"), Width = 130,
            ElementStyle = (Style)Application.Current.FindResource("LeftCell")
        });
        Tabs.Items.Insert(0, new TabItem { Header = "Entreprise", Content = BuildBusinessPanel() });
        Tabs.SelectedIndex = 0;
        LoadBusiness();
    }

    private UIElement BuildBusinessPanel()
    {
        var canEdit = App.Can(Permission.ManageStructure);
        var root = new StackPanel { Margin = new Thickness(0, 12, 0, 24), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };

        TextBlock Section(string text) => new() { Text = text, Style = Sty("SectionText"), Margin = new Thickness(0, 18, 0, 6) };
        Grid Form() => new()
        {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(230) }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } }
        };
        void Row(Grid g, string label, UIElement control)
        {
            var r = g.RowDefinitions.Count;
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
            Grid.SetRow(t, r);
            Grid.SetRow(control, r);
            Grid.SetColumn(control, 1);
            if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 0, 3);
            g.Children.Add(t);
            g.Children.Add(control);
        }

        // --- Identification
        root.Children.Add(new TextBlock
        {
            Style = Sty("MutedText"), TextWrapping = TextWrapping.Wrap,
            Text = "Saisissez le SIREN (9 chiffres) ou le SIRET (14 chiffres) : le nom, l'adresse et les codes APE sont récupérés " +
                   "automatiquement auprès de l'API publique « Recherche d'entreprises » de l'État (données Sirene de l'INSEE). " +
                   "Seul ce numéro est envoyé. Vous pouvez corriger les informations avant d'enregistrer."
        });
        root.Children.Add(Section("Identification de l'entreprise"));
        var id = Form();
        var sirenPanel = new DockPanel();
        _bizLookupButton = new Button { Content = "Récupérer les informations", Style = Sty("PrimaryButton"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = canEdit };
        _bizLookupButton.Click += BizLookup_Click;
        DockPanel.SetDock(_bizLookupButton, Dock.Right);
        sirenPanel.Children.Add(_bizLookupButton);
        sirenPanel.Children.Add(_bizSiren);
        Row(id, "SIREN ou SIRET", sirenPanel);
        Row(id, "Nom / dénomination", _bizName);
        Row(id, "Adresse", _bizAddress);
        Row(id, "Code postal", _bizPostal);
        Row(id, "Commune", _bizCity);
        Row(id, "Nature juridique (code INSEE)", _bizNature);
        Row(id, "Date de création", _bizCreated);
        root.Children.Add(id);
        root.Children.Add(_bizStatus);

        // --- Codes APE (un par activité)
        root.Children.Add(Section("Activités (codes APE)"));
        root.Children.Add(new TextBlock
        {
            Style = Sty("MutedText"), TextWrapping = TextWrapping.Wrap,
            Text = "Une entreprise peut exercer plusieurs activités. Le registre public ne fournit que le code APE principal (et ceux des " +
                   "établissements) : ajoutez ici les codes de vos autres activités. Chaque code donne un type de déclaration URSSAF."
        });
        _apeGrid.Margin = new Thickness(0, 6, 0, 0);
        _apeGrid.Columns.Add(new DataGridTextColumn { Header = "Code APE", Binding = new System.Windows.Data.Binding("Code"), Width = 100 });
        _apeGrid.Columns.Add(new DataGridTextColumn { Header = "Type de déclaration proposé", Binding = new System.Windows.Data.Binding("Type"), Width = 220 });
        _apeGrid.Columns.Add(new DataGridTextColumn { Header = "Remarque", Binding = new System.Windows.Data.Binding("Note"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        root.Children.Add(_apeGrid);
        var apeBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var addApe = new Button { Content = "Ajouter ce code APE", Style = Sty("SecondaryButton"), Margin = new Thickness(8, 0, 8, 0), IsEnabled = canEdit };
        var removeApe = new Button { Content = "Retirer le code sélectionné", Style = Sty("SecondaryButton"), IsEnabled = canEdit };
        addApe.Click += (_, _) => AddApeCode();
        removeApe.Click += (_, _) =>
        {
            if (_apeGrid.SelectedItem is ApeRow row) { _apeCodes.Remove(row.Code); RefreshApeInfo(); }
        };
        _apeNew.KeyDown += (_, a) => { if (a.Key == System.Windows.Input.Key.Enter) AddApeCode(); };
        apeBar.Children.Add(_apeNew);
        apeBar.Children.Add(addApe);
        apeBar.Children.Add(removeApe);
        root.Children.Add(apeBar);
        _apeNew.IsEnabled = canEdit;
        var createCats = new Button
        {
            Content = $"Créer les catégories de recettes de l'exercice d'après ces codes APE", Style = Sty("SecondaryButton"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), IsEnabled = canEdit && _year.Open
        };
        createCats.Click += BizCreateCategories_Click;
        root.Children.Add(createCats);

        // --- Déclaration
        root.Children.Add(Section("Déclaration de chiffre d'affaires"));
        var decl = Form();
        _bizMode.ItemsSource = new[] { "Trimestrielle", "Mensuelle" };
        _bizMode.SelectedIndex = 0;
        Row(decl, "Mode de déclaration", _bizMode);
        root.Children.Add(decl);

        // --- Cotisations
        root.Children.Add(Section("Taux de cotisations estimés (% du chiffre d'affaires)"));
        root.Children.Add(new TextBlock
        {
            Style = Sty("MutedText"), TextWrapping = TextWrapping.Wrap,
            Text = "Estimation pour le suivi : le site de l'URSSAF calcule les montants officiels. Les taux et plafonds par défaut " +
                   "sont indicatifs (ils sont révisés par l'État) : vérifiez-les et corrigez-les si besoin."
        });
        var rates = Form();
        foreach (var k in UrssafKinds.All)
        {
            var box = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
            _bizRates[k] = box;
            Row(rates, k.Label(), box);
        }
        Row(rates, "ACRE : réduction (%)", _bizAcreDiscount);
        _bizAcreDiscount.Width = 90;
        _bizAcreDiscount.HorizontalAlignment = HorizontalAlignment.Left;
        Row(rates, "ACRE : appliquée jusqu'au (vide = sans)", _bizAcreUntil);
        _bizAcreUntil.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(rates);

        // --- Plafonds
        root.Children.Add(Section("Plafonds du régime micro et franchise de TVA (€)"));
        var ceil = Form();
        foreach (var (label, box) in new[]
                 {
                     ("Plafond de chiffre d'affaires (total)", _bizCeilTotal),
                     ("Plafond des prestations de services", _bizCeilServices),
                     ("Franchise de TVA : vente de marchandises", _bizVatSale),
                     ("Franchise de TVA : vente (seuil majoré)", _bizVatSaleMax),
                     ("Franchise de TVA : prestations de services", _bizVatService),
                     ("Franchise de TVA : prestations (seuil majoré)", _bizVatServiceMax)
                 })
        {
            box.Width = 130;
            box.HorizontalAlignment = HorizontalAlignment.Left;
            Row(ceil, label, box);
        }
        root.Children.Add(ceil);

        var save = new Button
        {
            Content = "Enregistrer", Style = Sty("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 20, 0, 0), IsEnabled = canEdit
        };
        save.Click += BizSave_Click;
        root.Children.Add(save);
        if (!canEdit)
            root.Children.Add(new TextBlock
            {
                Text = "Votre rôle ne permet que la consultation de ces réglages.", Style = Sty("MutedText"), Margin = new Thickness(0, 8, 0, 0)
            });

        foreach (var tb in new[] { _bizSiren, _bizName, _bizAddress, _bizPostal, _bizCity, _bizNature, _bizAcreDiscount, _bizCeilTotal,
                                   _bizCeilServices, _bizVatSale, _bizVatSaleMax, _bizVatService, _bizVatServiceMax }.Concat(_bizRates.Values))
            tb.IsEnabled = canEdit;
        _bizCreated.IsEnabled = canEdit;
        _bizAcreUntil.IsEnabled = canEdit;
        _bizMode.IsEnabled = canEdit;

        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static string Euros(long cents) => (cents / 100m).ToString("0.##", CultureInfo.GetCultureInfo("fr-FR"));
    private static string Percent(int bp) => (bp / 100m).ToString("0.##", CultureInfo.GetCultureInfo("fr-FR"));

    private void LoadBusiness()
    {
        var p = _c.GetBusinessProfile();
        _bizLoaded = p;
        ShowProfile(p);
        var prm = _c.GetUrssafParams();
        foreach (var k in UrssafKinds.All) _bizRates[k].Text = Percent(prm.RatesBp.GetValueOrDefault(k));
        _bizAcreDiscount.Text = prm.AcreDiscountPercent.ToString(CultureInfo.InvariantCulture);
        _bizAcreUntil.SelectedDate = prm.AcreUntil?.ToDateTime(TimeOnly.MinValue);
        _bizCeilTotal.Text = Euros(prm.CeilingTotalCents);
        _bizCeilServices.Text = Euros(prm.CeilingServicesCents);
        _bizVatSale.Text = Euros(prm.VatSaleCents);
        _bizVatSaleMax.Text = Euros(prm.VatSaleMaxCents);
        _bizVatService.Text = Euros(prm.VatServiceCents);
        _bizVatServiceMax.Text = Euros(prm.VatServiceMaxCents);
        _bizStatus.Text = p.FetchedAt is { } f
            ? $"Informations récupérées le {f:dd/MM/yyyy à HH:mm} (source : API Recherche d'entreprises, données Sirene/INSEE)."
            : p.IsEmpty ? "Aucune entreprise renseignée : saisissez votre SIREN ou SIRET puis « Récupérer les informations »." : "";
    }

    private void ShowProfile(BusinessProfile p)
    {
        _bizSiren.Text = p.Siret ?? p.Siren ?? "";
        _bizName.Text = p.Name ?? "";
        _bizAddress.Text = p.Address ?? "";
        _bizPostal.Text = p.PostalCode ?? "";
        _bizCity.Text = p.City ?? "";
        _bizNature.Text = p.LegalNature ?? "";
        _bizCreated.SelectedDate = p.CreationDate?.ToDateTime(TimeOnly.MinValue);
        _apeCodes.Clear();
        _apeCodes.AddRange(p.ApeCodes.Count > 0 ? p.ApeCodes : p.ApeMain is { } m ? new[] { m } : Array.Empty<string>());
        _bizMode.SelectedIndex = p.Mode == DeclarationMode.Monthly ? 1 : 0;
        RefreshApeInfo();
    }

    private List<string> ApeCodesFromBox() => _apeCodes.Distinct().ToList();

    private void AddApeCode()
    {
        var raw = _apeNew.Text.Trim();
        if (raw.Length == 0) return;
        var code = ApeMapper.Normalize(raw);
        if (code is null) { Ui.Error("Code APE invalide : format attendu 4 chiffres et une lettre, par exemple 56.10A ou 5610A."); return; }
        if (!_apeCodes.Contains(code)) _apeCodes.Add(code);
        _apeNew.Clear();
        RefreshApeInfo();
    }

    private void RefreshApeInfo()
    {
        _apeGrid.ItemsSource = _apeCodes.Distinct().Select(code =>
        {
            var s = ApeMapper.Suggest(code);
            return s is null
                ? new ApeRow(code, "Non reconnu", "Choisissez le type à la création de la catégorie.")
                : new ApeRow(code, s.Kind.Label(), s.Reason + (s.Certain ? "" : " — à vérifier : ce code peut relever d'un autre type."));
        }).ToList();
    }

    private async void BizLookup_Click(object sender, RoutedEventArgs e)
    {
        _bizLookupButton!.IsEnabled = false;
        _bizStatus.Text = "Recherche en cours…";
        try
        {
            var info = await BusinessLookup.LookupAsync(_bizSiren.Text);
            var kept = _apeCodes.ToList();   // activités ajoutées à la main : conservées
            ShowProfile(info with { Mode = _bizMode.SelectedIndex == 1 ? DeclarationMode.Monthly : DeclarationMode.Quarterly });
            foreach (var c in kept) if (!_apeCodes.Contains(c)) _apeCodes.Add(c);
            RefreshApeInfo();
            _bizLoaded = info;
            _bizStatus.Text = $"Informations récupérées le {DateTime.Now:dd/MM/yyyy à HH:mm} (source : API Recherche d'entreprises, " +
                              "données Sirene/INSEE). Vérifiez-les puis cliquez sur « Enregistrer ».";
        }
        catch (ComptaException ex)
        {
            _bizStatus.Text = ex.Message;
        }
        finally
        {
            _bizLookupButton.IsEnabled = App.Can(Permission.ManageStructure);
        }
    }

    private BusinessProfile ReadProfileFromBoxes()
    {
        var number = new string(_bizSiren.Text.Where(char.IsDigit).ToArray());
        var codes = ApeCodesFromBox();
        return _bizLoaded with
        {
            Siren = number.Length >= 9 ? number[..9] : _bizLoaded.Siren,
            Siret = number.Length == 14 ? number : _bizLoaded.Siret,
            Name = _bizName.Text.Trim(),
            Address = _bizAddress.Text.Trim(),
            PostalCode = _bizPostal.Text.Trim(),
            City = _bizCity.Text.Trim(),
            LegalNature = _bizNature.Text.Trim(),
            CreationDate = _bizCreated.SelectedDate is { } d ? DateOnly.FromDateTime(d) : null,
            ApeMain = codes.FirstOrDefault(),
            ApeCodes = codes,
            Mode = _bizMode.SelectedIndex == 1 ? DeclarationMode.Monthly : DeclarationMode.Quarterly
        };
    }

    private bool TryReadParams(out UrssafParams prm, out string error)
    {
        prm = new UrssafParams();
        error = "";
        var message = "";
        bool Cents(TextBox b, string label, out long v)
        {
            v = 0;
            if (Money.TryParse(b.Text, out v) && v >= 0) return true;
            message = $"Montant invalide : {label}.";
            return false;
        }
        var rates = new Dictionary<UrssafKind, int>();
        foreach (var k in UrssafKinds.All)
        {
            var text = _bizRates[k].Text.Trim().Replace("%", "").Replace(',', '.');
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var pct) || pct < 0 || pct > 100)
            {
                error = $"Taux invalide : {k.Short()} (0 à 100 %).";
                return false;
            }
            rates[k] = (int)Math.Round(pct * 100, MidpointRounding.AwayFromZero);
        }
        if (!int.TryParse(_bizAcreDiscount.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var discount) || discount is < 0 or > 100)
        {
            error = "Réduction ACRE invalide (0 à 100 %).";
            return false;
        }
        if (!Cents(_bizCeilTotal, "plafond total", out var ct) || !Cents(_bizCeilServices, "plafond des prestations", out var cs)
            || !Cents(_bizVatSale, "franchise de TVA (vente)", out var vs) || !Cents(_bizVatSaleMax, "franchise de TVA majorée (vente)", out var vsm)
            || !Cents(_bizVatService, "franchise de TVA (prestations)", out var vv) || !Cents(_bizVatServiceMax, "franchise de TVA majorée (prestations)", out var vvm))
        {
            error = message;
            return false;
        }
        prm = new UrssafParams
        {
            RatesBp = rates,
            AcreDiscountPercent = discount,
            AcreUntil = _bizAcreUntil.SelectedDate is { } u ? DateOnly.FromDateTime(u) : null,
            CeilingTotalCents = ct, CeilingServicesCents = cs, VatSaleCents = vs, VatSaleMaxCents = vsm, VatServiceCents = vv, VatServiceMaxCents = vvm
        };
        return true;
    }

    private void BizSave_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadParams(out var prm, out var error)) { Ui.Error(error); return; }
        var profile = ReadProfileFromBoxes();
        if (Ui.Try(() => { _c.SaveBusinessProfile(profile); _c.SaveUrssafParams(prm); }))
        {
            _bizLoaded = _c.GetBusinessProfile();
            Ui.Info("Informations de l'entreprise enregistrées.");
            MainWindow.Current?.RefreshBusinessNav();
            // Aucune catégorie de recettes dans cet exercice : propose de les créer d'après les codes APE.
            if (_year.Open && App.Can(Permission.ManageStructure) && _c.Categories(_year.Id).All(c => c.Kind != CategoryKind.Product))
                BizCreateCategories_Click(sender, e);
        }
    }

    private void BizCreateCategories_Click(object sender, RoutedEventArgs e)
    {
        var wanted = ApeCodesFromBox().Select(ApeMapper.Suggest).Where(s => s is not null).Select(s => s!.Kind).Distinct().ToList();
        if (wanted.Count == 0) { Ui.Info("Aucun type de déclaration n'a pu être déduit des codes APE : créez les catégories depuis « Produits et charges »."); return; }
        var existing = _c.CategoryUrssafKinds();
        var cats = _c.Categories(_year.Id).Where(c => c.Kind == CategoryKind.Product)
            .Select(c => Compta.ResolveUrssafKind(existing, c.Name)).Where(k => k is not null).Select(k => k!.Value).ToHashSet();
        var toCreate = wanted.Where(k => !cats.Contains(k)).ToList();
        if (toCreate.Count == 0) { Ui.Info("Les catégories de recettes de ces types existent déjà dans cet exercice."); return; }
        if (!Ui.Confirm("Créer dans l'exercice " + _year.Label + " les catégories de recettes :\n\n• " +
                        string.Join("\n• ", toCreate.Select(k => k.Short())), "Catégories d'activité")) return;
        if (Ui.Try(() => { foreach (var k in toCreate) _c.AddActivityCategory(_year.Id, k, null); })) Reload();
    }
}