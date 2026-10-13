using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToutCompteFait.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Views;

/// <summary>Comparaison entre exercices : l'exercice affiché (N) et les précédents (N-1 … N-x, nombre réglable) en
/// tableaux chiffrés (synthèse, produits, charges, opérations) et en graphiques. Lecture seule.</summary>
public sealed class ComparisonView : UserControl, IPage
{
    private sealed class Row
    {
        public string[] Cells { get; init; } = Array.Empty<string>();
    }

    private sealed record Pick(string Display, string Kind, ComparisonLine Line);

    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x04, 0x78, 0x57));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xB9, 0x1C, 0x1C));
    private static readonly SolidColorBrush Blue = new(Color.FromRgb(0x1D, 0x4E, 0xD8));
    private static readonly Brush[] Palette =
    {
        new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)), new SolidColorBrush(Color.FromRgb(0x0E, 0x9F, 0x6E)),
        new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)), new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
        new SolidColorBrush(Color.FromRgb(0xDB, 0x27, 0x77)), new SolidColorBrush(Color.FromRgb(0x0E, 0x74, 0x90)),
        new SolidColorBrush(Color.FromRgb(0x65, 0xA3, 0x0D)), new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
        new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)), new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5))
    };
    private static readonly SolidColorBrush Grey = new(Color.FromRgb(0x47, 0x55, 0x69));

    private readonly FiscalYear _year;
    private readonly ComboBox _count = new() { Width = 70, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _picker = new() { MinWidth = 280, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly BarChart _overview = new();
    private readonly BarChart _detail = new();
    private readonly StackPanel _tables = new();
    private YearComparison? _cmp;
    private bool _loading;

    public ComparisonView(FiscalYear year)
    {
        _year = year;
        var root = new DockPanel { Margin = new Thickness(0) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = $"Comparaison entre exercices — {year.Label}", Style = Sty("TitleText") });
        head.Children.Add(new TextBlock
        {
            Style = Sty("MutedText"), TextWrapping = TextWrapping.Wrap,
            Text = "Compare, opération par opération, l'exercice affiché (N) aux exercices précédents (N-1, N-2…) : produits, charges et résultat de chaque opération. " +
                   "Les opérations sont regroupées par nom d'un exercice à l'autre ; montants des comptes annuels (régularisations et reports comprises)."
        });
        var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(new TextBlock { Text = "Exercices comparés (N compris) :", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        for (var i = 2; i <= 10; i++) _count.Items.Add(i);
        _count.SelectedItem = Math.Clamp(App.Settings.ComparisonYears, 2, 10);
        bar.Children.Add(_count);
        head.Children.Add(bar);
        head.Children.Add(_info);
        DockPanel.SetDock(head, Dock.Top);
        head.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(head);

        var charts = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        charts.ColumnDefinitions.Add(new ColumnDefinition());
        charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        charts.ColumnDefinitions.Add(new ColumnDefinition());
        var left = Card(_overview);
        var detail = new DockPanel();
        var pickBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        pickBar.Children.Add(new TextBlock { Text = "Détail de l'opération :", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        pickBar.Children.Add(_picker);
        DockPanel.SetDock(pickBar, Dock.Top);
        detail.Children.Add(pickBar);
        detail.Children.Add(Card(_detail));
        Grid.SetColumn(left, 0);
        Grid.SetColumn(detail, 2);
        charts.Children.Add(left);
        charts.Children.Add(detail);

        var content = new StackPanel();
        content.Children.Add(charts);
        content.Children.Add(_tables);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content };
        root.Children.Add(scroll);
        Content = root;

        _count.SelectionChanged += (_, _) =>
        {
            if (_loading || _count.SelectedItem is not int n) return;
            App.Settings.ComparisonYears = n;
            try { App.Settings.Save(); } catch { /* réglage de confort */ }
            Reload();
        };
        _picker.SelectionChanged += (_, _) => { if (!_loading) DrawDetail(); };
        Reload();
    }

    private static Style Sty(string key) => (Style)Application.Current.FindResource(key);

    private static Border Card(UIElement child) => new()
    {
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Background = Brushes.White, Child = child, ClipToBounds = true
    };

    private string[] Labels => _cmp!.Years.Select(y => y.Validated ? y.Label : y.Label + " (en cours)").ToArray();

    public void Reload()
    {
        _loading = true;
        try
        {
            var count = _count.SelectedItem is int n ? n : 3;
            _cmp = App.Compta.CompareYears(_year.Id, count);
            var years = _cmp.Years;
            _info.Text = years.Count < count
                ? $"{years.Count} exercice(s) disponible(s) jusqu'à {_year.Label} : les exercices précédents n'existent pas encore."
                : $"Exercices comparés : {string.Join(" → ", years.Select(y => y.Label))}.";
            _info.Foreground = Grey;

            // Graphique d'ensemble : résultat de chaque opération, une couleur par exercice
            var ops = _cmp.Operations;
            var names = ops.Select(o => o.Name).ToArray();
            var series = new List<BarChart.Series>();
            for (var i = 0; i < years.Count; i++)
                series.Add(new BarChart.Series(Labels[i], Palette[i % Palette.Length], ops.Select(o => o.Values[i]).ToArray()));
            _overview.SetData("Résultat par opération (produits − charges)", names, series);

            var keep = (_picker.SelectedItem as Pick)?.Display;
            var picks = ops.Select(l => new Pick(l.Name, "O", l)).ToList();
            _picker.ItemsSource = picks;
            _picker.DisplayMemberPath = nameof(Pick.Display);
            _picker.SelectedItem = picks.FirstOrDefault(p => p.Display == keep) ?? picks.FirstOrDefault();

            BuildTables();
        }
        finally { _loading = false; }
        DrawDetail();
    }

    private void DrawDetail()
    {
        if (_cmp is null) return;
        if (_picker.SelectedItem is not Pick p)
        {
            _detail.SetData("Aucune donnée à comparer", Labels, Array.Empty<BarChart.Series>());
            return;
        }
        var i = _cmp.Operations.ToList().FindIndex(o => o.Name == p.Line.Name);
        _detail.SetData("Opération : " + p.Display, Labels, new[]
        {
            new BarChart.Series("Produits", Green, _cmp.OperationProducts[i].Values),
            new BarChart.Series("Charges", Red, _cmp.OperationCharges[i].Values),
            new BarChart.Series("Résultat", Blue, p.Line.Values)
        });
    }

    // ------------------------------------------------------------------ tableaux

    private void BuildTables()
    {
        _tables.Children.Clear();
        var c = _cmp!;
        AddTable("Résultat par opération (produits − charges)", "Opération", c.Operations, total: true);
        AddTable("Produits par opération", "Opération", c.OperationProducts, total: true);
        AddTable("Charges par opération", "Opération", c.OperationCharges, total: true);
    }

    private static string Signed(long cents) => (cents > 0 ? "+" : "") + Money.Format(cents) + " €";

    private void AddTable(string title, string firstColumn, IReadOnlyList<ComparisonLine> lines, bool total)
    {
        var c = _cmp!;
        var n = c.Years.Count;
        var rows = new List<Row>();
        foreach (var l in lines) rows.Add(MakeRow(l.Name, l));
        if (total && lines.Count > 0)
        {
            var sums = new long[n];
            foreach (var l in lines) for (var i = 0; i < n; i++) sums[i] += l.Values[i];
            rows.Add(MakeRow("TOTAL", new ComparisonLine("TOTAL", sums)));
        }

        Row MakeRow(string name, ComparisonLine l)
        {
            // Colonnes : libellé, exercices du plus récent (N) au plus ancien, écart, %.
            var cells = new List<string> { name };
            for (var i = n - 1; i >= 0; i--) cells.Add(Money.Format(l.Values[i]) + " €");
            cells.Add(n > 1 ? Signed(l.Delta) : "");
            cells.Add(n > 1 ? (l.Percent is { } p ? (p > 0 ? "+" : "") + p.ToString("N1", CultureInfo.GetCultureInfo("fr-FR")) + " %" : "—") : "");
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
            Width = new DataGridLength(1, DataGridLengthUnitType.Star), ElementStyle = Sty("LeftCell")
        });
        var labels = Labels;
        for (var i = 0; i < n; i++)
        {
            var year = n - 1 - i;
            var head = i == 0 ? $"N · {labels[year]}" : $"N-{i} · {labels[year]}";
            grid.Columns.Add(RightColumn(head, 1 + i, 140));
        }
        if (n > 1)
        {
            grid.Columns.Add(RightColumn("Écart N / N-1", 1 + n, 130));
            grid.Columns.Add(RightColumn("%", 2 + n, 80));
        }
        grid.PreviewMouseWheel += (_, e) =>
        {
            // Le défilement appartient à la page, pas au tableau.
            e.Handled = true;
            var parent = (UIElement)grid.Parent;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent, Source = grid
            });
        };
        // Un clic sur une ligne de catégorie ou d'opération affiche son évolution dans le graphique de détail.
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is not Row r || _loading) return;
            if (r.Cells[0] == "TOTAL") return;
            var pick = (_picker.ItemsSource as IEnumerable<Pick>)?.FirstOrDefault(p => p.Line.Name == r.Cells[0]);
            if (pick is not null) _picker.SelectedItem = pick;
        };

        _tables.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        _tables.Children.Add(grid);
        if (lines.Count == 0)
            _tables.Children.Add(new TextBlock { Text = "Aucune donnée.", Foreground = Grey, Margin = new Thickness(0, -10, 0, 16) });
    }

    private DataGridTextColumn RightColumn(string header, int index, double width) => new()
    {
        Header = header, Binding = new System.Windows.Data.Binding($"Cells[{index}]"),
        Width = new DataGridLength(width), ElementStyle = Sty("RightCell"), HeaderStyle = Sty("HeaderRight")
    };
}
