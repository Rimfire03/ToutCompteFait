using System.Windows;
using ComptaAepby.Core;

namespace ComptaAepby.Dialogs;

public partial class ChangePasswordWindow : Window
{
    private readonly Compta _compta;

    public ChangePasswordWindow(Compta compta, bool forced)
    {
        InitializeComponent();
        _compta = compta;
        if (forced)
        {
            ForcedNote.Visibility = Visibility.Visible;
            CancelButton.Content = "Quitter";
        }
        Loaded += (_, _) => CurrentBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (NewBox.Password != ConfirmBox.Password)
        {
            ErrorText.Text = "Les deux nouveaux mots de passe ne sont pas identiques.";
            return;
        }
        try
        {
            _compta.ChangeOwnPassword(CurrentBox.Password, NewBox.Password);
            DialogResult = true;
        }
        catch (ComptaException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
