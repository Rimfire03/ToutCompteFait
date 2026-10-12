using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Business;

namespace ToutCompteFait.Dialogs;

/// <summary>Nouvelle catégorie de recettes d'une entreprise : type de déclaration URSSAF (BIC vente, BIC prestation,
/// BNC…) et nom personnalisé éventuel, qui s'écrit après le type (« BIC prestation – Dépannage »).</summary>
public sealed class ActivityCategoryWindow : Window
{
    private sealed record Item(UrssafKind Kind, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly ComboBox _kind = new();
    private readonly TextBox _name = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _preview = new() { Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    public UrssafKind Kind => ((Item)_kind.SelectedItem).Kind;
    public string? CustomName => string.IsNullOrWhiteSpace(_name.Text) ? null : _name.Text.Trim();

    /// <param name="suggested">Types suggérés par les codes APE de l'entreprise (proposés en premier).</param>
    public ActivityCategoryWindow(IReadOnlyCollection<UrssafKind> suggested)
    {
        Title = "Nouvelle catégorie de recettes";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushBackground");

        var items = UrssafKinds.All.OrderBy(k => suggested.Contains(k) ? 0 : 1)
            .Select(k => new Item(k, k.Label() + (suggested.Contains(k) ? "  (d'après votre code APE)" : ""))).ToList();
        _kind.ItemsSource = items;
        _kind.SelectedIndex = 0;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Le type détermine la ligne du site de l'URSSAF où ces recettes seront déclarées. Le nom personnalisé est facultatif : " +
                   "il permet de distinguer plusieurs activités ou clients du même type."
        });
        panel.Children.Add(new TextBlock { Text = "Type de déclaration", Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(_kind);
        panel.Children.Add(new TextBlock { Text = "Nom personnalisé (facultatif)", Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(_name);
        panel.Children.Add(_preview);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Style = (Style)Application.Current.FindResource("SecondaryButton") };
        var ok = new Button { Content = "Créer", IsDefault = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;

        void Refresh() => _preview.Text = "Catégorie créée : " + UrssafKinds.CategoryName(Kind, CustomName);
        _kind.SelectionChanged += (_, _) => Refresh();
        _name.TextChanged += (_, _) => Refresh();
        Refresh();
        Loaded += (_, _) => _name.Focus();
    }

    public static (UrssafKind Kind, string? Name)? Ask(Window? owner, IReadOnlyCollection<UrssafKind> suggested)
    {
        var w = new ActivityCategoryWindow(suggested) { Owner = owner };
        return w.ShowDialog() == true ? (w.Kind, w.CustomName) : null;
    }
}