using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core.Business;

namespace ToutCompteFait.Dialogs;

/// <summary>Profil court de première ouverture (Autoentreprise) : trois questions pour adapter le logiciel à l'activité
/// (catégories prêtes à l'emploi, mode de déclaration URSSAF, gestion du matériel). Tout reste modifiable ensuite.</summary>
public sealed class ProfileWindow : Window
{
    private readonly List<(RadioButton Radio, string TemplateId)> _activities = new();
    private readonly RadioButton _quarterly = new() { Content = "Par trimestre (le plus courant)", IsChecked = true, GroupName = "mode" };
    private readonly RadioButton _monthly = new() { Content = "Par mois", GroupName = "mode" };
    private readonly RadioButton _assetsYes = new() { Content = "Oui, suivre mon matériel et l'amortir", GroupName = "assets" };
    private readonly RadioButton _assetsNo = new() { Content = "Non, pas pour le moment", IsChecked = true, GroupName = "assets" };

    public string? TemplateId => _activities.FirstOrDefault(a => a.Radio.IsChecked == true).TemplateId;
    public DeclarationMode Mode => _monthly.IsChecked == true ? DeclarationMode.Monthly : DeclarationMode.Quarterly;
    public bool UseAssets => _assetsYes.IsChecked == true;

    /// <summary>Vrai si l'utilisateur écarte la question définitivement (« Ne plus demander »).</summary>
    public bool NeverAskAgain { get; private set; }

    public ProfileWindow()
    {
        Title = "Bienvenue — votre activité";
        Width = 580;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushSurface");

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };
        panel.Children.Add(new TextBlock { Text = "Adaptons le logiciel à votre activité", Style = (Style)Application.Current.FindResource("TitleText") });
        panel.Children.Add(new TextBlock
        {
            Style = (Style)Application.Current.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 14),
            Text = "Trois questions, une minute. Nous préparons vos produits et charges habituels ; vous pourrez tout modifier plus tard."
        });

        Section(panel, "1. Quelle est votre activité ?");
        var first = true;
        foreach (var t in CategoryTemplates.All)
        {
            var radio = new RadioButton { GroupName = "activity", IsChecked = first, Margin = new Thickness(0, 4, 0, 4) };
            radio.Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = t.Description, TextWrapping = TextWrapping.Wrap, Opacity = .75, FontSize = 12.5 }
                }
            };
            _activities.Add((radio, t.Id));
            panel.Children.Add(radio);
            first = false;
        }

        Section(panel, "2. Comment déclarez-vous votre chiffre d'affaires à l'URSSAF ?");
        panel.Children.Add(_quarterly);
        panel.Children.Add(_monthly);

        Section(panel, "3. Achetez-vous du matériel durable (ordinateur, machine, outillage) ?");
        panel.Children.Add(_assetsYes);
        panel.Children.Add(_assetsNo);

        var skip = new Button { Content = "Ne plus demander", Style = (Style)Application.Current.FindResource("SecondaryButton") };
        skip.Click += (_, _) => { NeverAskAgain = true; DialogResult = false; };
        var later = new Button { Content = "Plus tard", IsCancel = true, Style = (Style)Application.Current.FindResource("SecondaryButton") };
        var ok = new Button { Content = "Préparer mon dossier", IsDefault = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        ok.Click += (_, _) => DialogResult = true;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 16) };
        bar.Children.Add(skip);
        bar.Children.Add(later);
        bar.Children.Add(ok);
        panel.Children.Add(bar);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
    }

    private static void Section(Panel panel, string text) =>
        panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14.5, Margin = new Thickness(0, 14, 0, 4) });
}
