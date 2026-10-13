using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;
using TomLine.Licensing;
using TomLine.UI.Wpf.Controls;

namespace ToutCompteFait.Dialogs;

/// <summary>Saisie de la licence (premier lancement, ou aucune licence valide) : clé + « Activer », ou demande d'une
/// licence démo (la clé reçue est stockée sans jamais être affichée).</summary>
public sealed class LicenseWindow : Window
{
    private readonly LicenseService _service;
    private readonly TextBox _key = new() { Margin = new Thickness(0, 4, 0, 0), FontSize = 15, Padding = new Thickness(6) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button _activate;
    private readonly Button _demo;
    private readonly Button _retry;
    private DemoRequestProfile? _lastProfile;

    public LicenseWindow(LicenseService service, string? reason)
    {
        _service = service;
        Title = "Licence — " + Editions.AppName;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushBackground");

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Activation du logiciel", FontSize = 20, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock
        {
            Text = "Saisissez votre clé de licence pour activer ce poste, ou demandez une licence de démonstration (7 jours).",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14)
        });
        panel.Children.Add(new TextBlock { Text = "Clé de licence" });
        panel.Children.Add(_key);

        _message.Foreground = System.Windows.Media.Brushes.Firebrick;
        if (reason is not null) _message.Text = LicenseReasons.Describe(reason);
        panel.Children.Add(_message);

        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var quit = new Button { Content = "Quitter", IsCancel = true, Style = (Style)Application.Current.FindResource("SecondaryButton") };
        _retry = new Button
        {
            Content = "Réessayer", Style = (Style)Application.Current.FindResource("SecondaryButton"),
            Visibility = reason is "offline_grace_expired" or "no_prior_validation" ? Visibility.Visible : Visibility.Collapsed
        };
        _demo = new Button { Content = "Demander une licence démo", Style = (Style)Application.Current.FindResource("SecondaryButton") };
        _activate = new Button { Content = "Activer", IsDefault = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        _activate.Click += async (_, _) => await RunAsync(() => _service.ActivateAsync(_key.Text));
        _demo.Click += async (_, _) =>
        {
            // Formulaire du demandeur (civilité, nom, e-mail…) : envoyé au serveur et enregistré avec la démo.
            var form = new DemoRequestWindow(Editions.AppName, _lastProfile) { Owner = this };
            if (form.ShowDialog() != true || form.Profile is not { } profile) return;
            _lastProfile = profile;
            await RunAsync(() => _service.RequestDemoAsync(profile));
        };
        _retry.Click += async (_, _) => await RunAsync(() => _service.StartAsync());
        buttons.Children.Add(quit);
        buttons.Children.Add(_retry);
        buttons.Children.Add(_demo);
        buttons.Children.Add(_activate);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _key.Focus();
    }

    private async Task RunAsync(Func<Task<LicenseState>> action)
    {
        _activate.IsEnabled = _demo.IsEnabled = _retry.IsEnabled = false;
        _message.Foreground = System.Windows.Media.Brushes.DimGray;
        _message.Text = "Vérification auprès du serveur de licences…";
        try
        {
            var state = await action();
            if (state.IsUsable) { DialogResult = true; return; }
            _message.Foreground = System.Windows.Media.Brushes.Firebrick;
            _message.Text = LicenseReasons.Describe(state.Reason);
        }
        catch (Exception ex)
        {
            _message.Foreground = System.Windows.Media.Brushes.Firebrick;
            _message.Text = "Erreur : " + ex.Message;
        }
        finally
        {
            _activate.IsEnabled = _demo.IsEnabled = _retry.IsEnabled = true;
        }
    }
}
