using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Security;
using ToutCompteFait.Dialogs;

namespace ToutCompteFait.Views;

public sealed class BilanLine
{
    public string Name { get; init; } = "";
    public string CashText { get; init; } = "";
    public string AdjText { get; set; } = "";
    public string TotalText { get; init; } = "";
    public bool IsTotal { get; init; }

    /// <summary>Catégorie du bilan (double-clic : détail des lignes du livre) ; null pour une ligne de synthèse.</summary>
    public long? CategoryId { get; init; }
}

public sealed class OperationRow
{
    public string Name { get; init; } = "";
    public string Products { get; init; } = "";
    public string Charges { get; init; } = "";
    public string ProductsText { get; init; } = "";
    public string ChargesText { get; init; } = "";
    public string ResultText { get; init; } = "";

    /// <summary>Catégories (produits et charges) liées à l'opération, pour le détail au double-clic.</summary>
    public IReadOnlyList<long> CategoryIds { get; init; } = Array.Empty<long>();
}

public sealed class IssueLine
{
    public string Text { get; init; } = "";
    public Brush Brush { get; init; } = Brushes.Black;
}

public sealed class LabelValue
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
}

public partial class BilanView : UserControl, IPage
{
    private readonly FiscalYear _year;
    private readonly Compta _c = App.Compta;
    private Bilan? _bilan;

    public BilanView(FiscalYear year)
    {
        InitializeComponent();
        _year = year;
        foreach (var g in new[] { ChargesGrid, ProductsGrid, OperationsGrid })
            g.PreviewMouseWheel += ForwardWheel;
        Reload();
    }

    // ------------------------------------------------------------------ impression

    /// <summary>Menu d'impression : édition synthétique (les chiffres de cette page) ou détaillée (avec chaque ligne de compte).</summary>
    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string header, string tip, bool detailed)
        {
            var mi = new MenuItem { Header = header, ToolTip = tip };
            mi.Click += (_, _) => PrintBilan(detailed);
            return mi;
        }
        menu.Items.Add(Item("Édition synthétique — chiffres du bilan",
            "Synthèse, compte de fonctionnement, rentabilité des opérations et situation (ce qui est affiché sur cette page).", false));
        menu.Items.Add(Item("Édition détaillée — avec toutes les lignes de compte",
            "L'édition synthétique, puis chaque catégorie avec toutes les lignes du livre et les reports qui la composent.", true));
        menu.IsOpen = true;
    }

    private void PrintBilan(bool detailed)
    {
        var org = App.OrganizationName;
        var label = _year.Label.Replace('/', '-');
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Imprimer le bilan",
            Filter = "Document PDF (*.pdf)|*.pdf",
            FileName = $"Bilan {(detailed ? "détaillé" : "synthétique")} {label}{(_year.Validated ? "" : " (provisoire)")}.pdf"
        };
        if (dlg.ShowDialog() != true) return;
        byte[]? pdf = null;
        if (!Ui.Try(() => pdf = _c.ExportBilanPdf(_year.Id, detailed, org, App.Session.DisplayName)) || pdf is null) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, pdf);
            // Ouverture dans le lecteur PDF par défaut, d'où l'impression.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Ui.Error("Impossible d'enregistrer ou d'ouvrir le PDF : " + ex.Message);
        }
    }

    /// <summary>Double-clic sur une catégorie ou une opération : lignes du livre (et reports) qui la composent.</summary>
    private void Detail_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject src) return;
        if (ItemsControl.ContainerFromElement(grid, src) is not DataGridRow row) return;   // pas l'en-tête
        IReadOnlyCollection<long>? ids = null;
        string? name = null;
        switch (row.Item)
        {
            case BilanLine { CategoryId: { } id, IsTotal: false } line:
                ids = new[] { id };
                name = line.Name;
                break;
            case OperationRow op when op.CategoryIds.Count > 0:
                ids = op.CategoryIds;
                name = "Opération « " + op.Name + " »";
                break;
        }
        if (ids is null || name is null) return;
        e.Handled = true;
        new CategoryDetailWindow(_year, "Détail — " + name, ids) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    // Les tableaux n'ont pas de défilement propre : la molette fait défiler la page.
    private void ForwardWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender };
        Scroll.RaiseEvent(args);
    }

    public void Reload()
    {
        var b = _c.ComputeBilan(_year.Id);
        _bilan = b;
        TitleText.Text = $"Bilan & comptes annuels — exercice {_year.Label}";
        SubText.Text = $"Du {_year.Start:dd/MM/yyyy} au {_year.End:dd/MM/yyyy}" +
                       (_year.Validated
                           ? $" · validé le {_year.ValidatedAt:dd/MM/yyyy}" +
                             (Compta.CanReopenNow(_year) ? $" · réouvrable jusqu'au {Compta.ReopenDeadline(_year):dd/MM/yyyy}" : "")
                           : " · exercice ouvert (chiffres provisoires)");

        ValidateButton.Visibility = _year.Open && App.HasRole(Permission.ValidateYear) ? Visibility.Visible : Visibility.Collapsed;
        ValidateButton.IsEnabled = App.Can(Permission.ValidateYear);
        ReopenButton.Visibility = _year.Validated && App.HasRole(Permission.ReopenYear) ? Visibility.Visible : Visibility.Collapsed;
        var canReopenNow = Compta.CanReopenNow(_year);
        ReopenButton.IsEnabled = App.Can(Permission.ReopenYear) && canReopenNow;
        ReopenButton.ToolTip = !_year.Validated ? null
            : canReopenNow
                ? $"Réouverture possible jusqu'au {Compta.ReopenDeadline(_year):dd/MM/yyyy HH:mm} ({Compta.ReopenWindowDays} jours après la validation)."
                : $"Délai dépassé : la réouverture n'est possible que {Compta.ReopenWindowDays} jours après la validation.";

        ProductsTotal.Text = Money.Format(b.ProductsTotal) + " €";
        ChargesTotal.Text = Money.Format(b.ChargesTotal) + " €";
        ResultTotal.Text = (b.Result > 0 ? "+" : "") + Money.Format(b.Result) + " €";
        ResultTotal.Foreground = b.Result >= 0
            ? new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57))
            : new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));

        // Résultat négatif : précise la part d'investissement (achats d'immobilisations), qui explique souvent la baisse
        // de trésorerie sans peser sur le résultat (porté à l'actif, seule la dotation aux amortissements est une charge).
        var investmentNote = b.InvestmentNote();
        ResultNote.Text = investmentNote;
        ResultNote.Visibility = investmentNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        List<BilanLine> Lines(IReadOnlyList<CategoryResult> src, long total)
        {
            var l = src.Select(c => new BilanLine
            {
                Name = c.Name,
                CategoryId = c.CategoryId,
                CashText = Money.FormatOrBlank(c.Cash),
                AdjText = c.Adjustments == 0 ? "" : (c.Adjustments > 0 ? "+" : "") + Money.Format(c.Adjustments),
                TotalText = Money.Format(c.Total)
            }).ToList();
            l.Add(new BilanLine { Name = "TOTAL", CashText = Money.Format(src.Sum(s => s.Cash)),
                AdjText = Money.Format(src.Sum(s => s.Adjustments)), TotalText = Money.Format(total), IsTotal = true });
            return l;
        }
        var operatingCharges = b.OperatingCharges.ToList();
        var chargeLines = Lines(operatingCharges, b.ChargesTotal);
        // Immobilisations : achats portés à l'actif (retirés des charges), dotations et sorties.
        var assetRows = new List<BilanLine>();
        void AssetRow(string name, long cents)
        {
            if (cents != 0) assetRows.Add(new BilanLine { Name = name, CashText = "", AdjText = (cents > 0 ? "+" : "") + Money.Format(cents), TotalText = Money.Format(cents) });
        }
        AssetRow("Achats immobilisés (portés à l'actif)", -b.AssetsReclass);
        AssetRow("Dotations aux amortissements", b.AssetsDepreciation);
        AssetRow("Valeur nette des immobilisations sorties", b.AssetsDisposals);
        if (assetRows.Count > 0)
        {
            chargeLines.InsertRange(chargeLines.Count - 1, assetRows);
            var total = chargeLines[^1];
            total.AdjText = Money.Format(b.ChargesTotal - operatingCharges.Sum(x => x.Cash));
        }
        ChargesGrid.ItemsSource = chargeLines;
        ProductsGrid.ItemsSource = Lines(b.Products, b.ProductsTotal);

        OperationsGrid.ItemsSource = b.Operations.Select(o => new OperationRow
        {
            Name = o.Operation.Name,
            CategoryIds = o.Products.Concat(o.Charges).Where(p => p.CategoryId is not null).Select(p => p.CategoryId!.Value).ToList(),
            Products =string.Join(", ", o.Products.Select(p => p.Name)),
            Charges = string.Join(", ", o.Charges.Select(p => p.Name)),
            ProductsText = Money.Format(o.ProductsTotal),
            ChargesText = Money.Format(o.ChargesTotal),
            ResultText = (o.Result > 0 ? "+" : "") + Money.Format(o.Result)
        }).ToList();
        NoOperationText.Visibility = b.Operations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var actif = b.Accounts.Select(a => new LabelValue { Label = a.Account.Name, Value = Money.Format(a.Closing) + " €" }).ToList();
        foreach (var a in b.Assets.Where(a => a.Closing != 0))
            actif.Add(new LabelValue { Label = $"Immobilisation : {a.Asset.Label} (valeur nette)", Value = Money.Format(a.Closing) + " €" });
        ActifList.ItemsSource = actif;
        ActifTotal.Text = Money.Format(b.AssetsTotal) + " €";
        var passif = new List<LabelValue>
        {
            new() { Label = "Solde initial (ouverture des comptes)", Value = Money.Format(b.OpeningTotal) + " €" },
            new() { Label = "Immobilisations à l'ouverture (valeur nette reportée)", Value = Money.Format(b.AssetsNetOpening) + " €" },
            new() { Label = "Report (régularisations entre exercices)", Value = Money.Format(b.Report) + " €" },
            new() { Label = "Résultat de l'exercice", Value = Money.Format(b.Result) + " €" }
        };
        if (b.PersonalTotal != 0)
            passif.Add(new LabelValue { Label = "Prélèvements de l'exploitant (dépenses personnelles, hors résultat)", Value = Money.Format(-b.PersonalTotal) + " €" });
        if (b.UnallocatedGap != 0)
            passif.Add(new LabelValue { Label = $"⚠ Écritures non ventilées ({b.UnbalancedEntries})", Value = Money.Format(b.UnallocatedGap) + " €" });
        PassifList.ItemsSource = passif;
        PassifTotal.Text = Money.Format(b.LiabilitiesTotal) + " €";

        if (b.BalanceDifference == 0 && b.UnallocatedGap == 0)
        {
            BalanceCheck.Text = "✔ Actif = Passif : le bilan est équilibré et toutes les écritures sont ventilées.";
            BalanceCheck.Foreground = new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57));
        }
        else if (b.BalanceDifference == 0)
        {
            BalanceCheck.Text = $"⚠ {b.UnbalancedEntries} écriture(s) ont une ventilation incomplète ({Money.Format(b.UnallocatedGap)} €) : le résultat ci-dessus n'est pas fiable tant qu'elles ne sont pas corrigées (menu Opérations bancaires, lignes rouges).";
            BalanceCheck.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
        }
        else
        {
            BalanceCheck.Text = $"⚠ Écart actif/passif de {Money.Format(b.BalanceDifference)} € : anomalie, contactez le support.";
            BalanceCheck.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
        }

        // Contrôles avant validation
        if (_year.Open)
        {
            var issues = _c.ValidationIssues(_year.Id);
            if (issues.Count == 0)
            {
                IssuesCard.Visibility = Visibility.Visible;
                IssuesTitle.Text = "✔ Aucun point bloquant : l'exercice peut être validé.";
                IssuesTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57));
                IssuesList.ItemsSource = null;
            }
            else
            {
                var blocking = issues.Count(i => i.Blocking);
                IssuesCard.Visibility = Visibility.Visible;
                IssuesTitle.Text = blocking > 0
                    ? $"Avant de pouvoir valider : {blocking} point(s) à corriger" +
                      (App.Session.Role == ToutCompteFait.Core.Security.Role.Admin
                          ? " (en tant qu'administrateur, vous pouvez forcer la clôture)" : "")
                    : "L'exercice peut être validé (quelques remarques) :";
                IssuesTitle.Foreground = new SolidColorBrush(blocking > 0 ? Color.FromRgb(0xB9, 0x1C, 0x1C) : Color.FromRgb(0xB4, 0x53, 0x09));
                IssuesList.ItemsSource = issues.OrderByDescending(i => i.Blocking).Take(15).Select(i => new IssueLine
                {
                    Text = (i.Blocking ? "✖ " : "• ") + i.Message,
                    Brush = i.Blocking ? new SolidColorBrush(Color.FromRgb(0x99, 0x1B, 0x1B)) : new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69))
                }).Concat(issues.Count > 15 ? new[] { new IssueLine { Text = $"… et {issues.Count - 15} autre(s)." } } : Array.Empty<IssueLine>()).ToList();
            }
        }
        else
        {
            var ok = _c.VerifyIntegrity(_year.Id);
            var chain = _c.VerifyAuditChain();
            IssuesCard.Visibility = Visibility.Visible;
            IssuesTitle.Text = !ok
                ? "⚠ ATTENTION : le contenu de cet exercice validé ne correspond plus à l'empreinte enregistrée à la validation. La base a été modifiée en dehors du logiciel."
                : !chain.Ok
                    ? $"⚠ ATTENTION : le journal d'audit a été altéré ({chain.Problem}). Le contenu de l'exercice {_year.Label} reste conforme à son empreinte."
                    : $"🔒 Exercice validé le {_year.ValidatedAt:dd/MM/yyyy} par {_year.ValidatedBy} : données verrouillées, empreinte de contrôle conforme, " +
                      $"journal d'audit intact ({chain.Rows} ligne(s) chaînées).";
            IssuesTitle.Foreground = new SolidColorBrush(ok && chain.Ok ? Color.FromRgb(0x04, 0x78, 0x57) : Color.FromRgb(0xB9, 0x1C, 0x1C));
            IssuesList.ItemsSource = null;
        }
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        var rows = _c.Entries(_year.Id);
        var dlg = new Dialogs.CloseYearWindow(_year, d => rows.Count(r => r.Entry.Date > d)) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        var closing = dlg.ClosingDate;
        if (!Ui.Confirm(
                $"Valider l'exercice {_year.Label} avec une clôture au {closing:dd/MM/yyyy} ?\n\n" +
                "Après validation :\n" +
                " • plus aucune écriture, catégorie ou report ne peut être ajouté, modifié ou retiré ;\n" +
                " • les écritures datées après la clôture passent dans l'exercice suivant ;\n" +
                " • les soldes et les reports sont transmis automatiquement à l'exercice suivant ;\n" +
                " • une empreinte de contrôle est enregistrée.\n\n" +
                "Vérifiez le bilan avant de continuer.", "Valider l'exercice"))
            return;
        // Points bloquants : un administrateur peut forcer la clôture (écritures non ventilées, dates antérieures).
        var force = false;
        var blocking = _c.ValidationIssues(_year.Id, closing).Where(i => i.Blocking).ToList();
        if (blocking.Count > 0 && App.Session.Role == ToutCompteFait.Core.Security.Role.Admin)
        {
            if (!Ui.Confirm($"{blocking.Count} point(s) bloquant(s) subsistent :\n\n• " +
                            string.Join("\n• ", blocking.Take(6).Select(i => i.Message)) +
                            (blocking.Count > 6 ? $"\n… et {blocking.Count - 6} autre(s)." : "") +
                            "\n\nEn tant qu'administrateur, vous pouvez FORCER la clôture. Les mouvements non ventilés resteront « non affectés » " +
                            "dans l'exercice verrouillé, et la clôture forcée sera consignée dans le journal.\n\n" +
                            "Forcer la clôture ?", "Forcer la clôture"))
                return;
            force = true;
        }
        if (Ui.Try(() => _c.ValidateYear(_year.Id, closing, force)))
        {
            Ui.Info($"L'exercice {_year.Label} est validé et verrouillé (clôture au {closing:dd/MM/yyyy}).");
            MainWindow.Current?.Refresh();
        }
    }
    private void Reopen_Click(object sender, RoutedEventArgs e)
    {
        var reason = PromptWindow.Ask(Window.GetWindow(this), "Rouvrir l'exercice",
            $"Rouvrir un exercice validé est exceptionnel (il perd son verrouillage).\n\nIndiquez le motif de la réouverture de {_year.Label} :",
            okText: "Rouvrir");
        if (reason is null) return;
        if (Ui.Try(() => _c.ReopenYear(_year.Id, reason)))
            MainWindow.Current?.Refresh();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_bilan is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exporter le bilan",
            Filter = "Fichier CSV (*.csv)|*.csv",
            FileName = $"Bilan {_year.Label}.csv"
        };
        if (dlg.ShowDialog() != true) return;
        var b = _bilan;
        var sb = new StringBuilder();
        string N(long c) => (c / 100m).ToString("0.00", Money.Fr);
        string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        sb.AppendLine($"Compte de fonctionnement du {_year.Start:dd/MM/yyyy} au {_year.End:dd/MM/yyyy}");
        sb.AppendLine();
        sb.AppendLine("CHARGES;Livre;Reports;Total");
        var opCharges = b.OperatingCharges.ToList();
        foreach (var c in opCharges) sb.AppendLine($"{Q(c.Name)};{N(c.Cash)};{N(c.Adjustments)};{N(c.Total)}");
        if (b.AssetsReclass != 0) sb.AppendLine($"Achats immobilisés (portés à l'actif);;{N(-b.AssetsReclass)};{N(-b.AssetsReclass)}");
        if (b.AssetsDepreciation != 0) sb.AppendLine($"Dotations aux amortissements;;{N(b.AssetsDepreciation)};{N(b.AssetsDepreciation)}");
        if (b.AssetsDisposals != 0) sb.AppendLine($"Valeur nette des immobilisations sorties;;{N(b.AssetsDisposals)};{N(b.AssetsDisposals)}");
        sb.AppendLine($"TOTAL CHARGES;{N(opCharges.Sum(x => x.Cash))};{N(b.ChargesTotal - opCharges.Sum(x => x.Cash))};{N(b.ChargesTotal)}");
        sb.AppendLine();
        sb.AppendLine("PRODUITS;Livre;Reports;Total");
        foreach (var c in b.Products) sb.AppendLine($"{Q(c.Name)};{N(c.Cash)};{N(c.Adjustments)};{N(c.Total)}");
        sb.AppendLine($"TOTAL PRODUITS;{N(b.Products.Sum(x => x.Cash))};{N(b.Products.Sum(x => x.Adjustments))};{N(b.ProductsTotal)}");
        sb.AppendLine();
        sb.AppendLine($"RESULTAT NET;;;{N(b.Result)}");
        sb.AppendLine();
        sb.AppendLine("OPERATIONS;Produits;Charges;Rentabilité");
        foreach (var o in b.Operations) sb.AppendLine($"{Q(o.Operation.Name)};{N(o.ProductsTotal)};{N(o.ChargesTotal)};{N(o.Result)}");
        sb.AppendLine();
        sb.AppendLine("ACTIF;Solde");
        foreach (var a in b.Accounts) sb.AppendLine($"{Q(a.Account.Name)};{N(a.Closing)}");
        foreach (var a in b.Assets.Where(a => a.Closing != 0)) sb.AppendLine($"{Q("Immobilisation : " + a.Asset.Label + " (valeur nette)")};{N(a.Closing)}");
        sb.AppendLine($"TOTAL ACTIF;{N(b.AssetsTotal)}");
        sb.AppendLine();
        sb.AppendLine("PASSIF;Montant");
        sb.AppendLine($"Solde initial;{N(b.OpeningTotal)}");
        if (b.AssetsNetOpening != 0) sb.AppendLine($"Immobilisations à l'ouverture (valeur nette reportée);{N(b.AssetsNetOpening)}");
        sb.AppendLine($"Report;{N(b.Report)}");
        sb.AppendLine($"Résultat de l'exercice;{N(b.Result)}");
        if (b.PersonalTotal != 0) sb.AppendLine($"Prélèvements de l'exploitant (dépenses personnelles);{N(-b.PersonalTotal)}");
        if (b.UnallocatedGap != 0) sb.AppendLine($"Ecritures non ventilées;{N(b.UnallocatedGap)}");
        sb.AppendLine($"TOTAL PASSIF;{N(b.LiabilitiesTotal)}");
        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            Ui.Info("Bilan exporté :\n" + dlg.FileName);
        }
        catch (IOException ex) { Ui.Error("Export impossible : " + ex.Message); }
    }
}

