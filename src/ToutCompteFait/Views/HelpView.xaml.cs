using System.Windows;
using System.Windows.Controls;

namespace ToutCompteFait.Views;

public partial class HelpView : UserControl
{
    public HelpView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (MainWindow.Current?.CurrentYear is null) NoYearText.Visibility = Visibility.Visible;
        };
    }
}
