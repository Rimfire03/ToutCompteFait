using System.Windows;
using ComptaAepby.Core;
using ComptaAepby.Core.Security;

namespace ComptaAepby.Dialogs;

/// <summary>Connexion ; au tout premier lancement (aucun compte), création de l'administrateur.</summary>
public partial class LoginWindow : Window
{
    private readonly AuthService _auth;
    private readonly bool _setup;

    public AppUser? User { get; private set; }

    public LoginWindow(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        _setup = !auth.HasUsers();
        if (_setup)
        {
            Title = "Tout Compte Fait — Création de l'administrateur";
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
        }
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
            }
            DialogResult = true;
        }
        catch (ComptaException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
