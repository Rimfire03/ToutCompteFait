using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Dialogs;

/// <summary>Détail d'une catégorie (ou d'une opération = plusieurs catégories liées) du bilan : toutes les lignes du
/// livre qui y sont ventilées, plus les reports qui la corrigent, avec le total qui retrouve celui du bilan.</summary>
public sealed class CategoryDetailWindow : Window
{
    private sealed record DetailRow(string Date, string Label, string Account, string Category, string Origin,
        string Product, string Charge, string Comment);

    public CategoryDetailWindow(FiscalYear year, string title, IReadOnlyCollection<long> categoryIds)
    {
        var c = App.Compta;
        Title = title;
        Width = 980;
        Height = 560;
        MinWidth = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushBackground");

        var cats = c.Categories(year.Id).ToDictionary(k => k.Id);
        var accounts = c.Accounts(includeInactive: true).ToDictionary(a => a.Id, a => a.Name);
        var ids = categoryIds.ToHashSet();

        var rows = new List<(DateOnly Date, int Order, DetailRow Row)>();
        long products = 0, charges = 0;

        void Add(DateOnly date, int order, string label, string account, Category cat, string origin, long cents, string comment)
        {
            // Produit : crédit (+) ; charge : dépense (+) ; un montant négatif (remboursement, report en moins) se lit en négatif.
            var isProduct = cat.Kind == CategoryKind.Product;
            if (isProduct) products += cents; else charges += cents;
            rows.Add((date, order, new DetailRow(
                origin == "Report" ? "" : date.ToString("dd/MM/yyyy"), label, account, cat.Name, origin,
                isProduct ? Money.Format(cents) : "", isProduct ? "" : Money.Format(cents), comment)));
        }

        foreach (var r in c.Entries(year.Id))
        {
            var e = r.Entry;
            foreach (var a in r.Allocations.Where(a => ids.Contains(a.CategoryId)))
            {
                if (!cats.TryGetValue(a.CategoryId, out var cat)) continue;
                var account = e.AccountId is { } id && accounts.TryGetValue(id, out var n) ? n : "—";
                var origin = e.Kind == EntryKind.MemberRefund ? "Remboursement membre" : "Livre";
                var comment = string.Join(" · ", new[] { e.Note?.Trim(), e.MemberName }.Where(s => !string.IsNullOrWhiteSpace(s)));
                Add(e.Date, 0, e.Label, account, cat, origin, a.AmountCents, comment);
            }
        }
        foreach (var adj in c.Adjustments(year.Id).Where(a => a.CategoryId is { } cid && ids.Contains(cid)))
        {
            if (!cats.TryGetValue(adj.CategoryId!.Value, out var cat)) continue;
            var effect = adj.Sign * adj.AmountCents;
            Add(year.End, 1, $"Report : {adj.Label}", "—", cat, "Report", effect, Labels.Of(adj.Type) +
                (string.IsNullOrWhiteSpace(adj.Note) ? "" : " · " + adj.Note));
        }

        var ordered = rows.OrderBy(x => x.Order).ThenBy(x => x.Date).Select(x => x.Row).ToList();

        var root = new DockPanel { Margin = new Thickness(18) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("TitleText") });
        header.Children.Add(new TextBlock
        {
            Style = (Style)Application.Current.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap,
            Text = $"Exercice {year.Label} — {ordered.Count} ligne(s) du livre et reports qui composent ce montant. " +
                   "Pour corriger une ligne, ouvrez les Opérations bancaires."
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var totals = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold,
            Text = $"Produits {Money.Format(products)} €  ·  Charges {Money.Format(charges)} €  ·  Résultat {Money.Format(products - charges)} €"
        };
        DockPanel.SetDock(totals, Dock.Bottom);
        root.Children.Add(totals);

        var close = new Button
        {
            Content = "Fermer", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0),
            Style = (Style)Application.Current.FindResource("SecondaryButton")
        };
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);

        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, Margin = new Thickness(0, 12, 0, 0),
            ItemsSource = ordered
        };
        void Col(string header, string path, double width, bool right = false, bool star = false)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = new System.Windows.Data.Binding(path),
                Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
                ElementStyle = (Style)Application.Current.FindResource(right ? "RightCell" : "LeftCell"),
                HeaderStyle = right ? (Style)Application.Current.FindResource("HeaderRight") : null
            });
        }
        Col("Date", nameof(DetailRow.Date), 90);
        Col("Libellé", nameof(DetailRow.Label), 0, star: true);
        Col("Compte", nameof(DetailRow.Account), 110);
        Col("Catégorie", nameof(DetailRow.Category), 150);
        Col("Origine", nameof(DetailRow.Origin), 110);
        Col("Produit", nameof(DetailRow.Product), 90, right: true);
        Col("Charge", nameof(DetailRow.Charge), 90, right: true);
        Col("Commentaire", nameof(DetailRow.Comment), 180);
        root.Children.Add(grid);
        Content = root;
    }
}
