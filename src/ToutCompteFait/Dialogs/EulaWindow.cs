using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Licensing;

namespace ToutCompteFait.Dialogs;

/// <summary>Affiche le CLUF. En mode acceptation (premier lancement, ou texte modifié), l'utilisateur doit accepter pour
/// continuer ; sinon la fenêtre est une simple consultation.</summary>
public sealed class EulaWindow : Window
{
    public EulaWindow(bool requireAcceptance, bool changed = false)
    {
        Title = "Contrat de licence — " + Editions.AppName;
        Width = 720;
        Height = 640;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushBackground");

        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = "Contrat de licence utilisateur final", FontSize = 20, FontWeight = FontWeights.Bold });
        header.Children.Add(new TextBlock
        {
            Text = (changed
                       ? "Le contrat a été modifié depuis votre dernière acceptation : veuillez le relire et l'accepter pour continuer. "
                       : requireAcceptance ? "Veuillez lire et accepter ce contrat pour utiliser le logiciel. " : "") + $"Version {Eula.Revision}",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        if (requireAcceptance)
        {
            var refuse = new Button { Content = "Je refuse (quitter)", IsCancel = true, Style = (Style)Application.Current.FindResource("SecondaryButton") };
            var accept = new Button { Content = "J'accepte", IsDefault = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
            accept.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(refuse);
            buttons.Children.Add(accept);
        }
        else
        {
            var close = new Button { Content = "Fermer", IsCancel = true, IsDefault = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
            buttons.Children.Add(close);
        }
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        root.Children.Add(new TextBox
        {
            Text = Eula.Text.Replace("\r\n", "\n"), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12), FontSize = 13
        });
        Content = root;
    }
}
