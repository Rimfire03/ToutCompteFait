using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToutCompteFait.Core.Business;

namespace ToutCompteFait.Dialogs;

/// <summary>Alerte à l'ouverture : déclarations URSSAF à réaliser (périodes terminées non marquées « déclarées »).
/// <see cref="Window.DialogResult"/> vrai = ouvrir le Reporting URSSAF.</summary>
public sealed class UrssafAlertWindow : Window
{
    private static Style Sty(string key) => (Style)Application.Current.FindResource(key);

    public UrssafAlertWindow(IReadOnlyList<UrssafPeriod> pending, DateOnly today)
    {
        Title = "Déclaration URSSAF";
        Width = 640;
        MaxHeight = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.FindResource("BrushSurface");

        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 8) };
        panel.Children.Add(new TextBlock { Text = "Déclaration URSSAF à réaliser", Style = Sty("TitleText") });
        panel.Children.Add(new TextBlock
        {
            Style = Sty("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = pending.Count == 1 ? "Une période terminée n'est pas encore marquée « déclarée »."
                                      : $"{pending.Count} périodes terminées ne sont pas encore marquées « déclarées »."
        });

        var late = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
        var list = new StackPanel();
        foreach (var p in pending)
        {
            var overdue = today > p.DueDate;
            var days = Math.Abs(p.DueDate.DayNumber - today.DayNumber);
            var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
            var due = new TextBlock
            {
                Text = (overdue ? $"En retard de {days} j · " : $"J-{days} · ") + $"avant le {p.DueDate:dd/MM/yyyy}",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = overdue ? FontWeights.SemiBold : FontWeights.Normal
            };
            if (overdue) due.Foreground = late;
            else due.Style = Sty("MutedText");
            DockPanel.SetDock(due, Dock.Right);
            row.Children.Add(due);
            row.Children.Add(new TextBlock { Text = p.Label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            list.Children.Add(row);
        }
        panel.Children.Add(new Border { Style = Sty("Card"), Child = list, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = "Une fois la déclaration faite sur autoentrepreneur.urssaf.fr, marquez-la « déclarée » dans le Reporting URSSAF : " +
                   "cette alerte ne s'affichera plus pour la période."
        });

        var site = new Button { Content = "Ouvrir le site de l'URSSAF", Style = Sty("SecondaryButton"), ToolTip = ToutCompteFait.Services.UrssafSite.Address };
        site.Click += (_, _) => ToutCompteFait.Services.UrssafSite.Open();
        var open = new Button { Content = "Ouvrir le Reporting URSSAF", IsDefault = true, Style = Sty("PrimaryButton") };
        open.Click += (_, _) => DialogResult = true;
        var later = new Button { Content = "Plus tard", IsCancel = true, Style = Sty("SecondaryButton") };
        later.Click += (_, _) => DialogResult = false;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(22, 4, 22, 16)
        };
        buttons.Children.Add(site);
        buttons.Children.Add(later);
        buttons.Children.Add(open);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel });
        Content = root;
    }
}
