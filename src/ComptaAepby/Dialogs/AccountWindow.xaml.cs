using System.Windows;
using ComptaAepby.Core;

namespace ComptaAepby.Dialogs;

public partial class AccountWindow : Window
{
    private sealed record KindItem(AccountKind Kind, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly Account? _existing;

    public AccountWindow(Account? existing)
    {
        InitializeComponent();
        _existing = existing;
        KindCombo.ItemsSource = new[] { AccountKind.Bank, AccountKind.Savings, AccountKind.Cash, AccountKind.Other }
            .Select(k => new KindItem(k, Labels.Of(k))).ToList();
        KindCombo.SelectedIndex = 0;
        if (existing is not null)
        {
            TitleText.Text = "Modifier le compte";
            NameBox.Text = existing.Name;
            ActiveBox.IsChecked = existing.Active;
            KindCombo.SelectedItem = KindCombo.Items.OfType<KindItem>().First(k => k.Kind == existing.Kind);
        }
        else TitleText.Text = "Nouveau compte";
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var kind = (KindCombo.SelectedItem as KindItem)?.Kind ?? AccountKind.Bank;
        try
        {
            if (_existing is null) App.Compta.AddAccount(NameBox.Text, kind);
            else App.Compta.UpdateAccount(_existing.Id, NameBox.Text, kind, ActiveBox.IsChecked == true);
            DialogResult = true;
        }
        catch (ComptaException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
