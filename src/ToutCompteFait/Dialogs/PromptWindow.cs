using System.Windows;
using System.Windows.Controls;

namespace ToutCompteFait.Dialogs;

/// <summary>Petite boîte de saisie d'un texte (nom, motif...).</summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _box = new() { Margin = new Thickness(0, 10, 0, 0) };
    public string Value => _box.Text.Trim();

    public PromptWindow(string title, string message, string initial = "", string okText = "OK")
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushBackground");

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        _box.Text = initial;
        panel.Children.Add(_box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Style = (Style)Application.Current.FindResource("SecondaryButton") };
        var ok = new Button { Content = okText, IsDefault = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window? owner, string title, string message, string initial = "", string okText = "OK")
    {
        var w = new PromptWindow(title, message, initial, okText) { Owner = owner };
        return w.ShowDialog() == true ? w.Value : null;
    }
}
