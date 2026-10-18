using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToutCompteFait.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Views;

/// <summary>Éléments comparés par la statistique : produits, charges ou opérations.</summary>
public enum StatKind { Products, Charges, Operations }

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
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly BarChart _chartResult = new();
    private readonly BarChart _chartProducts = new();
    private readonly BarChart _chartCharges = new();
    private readonly StackPanel _tables = new();
    private readonly Border _empty = new()
    {
        Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(28), CornerRadius = new CornerRadius(12),
        Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)), BorderThickness = new Thickness(1)
    };
    private YearComparison? _cmp;
    private bool _loading;

    private readonly StatKind _kind;

    public ComparisonView(FiscalYear year, StatKind kind = StatKind.Operations)
    {
        _year = year;
        _kind = kind;
        var root = new DockPanel { Margin = new Thickness(0) };
        var head = new StackPanel();
        var what = kind switch { StatKind.Products => "produit", StatKind.Charges => "charge", _ => "opération" };
        head.Children.Add(new TextBlock { Text = $"Statistiques annuelles — {year.Label}", Style = Sty("TitleText") });
        head.Children.Add(new TextBlock
        {
            Style = Sty("MutedText"), TextWrapping = TextWrapping.Wrap,
            Text = $"Compare, {what} par {what}, l'exercice affiché (N) aux exercices précédents (N-1, N-2…). " +
                   "Les éléments sont regroupés par nom d'un exercice à l'autre ; montants des comptes annuels (régularisations et reports comprises)." +
                   (kind == StatKind.Operations ? " Produits, charges et résultat de chaque opération." : "") +
                   " Les éléments marqués « personnel » n'y figurent pas."
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

        var charts = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var ch in Charts)
        {
            ch.Height = 300;
            var card = Card(ch);
            card.Margin = new Thickness(0, 0, 0, 12);
            charts.Children.Add(card);
        }

        var emptyText = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520 };
        emptyText.Children.Add(new TextBlock { Text = "📊", FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center });
        emptyText.Children.Add(new TextBlock
        {
            Text = kind == StatKind.Operations ? "Aucune opération à comparer" : "Rien à comparer", FontSize = 16, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0)
        });
        emptyText.Children.Add(new TextBlock
        {
            Text = kind == StatKind.Operations
                ? "Une opération regroupe les produits et charges d'un même événement (ex. Loto Octobre). Créez-en dans " +
                  "Paramétrage du dossier → Opérations : leur résultat sera comparé ici, exercice par exercice."
                : "Aucun montant enregistré sur les exercices comparés.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Grey, Margin = new Thickness(0, 6, 0, 0)
        });
        _empty.Child = emptyText;

        var content = new StackPanel();
        content.Children.Add(_empty);
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
        Reload();
    }

    internal static Style Sty(string key) => (Style)Application.Current.FindResource(key);

    internal static Brush PaletteBrush(int i) => Palette[i % Palette.Length];

    // Couleurs par sens : produits en verts / turquoises, charges en ambres / orangés, opérations en bleus / violets.
    // La même couleur garde la même signification d'un écran à l'autre (le rouge reste réservé aux alertes).
    private static readonly Brush[] ProductPalette = Hex("16A34A", "0E9F6E", "65A30D", "0E7490", "15803D", "34D399", "84CC16", "0D9488");
    private static readonly Brush[] ChargePalette = Hex("D97706", "B45309", "F59E0B", "C2410C", "92400E", "FBBF24", "EA580C", "A16207");
    private static readonly Brush[] OperationPalette = Hex("2563EB", "7C3AED", "0284C7", "4F46E5", "6D28D9", "38BDF8", "1D4ED8", "8B5CF6");

    private static Brush[] Hex(params string[] hex) =>
        hex.Select(h => (Brush)new SolidColorBrush(Color.FromRgb(Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..], 16)))).ToArray();

    /// <summary>Gris neutre du regroupement « Autres ».</summary>
    internal static readonly Brush OthersBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    internal static Brush KindBrush(StatKind kind, int i)
    {
        var p = kind switch { StatKind.Products => ProductPalette, StatKind.Charges => ChargePalette, _ => OperationPalette };
        return p[i % p.Length];
    }

    internal static Border Card(UIElement child) => new()
    {
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Background = Brushes.White, Child = child, ClipToBounds = true
    };

    /// <summary>Dernière comparaison calculée (exercices du plus ancien au plus récent) ; null avant le premier calcul.</summary>
    internal YearComparison? Comparison => _cmp;

    /// <summary>Libellés des exercices comparés (« en cours » pour un exercice non clôturé).</summary>
    internal string[] YearLabels => _cmp is null ? Array.Empty<string>() : Labels;

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

            // Une couleur par exercice, colonnes côte à côte pour chaque opération (du plus ancien au plus récent).
            var ops = Main;
            var names = ops.Select(o => o.Name).ToArray();
            BarChart.Series[] ByYear(IReadOnlyList<ComparisonLine> lines) =>
                Enumerable.Range(0, years.Count)
                    .Select(i => new BarChart.Series(Labels[i], Palette[i % Palette.Length], lines.Select(o => o.Values[i]).ToArray()))
                    .ToArray();
            switch (_kind)
            {
                case StatKind.Products: _chartResult.SetData("Produits par exercice", names, ByYear(_cmp.Products)); break;
                case StatKind.Charges: _chartResult.SetData("Charges par exercice", names, ByYear(_cmp.Charges)); break;
                default:
                    _chartResult.SetData("Résultat par opération (produits − charges)", names, ByYear(_cmp.Operations));
                    _chartProducts.SetData("Produits par opération", names, ByYear(_cmp.OperationProducts));
                    _chartCharges.SetData("Charges par opération", names, ByYear(_cmp.OperationCharges));
                    break;
            }
            BuildTables();
            var none = ops.Count == 0;
            // Sans donnée : graphiques « fantômes » (cadre, grille, message) à la place d'un écran blanc ; tableaux masqués.
            if (none)
            {
                var titles = _kind switch
                {
                    StatKind.Products => new[] { "Produits par exercice" },
                    StatKind.Charges => new[] { "Charges par exercice" },
                    _ => new[] { "Résultat par opération (produits − charges)", "Produits par opération", "Charges par opération" }
                };
                var charts = Charts;
                for (var i = 0; i < charts.Length && i < titles.Length; i++)
                    charts[i].SetGhost(titles[i], Labels, "Aucune donnée pour l'instant");
            }
            _empty.Visibility = Visibility.Collapsed;
            _tables.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
            foreach (var ch in Charts)
                (ch.Parent is Border card ? card : (FrameworkElement)ch).Visibility = Visibility.Visible;
        }
        finally { _loading = false; }
    }

    // ------------------------------------------------------------------ tableaux

    private void BuildTables()
    {
        _tables.Children.Clear();
        var c = _cmp!;
        switch (_kind)
        {
            case StatKind.Products: AddTable("Produits par exercice", "Produit", c.Products, total: true); break;
            case StatKind.Charges: AddTable("Charges par exercice", "Charge", c.Charges, total: true); break;
            default:
                AddTable("Résultat par opération (produits − charges)", "Opération", c.Operations, total: true);
                AddTable("Produits par opération", "Opération", c.OperationProducts, total: true);
                AddTable("Charges par opération", "Opération", c.OperationCharges, total: true);
                break;
        }
    }

    private BarChart[] Charts => _kind == StatKind.Operations ? new[] { _chartResult, _chartProducts, _chartCharges } : new[] { _chartResult };

    private IReadOnlyList<ComparisonLine> Main => _kind switch
    {
        StatKind.Products => _cmp!.Products,
        StatKind.Charges => _cmp!.Charges,
        _ => _cmp!.Operations
    };

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
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ItemsSource = rows, Margin = new Thickness(0, 0, 0, 16),
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
        _tables.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        _tables.Children.Add(grid);
        if (lines.Count == 0)
            _tables.Children.Add(new TextBlock { Text = "Aucune donnée.", Foreground = Grey, Margin = new Thickness(0, -10, 0, 16) });
    }

    private DataGridTextColumn RightColumn(string header, int index, double width) => new()
    {
        Header = header, Binding = new System.Windows.Data.Binding($"Cells[{index}]"),
        // Colonnes proportionnelles : le tableau tient toujours dans la largeur, sans défilement horizontal.
        Width = new DataGridLength(width / 100.0, DataGridLengthUnitType.Star), MinWidth = 64,
        ElementStyle = Sty("RightCell"), HeaderStyle = Sty("HeaderRight")
    };
}
