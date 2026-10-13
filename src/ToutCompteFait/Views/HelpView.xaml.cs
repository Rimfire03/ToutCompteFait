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
            IntroText.Text = IntroText.Text.Replace("virement entre comptes, remboursement à un membre", "virement entre comptes");
        }
        if (Editions.Current.IsBusiness)
        {
            ReportingHelpTitle.Visibility = Visibility.Visible;
            ReportingHelpText.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) =>
        {
            if (MainWindow.Current?.CurrentYear is null)
            {
                NoYearText.Visibility = Visibility.Visible;
                NoYearButton.Visibility = Visibility.Visible;
            }
        };
        DevLogButton.Click += (_, _) =>
        {
            var all = ToutCompteFait.Dialogs.DevLogWindow.LoadEmbedded();
            var shown = all.Where(e => e.Items.Count > 0).Take(3).ToList();
            if (shown.Count == 0) { Ui.Info("Aucune nouveauté enregistrée pour cette version."); return; }
            new ToutCompteFait.Dialogs.DevLogWindow(shown) { Owner = Window.GetWindow(this) }.ShowDialog();
        };
        NoYearButton.Click += (_, _) => MainWindow.Current?.CreateYearInteractive();
    }
}
