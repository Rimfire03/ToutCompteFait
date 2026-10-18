using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Business;

namespace ToutCompteFait.Dialogs;

/// <summary>Choix d'un modèle de catégories par activité, puis des catégories à ajouter (toutes cochées par défaut ; les
/// catégories déjà présentes sont signalées et ne sont pas touchées).</summary>
public sealed class CategoryTemplateWindow : Window
{
    private readonly HashSet<string> _existing;
    private readonly ComboBox _template = new() { DisplayMemberPath = nameof(CategoryTemplate.Name) };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
    private readonly StackPanel _items = new();
    private readonly List<(CheckBox Box, TemplateItem Item)> _boxes = new();

    /// <summary>Catégories cochées à ajouter.</summary>
    public IReadOnlyList<TemplateItem> Selected => _boxes.Where(b => b.Box.IsChecked == true && b.Box.IsEnabled).Select(b => b.Item).ToList();

    public CategoryTemplateWindow(IEnumerable<Category> existingCategories)
    {
        _existing = existingCategories.Select(c => $"{c.Kind}|{c.Name.ToLowerInvariant()}").ToHashSet();
        Title = "Modèles de catégories";
        Width = 560;
        MaxHeight = 700;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushSurface");

        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 8) };
        panel.Children.Add(new TextBlock { Text = "Catégories prêtes à l'emploi", Style = (Style)Application.Current.FindResource("TitleText") });
        panel.Children.Add(new TextBlock
        {
            Style = (Style)Application.Current.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 10),
            Text = "Choisissez votre activité : les produits et charges courants sont ajoutés avec un numéro de compte usuel " +
                   "(modifiable ensuite). Rien n'est supprimé ni modifié : seules les catégories absentes sont ajoutées."
        });
        _template.ItemsSource = CategoryTemplates.All;
        _template.SelectionChanged += (_, _) => Fill();
        panel.Children.Add(_template);
        panel.Children.Add(_description);
        panel.Children.Add(new ScrollViewer { MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _items });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 12) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Style = (Style)Application.Current.FindResource("SecondaryButton") };
        var ok = new Button { Content = "Ajouter les catégories cochées", IsDefault = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
        _template.SelectedIndex = 0;
    }

    private void Fill()
    {
        _items.Children.Clear();
        _boxes.Clear();
        if (_template.SelectedItem is not CategoryTemplate t) return;
        _description.Text = t.Description;
        foreach (var group in new[] { (CategoryKind.Product, "Produits (recettes)"), (CategoryKind.Charge, "Charges (dépenses)") })
        {
            var items = t.Items.Where(i => i.Kind == group.Item1).ToList();
            if (items.Count == 0) continue;
            _items.Children.Add(new TextBlock { Text = group.Item2, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) });
            foreach (var it in items)
            {
                var present = _existing.Contains($"{it.Kind}|{it.Name.ToLowerInvariant()}");
                var box = new CheckBox
                {
                    IsChecked = !present, IsEnabled = !present, Margin = new Thickness(0, 2, 0, 2),
                    Content = $"{it.Name}   ·   compte {it.AccountNumber}" + (present ? "   (déjà présente)" : "")
                };
                _boxes.Add((box, it));
                _items.Children.Add(box);
            }
        }
    }
}
