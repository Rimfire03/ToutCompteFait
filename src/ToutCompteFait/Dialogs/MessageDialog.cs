using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ToutCompteFait.Dialogs;

/// <summary>Boîte de message aux couleurs de l'application (remplace la boîte système) : information, avertissement,
/// erreur ou question, avec les boutons OK / Oui / Non / Annuler. Même contrat que <see cref="MessageBox"/>.</summary>
public sealed class MessageDialog : Window
{
    private MessageBoxResult _result;

    private static Style Sty(string key) => (Style)Application.Current.FindResource(key);

    internal MessageDialog(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        Title = caption;
        Width = 480;
        MaxHeight = 640;
        MinHeight = 150;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("BrushSurface");

        var (glyph, color) = icon switch
        {
            MessageBoxImage.Error => ("✕", Color.FromRgb(0xB9, 0x1C, 0x1C)),
            MessageBoxImage.Warning => ("!", Color.FromRgb(0xD9, 0x77, 0x06)),
            MessageBoxImage.Question => ("?", Color.FromRgb(0x1D, 0x4E, 0xD8)),
            _ => ("i", Color.FromRgb(0x1D, 0x4E, 0xD8))
        };
        var badge = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 16, 0),
            Child = new TextBlock
            {
                Text = glyph, Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };

        var body = new DockPanel { Margin = new Thickness(22, 20, 22, 8) };
        DockPanel.SetDock(badge, Dock.Left);
        body.Children.Add(badge);
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });

        // Boutons dans l'ordre d'affichage ; le dernier de la liste est le plus à droite.
        var list = buttons switch
        {
            MessageBoxButton.OK => new[] { MessageBoxResult.OK },
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
            _ => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel }
        };
        var cancelResult = buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel
        };
        var def = defaultResult != MessageBoxResult.None && list.Contains(defaultResult) ? defaultResult : list[0];
        _result = cancelResult;

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(22, 8, 22, 16) };
        foreach (var r in list)
        {
            var result = r;
            var b = new Button
            {
                Content = r switch
                {
                    MessageBoxResult.Yes => "Oui", MessageBoxResult.No => "Non", MessageBoxResult.Cancel => "Annuler", _ => "OK"
                },
                MinWidth = 90, IsDefault = r == def, IsCancel = r == cancelResult && list.Length > 1,
                Style = Sty(r == def ? "PrimaryButton" : "SecondaryButton")
            };
            if (r == list[^1]) b.Margin = new Thickness(0);
            b.Click += (_, _) => { _result = result; Close(); };
            bar.Children.Add(b);
        }
        if (list.Length == 1) ((Button)bar.Children[0]).IsCancel = true;

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        Content = root;
        Loaded += (_, _) => bar.Children.OfType<Button>().FirstOrDefault(x => x.IsDefault)?.Focus();
    }

    /// <summary>Affiche la boîte (modale) et renvoie le bouton choisi ; fermer la fenêtre vaut Non / Annuler.</summary>
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dlg = new MessageDialog(text, caption, buttons, icon, defaultResult);
        if (owner is { IsLoaded: true, IsVisible: true } && !ReferenceEquals(owner, dlg)) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
        return dlg._result;
    }
}
