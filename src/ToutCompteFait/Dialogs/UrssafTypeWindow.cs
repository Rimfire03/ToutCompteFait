using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Business;

namespace ToutCompteFait.Dialogs;

/// <summary>Type de déclaration URSSAF (BIC vente, BIC prestation, BNC…) d'un produit (recette) déjà créé.</summary>
public sealed class UrssafTypeWindow : Window
{
    private sealed record Item(UrssafKind Kind, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly ComboBox _kind = new();

    public UrssafKind Kind => ((Item)_kind.SelectedItem).Kind;

    public UrssafTypeWindow(string productName, IReadOnlyCollection<UrssafKind> suggested, UrssafKind? current)
    {
        Title = "Type de déclaration URSSAF";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushSurface");

        var items = UrssafKinds.All.OrderBy(k => suggested.Contains(k) ? 0 : 1)
            .Select(k => new Item(k, k.Label() + (suggested.Contains(k) ? "  (d'après votre code APE)" : ""))).ToList();
        _kind.ItemsSource = items;
        _kind.SelectedItem = items.FirstOrDefault(i => i.Kind == current) ?? items[0];

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Produit « {productName} » : le type détermine la ligne du site de l'URSSAF où ces recettes sont déclarées."
        });
        panel.Children.Add(new TextBlock { Text = "Type de déclaration", Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(_kind);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Style = (Style)Application.Current.FindResource("SecondaryButton") };
        var ok = new Button { Content = "Enregistrer", IsDefault = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static (UrssafKind Kind, string? Name)? Ask(Window? owner, string productName, IReadOnlyCollection<UrssafKind> suggested, UrssafKind? current)
    {
        var w = new UrssafTypeWindow(productName, suggested, current) { Owner = owner };
        return w.ShowDialog() == true ? (w.Kind, null) : null;
    }
}
