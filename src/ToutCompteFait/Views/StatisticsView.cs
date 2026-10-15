using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToutCompteFait.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Views;

/// <summary>Page « Statistique » : trois onglets (produits, charges, opérations), chacun avec une vue mensuelle
/// (exercice affiché, mois par mois) et une vue annuelle (comparaison des exercices, voir <see cref="ComparisonView"/>).
/// Les produits et charges marqués « personnel » n'y figurent pas. Lecture seule.</summary>
public sealed class StatisticsView : UserControl, IPage
{
    private readonly List<IPage> _pages = new();

    public StatisticsView(FiscalYear year)
    {
        var tabs = new TabControl();
        foreach (var (kind, title) in new[]
                 {
                     (StatKind.Products, "Produits"), (StatKind.Charges, "Charges"), (StatKind.Operations, "Opérations")
                 })
        {
            var monthly = new MonthlyStatsPanel(year, kind);
            var annual = new ComparisonView(year, kind);
            _pages.Add(monthly);
            _pages.Add(annual);
            var inner = new TabControl { Margin = new Thickness(0, 8, 0, 0) };
            inner.Items.Add(new TabItem { Header = "Mensuelle", Content = monthly });
            inner.Items.Add(new TabItem { Header = "Annuelle", Content = annual });
            tabs.Items.Add(new TabItem { Header = title, Content = inner });
        }
        var root = new DockPanel();
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new TextBlock { Text = $"Statistiques — {year.Label}", Style = ComparisonView.Sty("TitleText") });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(tabs);
        Content = root;
    }

    public void Reload()
    {
        foreach (var p in _pages) p.Reload();
    }
}

/// <summary>Statistique mensuelle d'un type d'élément pour l'exercice affiché : graphique des principaux éléments
/// mois par mois et tableau complet (éléments × mois + total).</summary>
internal sealed class MonthlyStatsPanel : UserControl, IPage
{
    private const int TopItems = 8;
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private readonly FiscalYear _year;
    private readonly StatKind _kind;
    private readonly BarChart _chart = new() { Height = 320 };
    private readonly StackPanel _tables = new();
    private readonly TextBlock _empty = new()
    {
        Text = "Aucun montant enregistré sur cet exercice.", Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
        Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed
    };

    private sealed class Row
    {
        public string[] Cells { get; init; } = Array.Empty<string>();
    }

    public MonthlyStatsPanel(FiscalYear year, StatKind kind)
    {
        _year = year;
        _kind = kind;
        var holder = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _chart
        };
        var card = ComparisonView.Card(holder);
        card.Margin = new Thickness(0, 0, 0, 12);
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Style = ComparisonView.Sty("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "Montants encaissés ou décaissés, mois par mois, selon la date des écritures ventilées (sans les régularisations)."
        });
        content.Children.Add(card);
        content.Children.Add(_empty);
        content.Children.Add(_tables);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content };
        Reload();
    }

    public void Reload()
    {
        var s = App.Compta.ComputeMonthlyStatistics(_year.Id);
        var months = s.Months.Select(m => m.ToString("MMM yy", Fr)).ToArray();
        var (title, chartLines) = _kind switch
        {
            StatKind.Products => ("Produits par mois", s.Products),
            StatKind.Charges => ("Charges par mois", s.Charges),
            _ => ("Résultat par opération et par mois", s.Operations)
        };

        // Principaux éléments (par montant absolu), le reste regroupé.
        var ranked = chartLines.OrderByDescending(l => l.Values.Sum(Math.Abs)).ToList();
        var series = new List<BarChart.Series>();
        for (var i = 0; i < ranked.Count && i < TopItems; i++)
            series.Add(new BarChart.Series(ranked[i].Name, ComparisonView.PaletteBrush(i), ranked[i].Values));
        if (ranked.Count > TopItems)
        {
            var others = new long[months.Length];
            foreach (var l in ranked.Skip(TopItems))
                for (var i = 0; i < others.Length; i++) others[i] += l.Values[i];
            series.Add(new BarChart.Series("Autres", ComparisonView.PaletteBrush(TopItems), others));
        }
        _chart.SetData(title, months, series);
        _chart.MinWidth = Math.Max(700, months.Length * Math.Max(1, series.Count) * 30 + 120);

        _tables.Children.Clear();
        if (_kind == StatKind.Operations)
        {
            AddTable("Résultat par opération (produits − charges)", "Opération", months, s.Operations);
            AddTable("Produits par opération", "Opération", months, s.OperationProducts);
            AddTable("Charges par opération", "Opération", months, s.OperationCharges);
        }
        else AddTable(title, _kind == StatKind.Products ? "Produit" : "Charge", months, chartLines);
        _empty.Visibility = chartLines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddTable(string title, string firstColumn, string[] months, IReadOnlyList<ComparisonLine> lines)
    {
        var rows = new List<Row>();
        foreach (var l in lines) rows.Add(MakeRow(l.Name, l.Values));
        if (lines.Count > 0)
        {
            var sums = new long[months.Length];
            foreach (var l in lines) for (var i = 0; i < sums.Length; i++) sums[i] += l.Values[i];
            rows.Add(MakeRow("TOTAL", sums));
        }

        static Row MakeRow(string name, IReadOnlyList<long> v)
        {
            var cells = new List<string> { name };
            cells.AddRange(v.Select(x => x == 0 ? "" : Money.Format(x)));
            cells.Add(Money.Format(v.Sum()) + " €");
            return new Row { Cells = cells.ToArray() };
        }

        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, ItemsSource = rows, Margin = new Thickness(0, 0, 0, 16),
            SelectionMode = DataGridSelectionMode.Single
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = firstColumn, Binding = new System.Windows.Data.Binding("Cells[0]"),
            Width = new DataGridLength(180), ElementStyle = ComparisonView.Sty("LeftCell")
        });
        for (var i = 0; i < months.Length; i++) grid.Columns.Add(RightColumn(months[i], 1 + i, 78));
        grid.Columns.Add(RightColumn("Total", 1 + months.Length, 110));
        grid.PreviewMouseWheel += (_, e) =>
        {
            // Le défilement appartient à la page, pas au tableau.
            e.Handled = true;
            ((UIElement)grid.Parent).RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent, Source = grid
            });
        };
        _tables.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        _tables.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = grid
        });
    }

    private static DataGridTextColumn RightColumn(string header, int index, double width) => new()
    {
        Header = header, Binding = new System.Windows.Data.Binding($"Cells[{index}]"),
        Width = new DataGridLength(width), ElementStyle = ComparisonView.Sty("RightCell"), HeaderStyle = ComparisonView.Sty("HeaderRight")
    };
}
