using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToutCompteFait.Core;

namespace ToutCompteFait.Controls;

/// <summary>Logo de l'application (vectoriel), affiché au-dessus du nom dans la barre latérale et la fenêtre de connexion.
/// Une pastille en bas, centrée, indique la version du logiciel : « ASSO » (association) ou « EI » (Autoentreprise).
/// Elle ne recouvre que la zone vide sous l'anneau : le motif du logo reste intact.</summary>
public partial class AppLogo : UserControl
{
    public AppLogo()
    {
        InitializeComponent();
        var business = Editions.Current.IsBusiness;
        var badge = new Border
        {
            Width = business ? 52 : 80, Height = 34, CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(business ? Color.FromRgb(0xC2, 0x41, 0x0C) : Color.FromRgb(0x1E, 0x3A, 0x8A)),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(2.5),
            Child = new TextBlock
            {
                Text = business ? "EI" : "ASSO", Foreground = Brushes.White, FontSize = 23, FontWeight = FontWeights.Bold,
                // Police moderne (DIN contemporaine, livrée avec Windows 10/11) ; Segoe UI Black en repli.
                FontFamily = new FontFamily("Bahnschrift, Segoe UI Black, Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        // Chevauche le bas du carré (zone vide sous l'anneau) et dépasse un peu en dessous.
        Canvas.SetLeft(badge, (128 - badge.Width) / 2);
        Canvas.SetTop(badge, 110);
        Art.Children.Add(badge);
    }
}
