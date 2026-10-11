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
    public string AdjText { get; init; } = "";
    public string TotalText { get; init; } = "";
    public bool IsTotal { get; init; }
}

public sealed class OperationRow
{
    public string Name { get; init; } = "";
    public string Products { get; init; } = "";
    public string Charges { get; init; } = "";
    public string ProductsText { get; init; } = "";
    public string ChargesText { get; init; } = "";
    public string ResultText { get; init; } = "";
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
        TitleText.Text = $"Bilan — exercice {_year.Label}";
        SubText.Text = $"Du {_year.Start:dd/MM/yyyy} au {_year.End:dd/MM/yyyy}" +
                       (_year.Validated ? $" · validé le {_year.ValidatedAt:dd/MM/yyyy}" : " · exercice ouvert (chiffres provisoires)");

        ValidateButton.Visibility = _year.Open && App.HasRole(Permission.ValidateYear) ? Visibility.Visible : Visibility.Collapsed;
        ValidateButton.IsEnabled = App.Can(Permission.ValidateYear);
        ReopenButton.Visibility = _year.Validated && App.HasRole(Permission.ReopenYear) ? Visibility.Visible : Visibility.Collapsed;
        ReopenButton.IsEnabled = App.Can(Permission.ReopenYear);

        ProductsTotal.Text = Money.Format(b.ProductsTotal) + " €";
        ChargesTotal.Text = Money.Format(b.ChargesTotal) + " €";
        ResultTotal.Text = (b.Result > 0 ? "+" : "") + Money.Format(b.Result) + " €";
        ResultTotal.Foreground = b.Result >= 0
            ? new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57))
            : new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));

        List<BilanLine> Lines(IReadOnlyList<CategoryResult> src, long total)
        {
            var l = src.Select(c => new BilanLine
            {
                Name = c.Name,
                CashText = Money.FormatOrBlank(c.Cash),
                AdjText = c.Adjustments == 0 ? "" : (c.Adjustments > 0 ? "+" : "") + Money.Format(c.Adjustments),
                TotalText = Money.Format(c.Total)
            }).ToList();
            l.Add(new BilanLine { Name = "TOTAL", CashText = Money.Format(src.Sum(s => s.Cash)),
                AdjText = Money.Format(src.Sum(s => s.Adjustments)), TotalText = Money.Format(total), IsTotal = true });
            return l;
        }
        ChargesGrid.ItemsSource = Lines(b.Charges, b.ChargesTotal);
        ProductsGrid.ItemsSource = Lines(b.Products, b.ProductsTotal);

        OperationsGrid.ItemsSource = b.Operations.Select(o => new OperationRow
        {
            Name = o.Operation.Name,
            Products = string.Join(", ", o.Products.Select(p => p.Name)),
            Charges = string.Join(", ", o.Charges.Select(p => p.Name)),
            ProductsText = Money.Format(o.ProductsTotal),
            ChargesText = Money.Format(o.ChargesTotal),
            ResultText = (o.Result > 0 ? "+" : "") + Money.Format(o.Result)
        }).ToList();
        NoOperationText.Visibility = b.Operations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ActifList.ItemsSource = b.Accounts.Select(a => new LabelValue { Label = a.Account.Name, Value = Money.Format(a.Closing) + " €" }).ToList();
        ActifTotal.Text = Money.Format(b.ClosingTotal) + " €";
        var passif = new List<LabelValue>
        {
            new() { Label = "Solde initial (ouverture des comptes)", Value = Money.Format(b.OpeningTotal) + " €" },
            new() { Label = "Report (régularisations entre exercices)", Value = Money.Format(b.Report) + " €" },
            new() { Label = "Résultat de l'exercice", Value = Money.Format(b.Result) + " €" }
        };
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
            BalanceCheck.Text = $"⚠ {b.UnbalancedEntries} écriture(s) ont une ventilation incomplète ({Money.Format(b.UnallocatedGap)} €) : le résultat ci-dessus n'est pas fiable tant qu'elles ne sont pas corrigées (menu Livre, lignes rouges).";
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
            IssuesCard.Visibility = Visibility.Visible;
            IssuesTitle.Text = ok
                ? $"🔒 Exercice validé le {_year.ValidatedAt:dd/MM/yyyy} par {_year.ValidatedBy} : données verrouillées, empreinte de contrôle conforme."
                : "⚠ ATTENTION : le contenu de cet exercice validé ne correspond plus à l'empreinte enregistrée à la validation. La base a été modifiée en dehors du logiciel.";
            IssuesTitle.Foreground = new SolidColorBrush(ok ? Color.FromRgb(0x04, 0x78, 0x57) : Color.FromRgb(0xB9, 0x1C, 0x1C));
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
        foreach (var c in b.Charges) sb.AppendLine($"{Q(c.Name)};{N(c.Cash)};{N(c.Adjustments)};{N(c.Total)}");
        sb.AppendLine($"TOTAL CHARGES;{N(b.Charges.Sum(x => x.Cash))};{N(b.Charges.Sum(x => x.Adjustments))};{N(b.ChargesTotal)}");
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
        sb.AppendLine($"TOTAL ACTIF;{N(b.ClosingTotal)}");
        sb.AppendLine();
        sb.AppendLine("PASSIF;Montant");
        sb.AppendLine($"Solde initial;{N(b.OpeningTotal)}");
        sb.AppendLine($"Report;{N(b.Report)}");
        sb.AppendLine($"Résultat de l'exercice;{N(b.Result)}");
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

