using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToutCompteFait.Core;

namespace ToutCompteFait.Views;

/// <summary>Centre de notifications : toutes les alertes en cours (déclarations URSSAF, écritures à ventiler, exercice à
/// clôturer), consultables à tout moment. Chaque carte renvoie vers l'écran concerné. Lecture seule.</summary>
public sealed class NotificationsView : UserControl, IPage
{
    private readonly FiscalYear _year;
    private readonly Action<string> _open;
    private readonly StackPanel _list = new();

    public NotificationsView(FiscalYear year, Action<string> open)
    {
        _year = year;
        _open = open;
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = "Notifications", Style = ComparisonView.Sty("TitleText") });
        root.Children.Add(new TextBlock
        {
            Style = ComparisonView.Sty("MutedText"), TextWrapping = TextWrapping.Wrap,
            Text = "Les alertes en cours, à retrouver à tout moment : échéances de déclaration, écritures à ventiler, exercice à clôturer. " +
                   "Elles disparaissent d'elles-mêmes dès que la situation est réglée."
        });
        root.Children.Add(_list);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        Reload();
    }

    public void Reload()
    {
        _list.Children.Clear();
        var items = App.Compta.BuildNotifications(DateOnly.FromDateTime(DateTime.Today), _year.Id);
        if (items.Count == 0)
        {
            _list.Children.Add(new Border
            {
                Style = ComparisonView.Sty("Card"), Margin = new Thickness(0, 14, 0, 0),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "✔  Tout est à jour", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57)) },
                        new TextBlock { Text = "Aucune échéance ni action en attente.", Margin = new Thickness(0, 4, 0, 0), Style = ComparisonView.Sty("MutedText") }
                    }
                }
            });
            return;
        }
        foreach (var n in items) _list.Children.Add(Card(n));
    }

    private UIElement Card(AppNotification n)
    {
        var accent = n.Level switch
        {
            NotificationLevel.Alert => Color.FromRgb(0xB9, 0x1C, 0x1C),
            NotificationLevel.Warning => Color.FromRgb(0xD9, 0x77, 0x06),
            _ => Color.FromRgb(0x25, 0x63, 0xEB)
        };
        var open = new Button { Content = "Ouvrir", Style = ComparisonView.Sty("SecondaryButton"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var target = n.Target;
        open.Click += (_, _) => _open(target);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = n.Title, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = n.Detail, Style = ComparisonView.Sty("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var row = new DockPanel();
        DockPanel.SetDock(open, Dock.Right);
        row.Children.Add(open);
        row.Children.Add(text);
        var bar = new Border { Width = 5, Background = new SolidColorBrush(accent), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(bar, Dock.Left);
        var inner = new DockPanel();
        inner.Children.Add(bar);
        inner.Children.Add(row);
        return new Border { Style = ComparisonView.Sty("Card"), Margin = new Thickness(0, 12, 0, 0), Child = inner };
    }
}
