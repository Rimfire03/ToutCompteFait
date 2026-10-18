using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Business;
using ToutCompteFait.Core.Security;

namespace ToutCompteFait.Views;

/// <summary>Reporting URSSAF (version Autoentreprise) : pour chaque période (mois ou trimestre), les montants à saisir sur
/// le site de l'URSSAF, une estimation des cotisations, le suivi des plafonds et de la franchise de TVA, et l'historique
/// des déclarations faites.</summary>
public sealed class ReportingView : UserControl, IPage
{
    private readonly Compta _c = App.Compta;
    private readonly ComboBox _yearBox = new() { Width = 100 };
    private readonly ComboBox _periodBox = new() { Width = 220, Margin = new Thickness(12, 0, 0, 0) };
    private readonly StackPanel _body = new();
    private Panel _target;

    /// <summary>Ajoute un élément à la carte courante.</summary>
    private void Put(UIElement element) => _target.Children.Add(element);

    /// <summary>Ouvre une nouvelle carte blanche (titre facultatif) : les éléments suivants y sont placés.</summary>
    private void Card(string? title)
    {
        var inner = new StackPanel();
        if (title is not null)
            inner.Children.Add(new TextBlock { Text = title, Style = Sty("SectionText"), Margin = new Thickness(0, 0, 0, 8) });
        _body.Children.Add(new Border { Style = Sty("Card"), Margin = new Thickness(0, 14, 0, 0), Child = inner });
        _target = inner;
    }
    private bool _loading;
    private DeclarationMode _mode;

    public ReportingView()
    {
        _target = _body;
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = "Reporting URSSAF — déclaration du chiffre d'affaires", Style = Sty("TitleText") });
        var picker = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        picker.Children.Add(new TextBlock { Text = "Année", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        picker.Children.Add(_yearBox);
        picker.Children.Add(_periodBox);
        root.Children.Add(picker);
        root.Children.Add(_body);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _yearBox.SelectionChanged += (_, _) => { if (!_loading) FillPeriods(null); };
        _periodBox.SelectionChanged += (_, _) => { if (!_loading) Render(); };
        Reload();
    }

    private static Style Sty(string key) => (Style)Application.Current.FindResource(key);
    private static string Eur(long cents) => Money.Format(cents) + " €";

    private sealed record PeriodItem(UrssafPeriod Period)
    {
        public override string ToString() => Period.Label;
    }

    public void Reload()
    {
        _loading = true;
        var profile = _c.GetBusinessProfile();
        _mode = profile.Mode;
        var first = new[] { _c.Years().Select(y => y.Start.Year).DefaultIfEmpty(DateTime.Today.Year).Min(), profile.CreationDate?.Year ?? DateTime.Today.Year }.Min();
        var last = DateTime.Today.Year;
        var years = Enumerable.Range(first, Math.Max(1, last - first + 1)).Reverse().ToList();
        var keepYear = _yearBox.SelectedItem as int?;
        _yearBox.ItemsSource = years;

        // Période proposée : la dernière période terminée (celle qu'on déclare maintenant), sinon la courante.
        var today = DateOnly.FromDateTime(DateTime.Today);
        var current = UrssafPeriod.ForDate(today, _mode);
        var previous = UrssafPeriod.ForDate(current.Start.AddDays(-1), _mode);
        var suggested = previous;
        _yearBox.SelectedItem = keepYear is { } k && years.Contains(k) ? k : suggested.Start.Year;
        _loading = false;
        FillPeriods(keepYear is null ? suggested.Key : (_periodBox.SelectedItem as PeriodItem)?.Period.Key ?? suggested.Key);
    }

    private void FillPeriods(string? selectKey)
    {
        _loading = true;
        var year = _yearBox.SelectedItem as int? ?? DateTime.Today.Year;
        var items = UrssafPeriod.OfYear(year, _mode).Select(p => new PeriodItem(p)).ToList();
        _periodBox.ItemsSource = items;
        _periodBox.SelectedItem = items.FirstOrDefault(i => i.Period.Key == selectKey) ?? items.LastOrDefault(i => i.Period.End < DateOnly.FromDateTime(DateTime.Today))
                                  ?? items.FirstOrDefault();
        _loading = false;
        Render();
    }

    /// <summary>Encadré d'information (bleu), d'avertissement (jaune) ou d'alerte (rouge).</summary>
    private void AddNotice(NoticeLevel level, string text)
    {
        var (background, border, foreground) = level switch
        {
            NoticeLevel.Alert => ("#FEF2F2", "#FECACA", "#991B1B"),
            NoticeLevel.Warning => ("#FEF9C3", "#FDE047", "#713F12"),
            _ => ("#EFF6FF", "#BFDBFE", "#1E3A8A")
        };
        var conv = new System.Windows.Media.BrushConverter();
        Put(new Border
        {
            Background = (System.Windows.Media.Brush)conv.ConvertFromString(background)!,
            BorderBrush = (System.Windows.Media.Brush)conv.ConvertFromString(border)!,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 8, 0, 0),
            Child = new TextBlock
            {
                Text = (level == NoticeLevel.Alert ? "⚠ " : level == NoticeLevel.Warning ? "⚠ " : "ℹ ") + text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)conv.ConvertFromString(foreground)!
            }
        });
    }

    private TextBlock Muted(string text) => new() { Text = text, Style = Sty("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    private void Render()
    {
        _body.Children.Clear();
        _target = _body;
        Card(null);
        var profile = _c.GetBusinessProfile();
        if (_periodBox.SelectedItem is not PeriodItem item) return;
        var period = item.Period;
        var today = DateOnly.FromDateTime(DateTime.Today);

        Put(Muted(string.IsNullOrWhiteSpace(profile.Name)
            ? "Entreprise non renseignée : complétez Paramétrage du dossier › Entreprise (SIREN, codes APE, mode de déclaration)."
            : $"{profile.Name} · SIREN {profile.Siren} · déclaration {(_mode == DeclarationMode.Monthly ? "mensuelle" : "trimestrielle")}" +
              (profile.ApeCodes.Count > 0 ? $" · APE {string.Join(", ", profile.ApeCodes)}" : "")));

        UrssafReport report;
        try { report = _c.ComputeUrssafReport(period); }
        catch (ComptaException ex) { Put(Muted(ex.Message)); return; }

        // --- État de la déclaration
        string state;
        Brush brush;
        if (report.DeclaredAt is { } at)
        {
            state = $"✔ Déclarée le {at:dd/MM/yyyy}" + (report.DeclaredBy is { Length: > 0 } by ? $" par {by}" : "");
            brush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
        }
        else if (period.End >= today)
        {
            state = $"Période en cours : à déclarer entre le {period.End.AddDays(1):dd/MM/yyyy} et le {period.DueDate:dd/MM/yyyy}";
            brush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        }
        else if (today > period.DueDate)
        {
            state = $"⚠ En retard : à déclarer avant le {period.DueDate:dd/MM/yyyy}";
            brush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
        }
        else
        {
            state = $"À déclarer avant le {period.DueDate:dd/MM/yyyy} ({(period.DueDate.DayNumber - today.DayNumber)} jour(s) restant(s))";
            brush = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
        }
        Put(new TextBlock
        {
            Text = $"{period.Label} — {state}", FontWeight = FontWeights.SemiBold, FontSize = 15, Foreground = brush,
            Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap
        });

        // --- À saisir sur le site
        Card("À saisir sur autoentrepreneur.urssaf.fr");
        var grid = new Grid { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var w in new[] { 140, 80, 150 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        var r = 0;
        void Cell(int row, int col, string text, bool bold = false, bool right = false, bool header = false)
        {
            var t = new TextBlock
            {
                Text = text, FontWeight = bold || header ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(6, 5, 6, 5),
                TextWrapping = TextWrapping.Wrap, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Foreground = header ? (Brush)Application.Current.FindResource("BrushTextSecondary") : Brushes.Black
            };
            var b = new Border
            {
                Child = t, BorderBrush = (Brush)Application.Current.FindResource("BrushBorder"), BorderThickness = new Thickness(0, 0, 0, 1),
                Background = header ? new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)) : Brushes.White
            };
            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            grid.Children.Add(b);
        }
        void NewRow() => grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        NewRow();
        Cell(r, 0, "Rubrique de la déclaration", header: true);
        Cell(r, 1, "Chiffre d'affaires", header: true, right: true);
        Cell(r, 2, "Taux estimé", header: true, right: true);
        Cell(r, 3, "Cotisations estimées", header: true, right: true);
        foreach (var line in report.Lines)
        {
            r++;
            NewRow();
            Cell(r, 0, line.Kind.FormLabel());
            Cell(r, 1, Eur(line.TurnoverCents), right: true, bold: true);
            Cell(r, 2, $"{line.RateBp / 100m:0.##} %", right: true);
            Cell(r, 3, Eur(line.ContributionCents), right: true);
        }
        r++;
        NewRow();
        Cell(r, 0, "Total", bold: true);
        Cell(r, 1, Eur(report.TotalTurnoverCents), bold: true, right: true);
        Cell(r, 2, "", right: true);
        Cell(r, 3, Eur(report.TotalContributionCents), bold: true, right: true);
        Put(grid);

        if (report.Lines.All(l => l.TurnoverCents == 0))
            Put(Muted("Aucun chiffre d'affaires sur la période : la déclaration à 0 € est quand même obligatoire sur le site."));
        Put(Muted("Le chiffre d'affaires est celui ENCAISSÉ sur la période (date de l'écriture), hors virements entre comptes. " +
                                 "Les cotisations sont une estimation : le site calcule le montant exact à payer."));
        if (report.UnclassifiedCents != 0)
            Put(new TextBlock
            {
                Text = $"⚠ {Eur(report.UnclassifiedCents)} de recettes sont ventilées sur des produits sans type de déclaration : ils ne sont pas comptés. " +
                       "Choisissez leur type dans Paramétrage du dossier › Produits et charges.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
            });
        if (report.PendingEntries > 0)
            Put(new TextBlock
            {
                Text = $"⚠ {report.PendingEntries} entrée(s) d'argent de la période ne sont pas encore ventilées (Livre, lignes jaunes) : " +
                       "le total peut être incomplet.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
            });

        // --- Actions
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "Copier le récapitulatif", Style = Sty("SecondaryButton") };
        copy.Click += (_, _) =>
        {
            var text = $"Déclaration URSSAF — {period.Label}\n" +
                       string.Join("\n", report.Lines.Select(l => $"{l.Kind.FormLabel()} : {Money.Format(l.TurnoverCents)} €")) +
                       $"\nTotal : {Money.Format(report.TotalTurnoverCents)} €";
            try { Clipboard.SetText(text); Ui.Info("Récapitulatif copié dans le presse-papiers."); }
            catch (System.Runtime.InteropServices.ExternalException) { Ui.Error("Le presse-papiers est indisponible."); }
        };
        actions.Children.Add(copy);
        var site = new Button { Content = "Ouvrir autoentrepreneur.urssaf.fr", Style = Sty("SecondaryButton"), ToolTip = ToutCompteFait.Services.UrssafSite.Address };
        site.Click += (_, _) => ToutCompteFait.Services.UrssafSite.Open();
        actions.Children.Add(site);
        var canMark = App.Can(Permission.WriteLedger);
        if (report.DeclaredAt is null)
        {
            var mark = new Button { Content = "Marquer comme déclarée", Style = Sty("PrimaryButton"), IsEnabled = canMark };
            mark.Click += (_, _) =>
            {
                var note = PromptWindowAsk("Déclaration faite", $"Période : {period.Label}\nNote (facultatif : n° de déclaration, montant payé…) :");
                if (note is null) return;
                if (Ui.Try(() => _c.MarkUrssafDeclared(period, note))) Render();
            };
            actions.Children.Add(mark);
        }
        else
        {
            var unmark = new Button { Content = "Retirer la marque « déclarée »", Style = Sty("SecondaryButton"), IsEnabled = canMark };
            unmark.Click += (_, _) => { if (Ui.Try(() => _c.UnmarkUrssafDeclared(period.Key))) Render(); };
            actions.Children.Add(unmark);
        }
        Put(actions);

        // --- Plafonds et TVA
        var ceil = _c.ComputeUrssafCeilings(period.Start.Year);
        Card($"Plafonds et TVA — année {ceil.Year}");
        AddGauge("Chiffre d'affaires total / plafond du régime micro", ceil.TotalCents, ceil.Params.CeilingTotalCents, ceil.Params.CeilingTotalCents);
        AddGauge("Prestations de services / sous-plafond", ceil.ServicesCents, ceil.Params.CeilingServicesCents, ceil.Params.CeilingServicesCents);
        AddGauge("Ventes de marchandises / franchise de TVA", ceil.SalesCents, ceil.Params.VatSaleCents, ceil.Params.VatSaleMaxCents);
        AddGauge("Prestations de services / franchise de TVA", ceil.ServicesCents, ceil.Params.VatServiceCents, ceil.Params.VatServiceMaxCents);
        Put(Muted("Plafonds et seuils modifiables dans Paramétrage du dossier › Entreprise (ils sont révisés par l'État). " +
                                 "Dépasser le seuil de TVA rend la TVA exigible ; dépasser le plafond du régime micro fait sortir du régime. " +
                                 "Référence des valeurs par défaut : " + ToutCompteFait.Core.Business.UrssafParams.ReferenceLabel + "."));
        // Règle des deux dépassements consécutifs, prorata de l'année de création, dates d'exigibilité de la TVA.
        var micro = _c.ComputeMicroRegimeStatus(ceil.Year);
        AddNotice(micro.Level, micro.Message);
        foreach (var n in _c.ComputeVatNotices(ceil.Year)) AddNotice(n.Level, n.Message);
        if (ceil.Params.OutdatedFields().Count > 0)
            Put(Muted("⚠ Certains seuils ou taux portent encore les valeurs de 2023-2025 : mettez-les à jour dans " +
                                     "Paramétrage du dossier › Entreprise (bouton « Appliquer le barème 2026 »)."));

        // --- Historique
        Card("Historique des déclarations");
        var history = _c.Declarations();
        if (history.Count == 0) Put(Muted("Aucune déclaration marquée comme faite."));
        else
        {
            var list = new DataGrid { MaxHeight = 260, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
            list.Columns.Add(new DataGridTextColumn { Header = "Période", Binding = new System.Windows.Data.Binding("Label"), Width = 200 });
            list.Columns.Add(new DataGridTextColumn { Header = "Déclarée le", Binding = new System.Windows.Data.Binding("At"), Width = 130 });
            list.Columns.Add(new DataGridTextColumn { Header = "Chiffre d'affaires", Binding = new System.Windows.Data.Binding("Turnover"), Width = 140 });
            list.Columns.Add(new DataGridTextColumn { Header = "Note", Binding = new System.Windows.Data.Binding("Note"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            list.ItemsSource = history.Select(h => new
            {
                Label = $"{h.Start:dd/MM/yyyy} → {h.End:dd/MM/yyyy}",
                At = h.DeclaredAt.ToString("dd/MM/yyyy"),
                Turnover = Eur(h.TurnoverCents),
                Note = h.Note ?? ""
            }).ToList();
            Put(list);
        }
    }

    private static string? PromptWindowAsk(string title, string message) =>
        Dialogs.PromptWindow.Ask(Window.GetWindow(MainWindow.Current!), title, message, okText: "Marquer");

    /// <summary>Jauge : <paramref name="value"/> face à un seuil et à son seuil majoré (tolérance).</summary>
    private void AddGauge(string label, long value, long threshold, long hardLimit)
    {
        var ratio = threshold <= 0 ? 0 : Math.Min(1.0, (double)value / threshold);
        var over = threshold > 0 && value > threshold;
        var color = over ? Color.FromRgb(0xDC, 0x26, 0x26) : ratio >= 0.8 ? Color.FromRgb(0xD9, 0x77, 0x06) : Color.FromRgb(0x16, 0xA3, 0x4A);
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 8), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock
        {
            Text = $"{label} : {Eur(value)} / {Eur(threshold)}" + (hardLimit > threshold ? $" (seuil majoré {Eur(hardLimit)})" : "") +
                   (over ? "  — seuil dépassé" : $"  — {(threshold > 0 ? (double)value / threshold : 0):P0}")
        });
        panel.Children.Add(new ProgressBar
        {
            Minimum = 0, Maximum = 1, Value = ratio, Height = 8, Margin = new Thickness(0, 4, 0, 0), Foreground = new SolidColorBrush(color),
            Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF5)), BorderThickness = new Thickness(0)
        });
        Put(panel);
    }
}