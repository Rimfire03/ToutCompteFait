using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToutCompteFait.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Views;

/// <summary>Page « Statistiques » : trois onglets (produits, charges, opérations). Chacun propose un tableau de bord
/// (graphique, camembert et tableaux) et un onglet par élément avec son graphique en ligne ; un commutateur
/// Mensuel / Annuel agit sur tout l'onglet. Les produits et charges marqués « personnel » n'y figurent pas. Lecture seule.</summary>
public sealed class StatisticsView : UserControl, IPage
{
    private readonly List<StatKindPage> _pages = new();

    private readonly TabControl _tabs = new();

    /// <summary>Diagnostic (captures d'écran) : affiche le type d'élément, le mode et l'onglet voulus.</summary>
    public void ShowForShot(int kindIndex, bool annual, int itemTab)
    {
        _tabs.SelectedIndex = kindIndex;
        _pages[kindIndex].ShowForShot(annual, itemTab);
    }

    public StatisticsView(FiscalYear year)
    {
        var tabs = _tabs;
        foreach (var (kind, title) in new[]
                 {
                     (StatKind.Products, "Produits"), (StatKind.Charges, "Charges"), (StatKind.Operations, "Opérations")
                 })
        {
            var page = new StatKindPage(year, kind);
            _pages.Add(page);
            tabs.Items.Add(new TabItem { Header = title, Content = page });
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

/// <summary>Un type d'élément (produits, charges ou opérations) : tableau de bord + un onglet par élément.</summary>
internal sealed class StatKindPage : UserControl, IPage
{
    private readonly FiscalYear _year;
    private readonly StatKind _kind;
    private readonly MonthlyStatsPanel _monthly;
    private readonly ComparisonView _annual;
    private readonly RadioButton _monthlyRadio = new() { Content = "Mensuel", GroupName = "", IsChecked = true, Margin = new Thickness(0, 0, 14, 0) };
    private readonly RadioButton _annualRadio = new() { Content = "Annuel" };
    private readonly TabControl _tabs = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly Dictionary<string, ItemLinePanel> _items = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _itemNames = new();
    private bool _isMonthly = true;

    public StatKindPage(FiscalYear year, StatKind kind)
    {
        _year = year;
        _kind = kind;
        _monthly = new MonthlyStatsPanel(year, kind);
        _annual = new ComparisonView(year, kind) { Visibility = Visibility.Collapsed };

        // Les deux boutons radio ont un groupe propre à la page (plusieurs pages coexistent).
        var group = "stat-" + kind;
        _monthlyRadio.GroupName = group;
        _annualRadio.GroupName = group;
        _monthlyRadio.Checked += (_, _) => SetMode(true);
        _annualRadio.Checked += (_, _) => SetMode(false);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        bar.Children.Add(new TextBlock { Text = "Affichage :", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        bar.Children.Add(_monthlyRadio);
        bar.Children.Add(_annualRadio);

        var dashboard = new Grid();
        dashboard.Children.Add(_monthly);
        dashboard.Children.Add(_annual);
        _tabs.Items.Add(new TabItem { Header = "Tableau de bord", Content = dashboard });

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_tabs);
        Content = root;
        Reload();
    }

    public void ShowForShot(bool annual, int itemTab)
    {
        (annual ? _annualRadio : _monthlyRadio).IsChecked = true;
        _tabs.SelectedIndex = Math.Clamp(itemTab, 0, _tabs.Items.Count - 1);
    }

    private void SetMode(bool monthly)
    {
        _isMonthly = monthly;
        _monthly.Visibility = monthly ? Visibility.Visible : Visibility.Collapsed;
        _annual.Visibility = monthly ? Visibility.Collapsed : Visibility.Visible;
        foreach (var p in _items.Values) p.ShowMonthly(monthly);
    }

    public void Reload()
    {
        _monthly.Reload();
        _annual.Reload();
        var s = _monthly.Statistics;
        var cmp = _annual.Comparison;
        if (s is null) return;

        var (monthLines, yearLines) = _kind switch
        {
            StatKind.Products => (s.Products, cmp?.Products),
            StatKind.Charges => (s.Charges, cmp?.Charges),
            _ => (s.Operations, cmp?.Operations)
        };
        var months = s.Months.Select(m => m.ToString("MMM yy", CultureInfo.GetCultureInfo("fr-FR"))).ToArray();
        var yearLabels = _annual.YearLabels;

        var names = new List<string>();
        foreach (var l in monthLines) if (!names.Contains(l.Name, StringComparer.OrdinalIgnoreCase)) names.Add(l.Name);
        if (yearLines is not null)
            foreach (var l in yearLines) if (!names.Contains(l.Name, StringComparer.OrdinalIgnoreCase)) names.Add(l.Name);

        // Onglets d'éléments : reconstruits seulement si la liste change (la sélection courante est conservée).
        if (!names.SequenceEqual(_itemNames, StringComparer.OrdinalIgnoreCase))
        {
            var selected = (_tabs.SelectedItem as TabItem)?.Header as string;
            while (_tabs.Items.Count > 1) _tabs.Items.RemoveAt(1);
            _items.Clear();
            foreach (var name in names)
            {
                var panel = new ItemLinePanel(name, _kind);
                _items[name] = panel;
                _tabs.Items.Add(new TabItem { Header = name, Content = panel });
            }
            _itemNames = names;
            var again = _tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header as string == selected);
            _tabs.SelectedItem = again ?? _tabs.Items[0];
        }
        foreach (var name in names)
        {
            var m = monthLines.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            var y = yearLines?.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            _items[name].SetData(months, m?.Values ?? new long[months.Length], yearLabels, y?.Values ?? new long[yearLabels.Length]);
            _items[name].ShowMonthly(_isMonthly);
        }
    }
}

/// <summary>Graphique en ligne d'un élément : mois de l'exercice affiché ou exercices comparés.</summary>
internal sealed class ItemLinePanel : UserControl
{
    private readonly string _name;
    private readonly StatKind _kind;
    private readonly LineChart _chart = new() { Height = 320 };
    private readonly TextBlock _summary = new() { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    private string[] _months = Array.Empty<string>();
    private IReadOnlyList<long> _monthValues = Array.Empty<long>();
    private string[] _years = Array.Empty<string>();
    private IReadOnlyList<long> _yearValues = Array.Empty<long>();
    private bool _monthly = true;

    public ItemLinePanel(string name, StatKind kind)
    {
        _name = name;
        _kind = kind;
        _summary.Style = ComparisonView.Sty("MutedText");
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(ComparisonView.Card(_chart));
        stack.Children.Add(_summary);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
    }

    public void SetData(string[] months, IReadOnlyList<long> monthValues, string[] years, IReadOnlyList<long> yearValues)
    {
        _months = months;
        _monthValues = monthValues;
        _years = years;
        _yearValues = yearValues;
        Render();
    }

    public void ShowMonthly(bool monthly)
    {
        _monthly = monthly;
        Render();
    }

    private static string Eur(long cents) => Money.Format(cents) + " €";

    private void Render()
    {
        var (labels, values, scope) = _monthly ? (_months, _monthValues, "par mois") : (_years, _yearValues, "par exercice");
        var kindWord = _kind switch { StatKind.Products => "Produit", StatKind.Charges => "Charge", _ => "Résultat" };
        var color = ComparisonView.KindBrush(_kind, 0);
        _chart.SetData($"{kindWord} « {_name} » {scope}", labels,
            new[] { new BarChart.Series(_name, color, values.ToArray()) });
        if (values.Count == 0)
        {
            _summary.Text = "Aucune donnée.";
            return;
        }
        var total = values.Sum();
        var best = values.Select((v, i) => (v, i)).MaxBy(x => x.v);
        _summary.Text = $"Total : {Eur(total)} · moyenne : {Eur(total / Math.Max(1, values.Count))} · " +
                        $"maximum : {Eur(best.v)} ({(best.i < labels.Length ? labels[best.i] : "")})";
    }
}

/// <summary>Tableau de bord mensuel d'un type d'élément pour l'exercice affiché : graphique des principaux éléments mois
/// par mois, camembert de répartition et tableau complet (éléments × mois + total).</summary>
internal sealed class MonthlyStatsPanel : UserControl, IPage
{
    private const int TopItems = 8;
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private readonly FiscalYear _year;
    private readonly StatKind _kind;
    private readonly BarChart _chart = new() { Height = 340, ThinLabels = true };
    private readonly PieChart _pie = new();
    private readonly Grid _charts = new();
    private readonly StackPanel _tables = new();
    private readonly TextBlock _empty = new()
    {
        Text = "Aucun montant enregistré sur cet exercice.", Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
        Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed
    };

    /// <summary>Dernière statistique calculée (null avant le premier calcul).</summary>
    public MonthlyStatistics? Statistics { get; private set; }

    private sealed class Row
    {
        public string[] Cells { get; init; } = Array.Empty<string>();
    }

    public MonthlyStatsPanel(FiscalYear year, StatKind kind)
    {
        _year = year;
        _kind = kind;
        var charts = _charts;
        charts.Margin = new Thickness(0, 0, 0, 12);
        charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        var barCard = ComparisonView.Card(_chart);
        barCard.Margin = new Thickness(0, 0, 12, 0);
        var pieCard = ComparisonView.Card(_pie);
        pieCard.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(pieCard, 1);
        charts.Children.Add(barCard);
        charts.Children.Add(pieCard);

        var content = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        content.Children.Add(new TextBlock
        {
            Style = ComparisonView.Sty("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "Montants encaissés ou décaissés, mois par mois, selon la date des écritures ventilées (sans les régularisations)."
        });
        content.Children.Add(charts);
        content.Children.Add(_empty);
        content.Children.Add(_tables);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content };
        Reload();
    }

    public void Reload()
    {
        var s = App.Compta.ComputeMonthlyStatistics(_year.Id);
        Statistics = s;
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
            series.Add(new BarChart.Series(ranked[i].Name, ComparisonView.KindBrush(_kind, i), ranked[i].Values));
        if (ranked.Count > TopItems)
        {
            var others = new long[months.Length];
            foreach (var l in ranked.Skip(TopItems))
                for (var i = 0; i < others.Length; i++) others[i] += l.Values[i];
            series.Add(new BarChart.Series("Autres", ComparisonView.OthersBrush, others));
        }
        if (chartLines.Count == 0) _chart.SetGhost(title, months, "Aucune donnée pour l'instant");
        else _chart.SetData(title, months, series);

        // Camembert : revenus (produits, ou produits liés à chaque opération) ou, pour les charges, leur répartition.
        var (pieTitle, pieLines) = _kind switch
        {
            StatKind.Products => ("Répartition des revenus", s.Products),
            StatKind.Charges => ("Répartition des charges", s.Charges),
            _ => ("Revenus par opération", s.OperationProducts)
        };
        var pieRanked = pieLines.Select(l => (l.Name, Total: l.Values.Sum())).Where(x => x.Total > 0).OrderByDescending(x => x.Total).ToList();
        var slices = new List<PieChart.Slice>();
        for (var i = 0; i < pieRanked.Count && i < TopItems; i++)
            slices.Add(new PieChart.Slice(pieRanked[i].Name, ComparisonView.KindBrush(_kind, i), pieRanked[i].Total));
        if (pieRanked.Count > TopItems)
            slices.Add(new PieChart.Slice("Autres", ComparisonView.OthersBrush, pieRanked.Skip(TopItems).Sum(x => x.Total)));
        _pie.SetData(pieTitle, slices);

        _tables.Children.Clear();
        if (_kind == StatKind.Operations)
        {
            AddTable("Résultat par opération (produits − charges)", "Opération", months, s.Operations);
            AddTable("Produits par opération", "Opération", months, s.OperationProducts);
            AddTable("Charges par opération", "Opération", months, s.OperationCharges);
        }
        else AddTable(title, _kind == StatKind.Products ? "Produit" : "Charge", months, chartLines);
        // Sans donnée : les graphiques restent affichés en « fantôme » (cadre, grille, message) ; seul le tableau est masqué.
        var none = chartLines.Count == 0 && pieLines.Count == 0;
        _empty.Visibility = Visibility.Collapsed;
        _charts.Visibility = Visibility.Visible;
        _tables.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
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
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ItemsSource = rows, Margin = new Thickness(0, 0, 0, 16), SelectionMode = DataGridSelectionMode.Single
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = firstColumn, Binding = new System.Windows.Data.Binding("Cells[0]"),
            Width = new DataGridLength(1.6, DataGridLengthUnitType.Star), MinWidth = 110, ElementStyle = ComparisonView.Sty("LeftCell")
        });
        for (var i = 0; i < months.Length; i++) grid.Columns.Add(RightColumn(months[i], 1 + i, 1));
        grid.Columns.Add(RightColumn("Total", 1 + months.Length, 1.3));
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
        _tables.Children.Add(grid);
    }

    private static DataGridTextColumn RightColumn(string header, int index, double star) => new()
    {
        Header = header, Binding = new System.Windows.Data.Binding($"Cells[{index}]"),
        Width = new DataGridLength(star, DataGridLengthUnitType.Star), MinWidth = 50,
        ElementStyle = ComparisonView.Sty("RightCell"), HeaderStyle = ComparisonView.Sty("HeaderRight")
    };
}
