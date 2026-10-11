using System.Windows;
using System.Windows.Controls;

namespace ComptaAepby.Dialogs;

/// <summary>Affiche un long texte (rapport d'import) dans une zone défilante, avec copie.</summary>
public sealed class ReportWindow : Window
{
    public ReportWindow(string title, string heading, string text)
    {
        Title = title;
        Width = 820;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushBackground");

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new TextBlock { Text = heading, TextWrapping = TextWrapping.Wrap, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true
        };
        Grid.SetRow(box, 1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "Copier", Style = (Style)Application.Current.FindResource("SecondaryButton") };
        copy.Click += (_, _) => { try { Clipboard.SetText(text); } catch { /* presse-papiers indisponible */ } };
        var close = new Button { Content = "Fermer", IsDefault = true, IsCancel = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(head);
        grid.Children.Add(box);
        grid.Children.Add(buttons);
        Content = grid;
    }
}
