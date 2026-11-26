using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ToutCompteFait.Controls;

/// <summary>Champ de mot de passe avec un petit œil : un clic affiche le mot de passe en clair, un second clic le masque. Même
/// interface qu'un <see cref="PasswordBox"/> pour ce que le code utilise (<see cref="Password"/>, <see cref="Clear"/>, focus).</summary>
public sealed class PasswordField : UserControl
{
    private readonly PasswordBox _hidden = new() { Padding = new Thickness(6, 5, 34, 5), BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)) };
    private readonly TextBox _shown = new() { Padding = new Thickness(6, 5, 34, 5), Visibility = Visibility.Collapsed };
    private readonly ToggleButton _eye = new();

    public PasswordField()
    {
        Focusable = true;
        IsTabStop = false;
        var eyeText = new TextBlock { Text = "👁", FontSize = 15, Opacity = .55, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var template = new ControlTemplate(typeof(ToggleButton));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = presenter;
        _eye.Template = template;
        _eye.Content = eyeText;
        _eye.Width = 30;
        _eye.HorizontalAlignment = HorizontalAlignment.Right;
        _eye.Margin = new Thickness(0, 2, 2, 2);
        _eye.Cursor = Cursors.Hand;
        _eye.Focusable = false;
        _eye.ToolTip = "Afficher le mot de passe";
        _eye.Checked += (_, _) => Show(true, eyeText);
        _eye.Unchecked += (_, _) => Show(false, eyeText);
        var grid = new Grid();
        grid.Children.Add(_hidden);
        grid.Children.Add(_shown);
        grid.Children.Add(_eye);
        Content = grid;
    }

    private bool Revealed => _shown.Visibility == Visibility.Visible;

    private void Show(bool on, TextBlock eyeText)
    {
        if (on) _shown.Text = _hidden.Password; else _hidden.Password = _shown.Text;
        _shown.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _hidden.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        eyeText.Opacity = on ? 1 : .55;
        _eye.ToolTip = on ? "Masquer le mot de passe" : "Afficher le mot de passe";
        (on ? (UIElement)_shown : _hidden).Focus();
    }

    public string Password
    {
        get => Revealed ? _shown.Text : _hidden.Password;
        set { _hidden.Password = value; _shown.Text = value; }
    }

    public void Clear() => Password = "";

    /// <summary>Le focus donné au champ va à la zone de saisie visible.</summary>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (ReferenceEquals(e.NewFocus, this)) (Revealed ? (UIElement)_shown : _hidden).Focus();
    }
}