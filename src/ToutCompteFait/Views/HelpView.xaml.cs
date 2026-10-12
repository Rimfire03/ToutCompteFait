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
        Loaded += (_, _) =>
        {
            if (MainWindow.Current?.CurrentYear is null) NoYearText.Visibility = Visibility.Visible;
        };
    }
}
