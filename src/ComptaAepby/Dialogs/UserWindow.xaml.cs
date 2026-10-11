using System.Windows;
using System.Windows.Controls;
using ComptaAepby.Core;
using ComptaAepby.Core.Security;

namespace ComptaAepby.Dialogs;

/// <summary>Création, modification ou réinitialisation du mot de passe d'un compte (réservé à l'administrateur).</summary>
public partial class UserWindow : Window
{
    public enum UserMode { Create, Edit, ResetPassword }

    private sealed record RoleItem(Role Role)
    {
        public override string ToString() => RoleInfo.DisplayName(Role);
    }

    private readonly Compta _c = App.Compta;
    private readonly UserMode _mode;
    private readonly AppUser? _user;

    public UserWindow(UserMode mode, AppUser? user = null)
    {
        InitializeComponent();
        _mode = mode;
        _user = user;
        RoleCombo.ItemsSource = new[] { Role.Reader, Role.Treasurer, Role.Admin }.Select(r => new RoleItem(r)).ToList();
        RoleCombo.SelectedIndex = 0;

        switch (mode)
        {
            case UserMode.Create:
                TitleText.Text = "Nouvel utilisateur";
                Intro.Text = "Le compte pourra se connecter avec ce mot de passe provisoire, qu'il devra changer.";
                ActiveBox.Visibility = Visibility.Collapsed;
                break;

            case UserMode.Edit:
                TitleText.Text = "Modifier l'utilisateur";
                UserNameBox.Text = user!.UserName;
                UserNameBox.IsEnabled = false;
                DisplayBox.Text = user.DisplayName;
                RoleCombo.SelectedItem = RoleCombo.Items.OfType<RoleItem>().First(r => r.Role == user.Role);
                ActiveBox.IsChecked = user.Active;
                PasswordPanel.Visibility = Visibility.Collapsed;
                break;

            default:
                TitleText.Text = "Réinitialiser le mot de passe";
                Intro.Text = $"Nouveau mot de passe provisoire pour « {user!.UserName} » : il devra le changer à sa prochaine connexion.";
                IdentityPanel.Visibility = Visibility.Collapsed;
                RolePanel.Visibility = Visibility.Collapsed;
                ActiveBox.Visibility = Visibility.Collapsed;
                PasswordLabel.Text = "Nouveau mot de passe provisoire";
                break;
        }
        Loaded += (_, _) => (mode == UserMode.ResetPassword ? (Control)PasswordBox : UserNameBox.IsEnabled ? UserNameBox : DisplayBox).Focus();
    }

    private void Role_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RoleCombo.SelectedItem is RoleItem r) RoleHelp.Text = RoleInfo.Describe(r.Role);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var role = (RoleCombo.SelectedItem as RoleItem)?.Role ?? Role.Reader;
        try
        {
            switch (_mode)
            {
                case UserMode.Create:
                    _c.CreateUser(UserNameBox.Text, DisplayBox.Text, role, PasswordBox.Password);
                    break;
                case UserMode.Edit:
                    _c.UpdateUser(_user!.Id, DisplayBox.Text, role, ActiveBox.IsChecked == true);
                    break;
                default:
                    _c.ResetPassword(_user!.Id, PasswordBox.Password);
                    break;
            }
            DialogResult = true;
        }
        catch (ComptaException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
