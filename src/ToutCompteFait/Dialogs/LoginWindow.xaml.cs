using System.Windows;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Security;

namespace ToutCompteFait.Dialogs;

/// <summary>Connexion ; au tout premier lancement (aucun compte), création de l'administrateur.</summary>
public partial class LoginWindow : Window
{
    private readonly AuthService _auth;
    private readonly bool _setup;

    public AppUser? User { get; private set; }

    public LoginWindow(AuthService auth)
    {
        InitializeComponent();
        Title = Editions.AppName + " — Connexion";
        AppTitleText.Text = Editions.AppName;
        _auth = auth;
        _setup = !auth.HasUsers();
        if (_setup)
        {
            Title = Editions.AppName + " — Création de l'administrateur";
            SubTitle.Text = "Création du compte administrateur";
            SetupNote.Visibility = Visibility.Visible;
            DisplayPanel.Visibility = Visibility.Visible;
            ConfirmPanel.Visibility = Visibility.Visible;
            OkButton.Content = "Créer le compte";
        }
        else
        {
            SubTitle.Text = "Livre de trésorerie — identifiez-vous";
            UserBox.Text = App.Settings.LastUserName ?? "";
            var remembered = App.Settings.GetRememberedPassword();
            if (remembered.Length > 0 && UserBox.Text.Length > 0)
            {
                PasswordBox.Password = remembered;
                RememberBox.IsChecked = true;
            }
            // Autre identifiant saisi : le mot de passe mémorisé ne le concerne pas.
            UserBox.TextChanged += (_, _) =>
            {
                if (!string.Equals(UserBox.Text.Trim(), App.Settings.LastUserName, StringComparison.OrdinalIgnoreCase))
                {
                    PasswordBox.Clear();
                    RememberBox.IsChecked = false;
                }
            };
        }
        if (_setup) RememberBox.Visibility = Visibility.Collapsed;
        Loaded += (_, _) => (string.IsNullOrEmpty(UserBox.Text) ? UserBox : (System.Windows.Controls.Control)PasswordBox).Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        try
        {
            if (_setup)
            {
                if (PasswordBox.Password != ConfirmBox.Password)
                {
                    ErrorText.Text = "Les deux mots de passe ne sont pas identiques.";
                    return;
                }
                User = _auth.CreateFirstAdmin(UserBox.Text, DisplayBox.Text, PasswordBox.Password);
            }
            else
            {
                User = _auth.TryLogin(UserBox.Text, PasswordBox.Password, out var error);
                if (User is null)
                {
                    ErrorText.Text = error ?? "Connexion refusée.";
                    PasswordBox.Clear();
                    PasswordBox.Focus();
                    return;
                }
                // Enregistré par App après la connexion (Settings.Save) ; chiffré, ou oublié si la case est décochée.
                App.Settings.SetRememberedPassword(RememberBox.IsChecked == true ? PasswordBox.Password : null);
            }
            DialogResult = true;
        }
        catch (ComptaException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
