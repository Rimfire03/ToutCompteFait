using System.Windows;
using System.Windows.Controls;

namespace ToutCompteFait.Controls;

/// <summary>Message d'accueil d'une liste vide : icône, titre et une phrase qui dit quoi faire. Posé par-dessus la grille
/// (même cellule), sans capter la souris.</summary>
public sealed class EmptyState : StackPanel
{
    private readonly TextBlock _icon = new() { FontSize = 38, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _title = new()
    {
        FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0)
    };
    private readonly TextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontSize = 12.5
    };

    public EmptyState()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        MaxWidth = 460;
        IsHitTestVisible = false;
        Panel.SetZIndex(this, 5);
        Children.Add(_icon);
        Children.Add(_title);
        Children.Add(_text);
        _text.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
        Visibility = Visibility.Collapsed;
    }

    public string Icon { get => _icon.Text; set => _icon.Text = value; }
    public string Title { get => _title.Text; set => _title.Text = value; }
    public string Text { get => _text.Text; set => _text.Text = value; }
}