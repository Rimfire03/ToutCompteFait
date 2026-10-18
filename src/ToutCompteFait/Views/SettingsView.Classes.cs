using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Views;

/// <summary>Onglet « Comptes comptables » du paramétrage du dossier : vue en lecture seule des catégories rangées par classe
/// du plan comptable général, avec un rappel simple de ce que signifient les classes. Les numéros se modifient dans
/// l'export FEC (numéros de compte) ; cette vue ne change aucune donnée.</summary>
public partial class SettingsView
{
    private static readonly (string Digit, string Name, string Help, string Where)[] AccountClasses =
    {
        ("1", "Capitaux", "Capital, réserves, emprunts : ce qui finance l'activité.", "Non suivi : le logiciel tient la trésorerie."),
        ("2", "Immobilisations", "Matériel et biens durables (ordinateur, machine).", "Onglet Immobilisations (si activé)."),
        ("3", "Stocks", "Marchandises et matières en stock.", "Non suivi."),
        ("4", "Tiers", "Clients (41), fournisseurs (40), État (44) : créances et dettes.", "Non suivi : seuls les encaissements et décaissements comptent."),
        ("5", "Financiers", "Comptes bancaires, livrets, caisse.", "Vos comptes bancaires."),
        ("6", "Charges", "Achats, services extérieurs, impôts, cotisations : ce qui sort.", "Vos charges (ci-dessous)."),
        ("7", "Produits", "Ventes, prestations, subventions : ce qui rentre.", "Vos produits (ci-dessous)."),
        ("8", "Spéciaux", "Engagements hors bilan.", "Non utilisé.")
    };

    private sealed class ClassRow
    {
        public string Group { get; init; } = "";
        public string Number { get; init; } = "";
        public string Name { get; init; } = "";
        public string Origin { get; init; } = "";
    }

    private void InitClasses()
    {
        if (_scope != SettingsScope.Dossier) return;
        Tabs.Items.Add(new TabItem { Header = "Comptes comptables", Content = BuildClassesPanel() });
    }

    private UIElement BuildClassesPanel()
    {
        var panel = new StackPanel { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = "Le plan comptable général classe tout en 8 classes numérotées. Vous n'avez pas besoin de le maîtriser : le logiciel range " +
                   "automatiquement vos produits en classe 7 et vos charges en classe 6, avec un numéro de compte usuel (modifiable dans l'export FEC)."
        });

        panel.Children.Add(new TextBlock { Text = "Les 8 classes en bref", Style = (Style)FindResource("SectionText"), Margin = new Thickness(0, 0, 0, 6) });
        var classes = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        classes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        classes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        classes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        classes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < AccountClasses.Length; i++)
        {
            classes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var c = AccountClasses[i];
            void Cell(string text, int col, bool bold = false, bool muted = false)
            {
                var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 10, 3), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
                if (muted) tb.Style = (Style)FindResource("MutedText");
                Grid.SetRow(tb, i);
                Grid.SetColumn(tb, col);
                classes.Children.Add(tb);
            }
            Cell(c.Digit, 0, bold: true);
            Cell(c.Name, 1, bold: true);
            Cell(c.Help, 2);
            Cell(c.Where, 3, muted: true);
        }
        panel.Children.Add(classes);

        panel.Children.Add(new TextBlock { Text = "Vos produits et charges par classe", Style = (Style)FindResource("SectionText"), Margin = new Thickness(0, 4, 0, 6) });
        var rows = _c.CategoryAccountNumbers()
            .Select(r => new ClassRow
            {
                Group = r.Effective.StartsWith('7') ? "Classe 7 — Produits" : r.Effective.StartsWith('6') ? "Classe 6 — Charges" : "Classe " + r.Effective[0] + " — Autres",
                Number = r.Effective,
                Name = r.Name,
                Origin = r.Number is null ? "numéro automatique" : "numéro choisi"
            })
            .OrderBy(r => r.Number, StringComparer.Ordinal).ToList();
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 360
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Numéro", Binding = new System.Windows.Data.Binding(nameof(ClassRow.Number)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Catégorie", Binding = new System.Windows.Data.Binding(nameof(ClassRow.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Origine du numéro", Binding = new System.Windows.Data.Binding(nameof(ClassRow.Origin)), Width = 170 });
        var view = new System.Windows.Data.ListCollectionView(rows);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ClassRow.Group)));
        grid.ItemsSource = view;
        grid.GroupStyle.Add(new GroupStyle
        {
            HeaderTemplate = BuildGroupHeader()
        });
        panel.Children.Add(grid);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
    }

    private static DataTemplate BuildGroupHeader()
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
        factory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        factory.SetValue(TextBlock.MarginProperty, new Thickness(6, 8, 0, 4));
        return new DataTemplate { VisualTree = factory };
    }
}
