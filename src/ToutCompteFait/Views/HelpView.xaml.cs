using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Views;

public partial class HelpView : UserControl
{
    public HelpView()
    {
        InitializeComponent();
        HelpTitle.Text = "Aide — " + Editions.AppName;
        if (!Editions.Current.HasMemberRefunds)
        {
            MemberHelpTitle.Visibility = Visibility.Collapsed;
            MemberHelpText.Visibility = Visibility.Collapsed;
            IntroText.Text = IntroText.Text.Replace(", virement entre comptes ou remboursement à un membre)", " ou virement entre comptes)")
                .Replace("(dépense, recette, virement entre comptes ou remboursement à un membre)", "(dépense, recette ou virement entre comptes)");
        }
        Loaded += (_, _) =>
        {
            if (MainWindow.Current?.CurrentYear is null) NoYearText.Visibility = Visibility.Visible;
        };
    }
}
