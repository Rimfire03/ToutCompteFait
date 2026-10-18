using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ToutCompteFait.Dialogs;

/// <summary>Retour visuel discret (« ✔ Enregistré ») affiché quelques secondes en bas de la fenêtre, sans prendre le focus
/// ni bloquer la saisie : confirme qu'une modification a bien été prise en compte.</summary>
public sealed class ToastWindow : Window
{
    private static ToastWindow? _current;

    private ToastWindow(Window owner, string text)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57)), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 11, 20, 11), Margin = new Thickness(6),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = .28 },
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 14 }
        };
        Loaded += (_, _) =>
        {
            Left = owner.Left + (owner.ActualWidth - ActualWidth) / 2;
            Top = owner.Top + owner.ActualHeight - ActualHeight - 56;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1900) };
            timer.Tick += (_, _) => { timer.Stop(); Close(); };
            timer.Start();
        };
        Closed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };
    }

    /// <summary>Affiche le message (le précédent est remplacé). Sans effet si la fenêtre n'est pas visible.</summary>
    public static void Show(string text)
    {
        try
        {
            var owner = Application.Current?.MainWindow;
            if (owner is not { IsVisible: true } || owner.WindowState == WindowState.Minimized) return;
            _current?.Close();
            _current = new ToastWindow(owner, ToutCompteFait.Core.Wording.Adapt(text));
            _current.Show();
        }
        catch
        {
            // Confort visuel : jamais d'erreur pour un message de confirmation.
        }
    }
}
