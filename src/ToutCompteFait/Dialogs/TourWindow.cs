using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Dialogs;

/// <summary>Visite guidée : quelques écrans d'explication (Précédent / Suivant) sur les rubriques du menu, pour savoir où
/// aller et pourquoi. Aucun effet sur les données.</summary>
public sealed class TourWindow : Window
{
    private readonly List<(string Title, string Text)> _slides = new();
    private int _index;
    private readonly TextBlock _title = new();
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 14.5, LineHeight = 22 };
    private readonly TextBlock _counter = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _prev = new() { Content = "Précédent" };
    private readonly Button _next = new() { Content = "Suivant" };

    public TourWindow()
    {
        var business = Editions.Current.IsBusiness;
        _slides.Add(("Bienvenue", "Ce logiciel tient votre trésorerie à partir de ce qui se passe sur votre compte bancaire : vous importez ou saisissez vos écritures, " +
                                  "vous les classez en produits (ce qui rentre) et en charges (ce qui sort), et le bilan, les statistiques et les déclarations se calculent tout seuls."));
        _slides.Add(("Opérations bancaires", "C'est l'écran de travail. « Importer un relevé bancaire » lit le fichier de votre banque ; les nouvelles lignes apparaissent en jaune : " +
                                              "choisissez pour chacune son produit ou sa charge. Un clic droit sur une ligne permet de la modifier ou de retirer sa ventilation."));
        _slides.Add(("Bilan & comptes annuels", "Le résultat de l'exercice, la situation des comptes et des graphiques. Vous pouvez imprimer ou exporter, puis valider l'exercice : " +
                                                 "il est alors verrouillé et protégé par une empreinte de contrôle."));
        _slides.Add(("Statistiques", "Un tableau de bord par type d'élément (produits, charges, opérations), avec un onglet par élément et un commutateur Mensuel / Annuel pour comparer les périodes."));
        _slides.Add(business
            ? ("Reporting URSSAF", "Le calendrier des échéances, les montants à saisir sur le site de l'URSSAF, l'historique de vos déclarations et le suivi des plafonds. " +
                                    "Une fois la déclaration envoyée, marquez-la comme faite : l'alerte disparaît.")
            : ("Remboursements aux membres", "Quand un membre avance de l'argent pour l'association, saisissez un remboursement : le récapitulatif par membre se met à jour ici."));
        _slides.Add((business ? "Gestion de l'entreprise" : "Paramétrage du dossier",
            "Vos produits et charges, vos comptes bancaires, l'exercice et les réglages. " + (business ? "Les « Modèles de catégories » ajoutent en un clic les catégories courantes de votre activité." : "")));
        _slides.Add(("Notifications et aide", "La cloche en haut à gauche rassemble les alertes (échéances, écritures à ventiler). « Premiers pas » liste les étapes pour bien démarrer, et l'Aide détaille chaque écran."));

        Title = "Visite guidée";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushSurface");
        _title.Style = (Style)Application.Current.FindResource("TitleText");
        _prev.Style = (Style)Application.Current.FindResource("SecondaryButton");
        _next.Style = (Style)Application.Current.FindResource("PrimaryButton");
        _next.Margin = new Thickness(0);
        _prev.Click += (_, _) => Go(_index - 1);
        _next.Click += (_, _) => { if (_index >= _slides.Count - 1) Close(); else Go(_index + 1); };

        var buttons = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_prev);
        right.Children.Add(_next);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(_counter);

        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 20) };
        panel.Children.Add(_title);
        panel.Children.Add(_text);
        panel.Children.Add(buttons);
        Content = panel;
        Go(0);
    }

    private void Go(int index)
    {
        _index = Math.Clamp(index, 0, _slides.Count - 1);
        _title.Text = _slides[_index].Title;
        _text.Text = _slides[_index].Text;
        _counter.Text = $"{_index + 1} / {_slides.Count}";
        _prev.IsEnabled = _index > 0;
        _next.Content = _index >= _slides.Count - 1 ? "Terminer" : "Suivant";
    }
}
