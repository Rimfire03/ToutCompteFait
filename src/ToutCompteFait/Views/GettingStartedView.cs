using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToutCompteFait.Core;

namespace ToutCompteFait.Views;

/// <summary>« Premiers pas » : checklist de démarrage calculée d'après l'état du dossier, avec une barre de progression,
/// un bouton « Y aller » par étape et la visite guidée. Lecture seule.</summary>
public sealed class GettingStartedView : UserControl, IPage
{
    private readonly FiscalYear? _year;
    private readonly Action<string> _open;
    private readonly StackPanel _list = new();
    private readonly TextBlock _summary = new() { Margin = new Thickness(0, 10, 0, 6), FontWeight = FontWeights.SemiBold };
    private readonly ProgressBar _bar = new() { Height = 10, Maximum = 1, Margin = new Thickness(0, 0, 0, 6) };

    public GettingStartedView(FiscalYear? year, Action<string> open)
    {
        _year = year;
        _open = open;
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = "Premiers pas", Style = ComparisonView.Sty("TitleText") });
        root.Children.Add(new TextBlock
        {
            Style = ComparisonView.Sty("MutedText"), TextWrapping = TextWrapping.Wrap,
            Text = "Les étapes pour bien démarrer. Elles se cochent d'elles-mêmes dès que vous les avez réalisées."
        });
        var tour = new Button { Content = "▶  Lancer la visite guidée", Style = ComparisonView.Sty("PrimaryButton"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        tour.Click += (_, _) => new Dialogs.TourWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        root.Children.Add(tour);
        root.Children.Add(_summary);
        root.Children.Add(_bar);
        root.Children.Add(_list);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        Reload();
    }

    public void Reload()
    {
        _list.Children.Clear();
        var steps = App.Compta.BuildGettingStarted(_year?.Id);
        var done = steps.Count(s => s.Done);
        _bar.Value = steps.Count == 0 ? 0 : (double)done / steps.Count;
        _summary.Text = done == steps.Count ? "✔  Bravo, toutes les étapes sont réalisées." : $"{done} étape(s) sur {steps.Count} réalisée(s)";
        foreach (var s in steps) _list.Children.Add(Card(s));
    }

    private UIElement Card(GettingStartedStep s)
    {
        var green = new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57));
        var mark = new TextBlock
        {
            Text = s.Done ? "✔" : "○", FontSize = 22, Foreground = s.Done ? green : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            VerticalAlignment = VerticalAlignment.Center, Width = 34
        };
        var text = new StackPanel();
        var title = new TextBlock { Text = s.Title, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap };
        if (s.Done) title.Foreground = green;
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = s.Detail, Style = ComparisonView.Sty("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var go = new Button
        {
            Content = s.Done ? "Revoir" : "Y aller", Style = ComparisonView.Sty(s.Done ? "SecondaryButton" : "PrimaryButton"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
        };
        var target = s.Target;
        go.Click += (_, _) => _open(target);
        var row = new DockPanel();
        DockPanel.SetDock(mark, Dock.Left);
        DockPanel.SetDock(go, Dock.Right);
        row.Children.Add(mark);
        row.Children.Add(go);
        row.Children.Add(text);
        return new Border { Style = ComparisonView.Sty("Card"), Margin = new Thickness(0, 10, 0, 0), Child = row };
    }
}
