using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Dialogs;

/// <summary>Déclaration URSSAF faite : saisie du montant réellement déclaré sur le site et d'une note (n° de déclaration,
/// montant payé…). Le montant calculé par le logiciel est proposé par défaut ; l'écart éventuel est conservé à l'historique.</summary>
public sealed class DeclarationWindow : Window
{
    private readonly TextBox _amount = new() { Margin = new Thickness(0, 4, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Right };
    private readonly TextBox _note = new() { Margin = new Thickness(0, 4, 0, 0), AcceptsReturn = false };
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>Montant déclaré en centimes ; null si laissé vide.</summary>
    public long? DeclaredCents { get; private set; }
    public string? Note => string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim();

    public DeclarationWindow(string periodLabel, long computedCents, long? declaredCents, string? note)
    {
        Title = "Déclaration faite";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushSurface");

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = periodLabel, Style = (Style)Application.Current.FindResource("TitleText") });
        panel.Children.Add(new TextBlock
        {
            Text = $"Chiffre d'affaires calculé par le logiciel : {Money.Format(computedCents)} €.", Margin = new Thickness(0, 4, 0, 12),
            Style = (Style)Application.Current.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock { Text = "Montant déclaré sur autoentrepreneur.urssaf.fr (€)" });
        _amount.Text = Money.Format(declaredCents ?? computedCents);
        panel.Children.Add(_amount);
        panel.Children.Add(new TextBlock { Text = "Note (facultatif : n° de déclaration, montant payé…)", Margin = new Thickness(0, 12, 0, 0) });
        _note.Text = note ?? "";
        panel.Children.Add(_note);
        panel.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Style = (Style)Application.Current.FindResource("SecondaryButton") };
        var ok = new Button { Content = "Enregistrer", IsDefault = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        ok.Click += (_, _) =>
        {
            if (_amount.Text.Trim().Length == 0) { DeclaredCents = null; DialogResult = true; return; }
            if (!Money.TryParse(_amount.Text, out var cents) || cents < 0) { _error.Text = "Montant invalide : saisissez un nombre positif, par exemple 1 250,00."; return; }
            DeclaredCents = cents;
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _amount.Focus(); _amount.SelectAll(); };
    }
}
