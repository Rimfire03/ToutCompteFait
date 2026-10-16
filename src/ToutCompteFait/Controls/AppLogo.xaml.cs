using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToutCompteFait.Core;

namespace ToutCompteFait.Controls;

/// <summary>Logo de l'application (vectoriel), affiché au-dessus du nom dans la barre latérale et la fenêtre de connexion.
/// Une pastille en haut à gauche indique la version du logiciel : « ASSO » (association) ou « EI » (Autoentreprise).</summary>
public partial class AppLogo : UserControl
{
    public AppLogo()
    {
        InitializeComponent();
        var business = Editions.Current.IsBusiness;
        var badge = new Border
        {
            // Pastille franche, débordant légèrement du coin : lisible même à 60 px de logo (≈ 14 px de haut de texte).
            Width = business ? 62 : 96, Height = 46, CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(business ? Color.FromRgb(0xC2, 0x41, 0x0C) : Color.FromRgb(0x1E, 0x3A, 0x8A)),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(3),
            Child = new TextBlock
            {
                Text = business ? "EI" : "ASSO", Foreground = Brushes.White, FontSize = 30, FontWeight = FontWeights.Bold,
                // Police moderne (DIN contemporaine, livrée avec Windows 10/11) ; Segoe UI Black en repli.
                FontFamily = new FontFamily("Bahnschrift, Segoe UI Black, Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        Canvas.SetLeft(badge, -10);
        Canvas.SetTop(badge, -10);
        Art.Children.Add(badge);
    }
}
