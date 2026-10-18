using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Views;

/// <summary>Onglet « Affichage » du paramétrage de l'application : ordre des entrées du menu de gauche et page ouverte au
/// lancement. Réglages propres à ce poste (fichier de réglages), sans effet sur les données.</summary>
public partial class SettingsView
{
    private sealed class NavEntry
    {
        public string Tag { get; init; } = "";
        public string Label { get; init; } = "";
        public override string ToString() => Label;
    }

    private readonly ObservableCollection<NavEntry> _navEntries = new();
    private ListBox? _navList;
    private ComboBox? _startBox;

    private void InitDisplay()
    {
        if (_scope != SettingsScope.Application) return;
        Tabs.Items.Add(new TabItem { Header = "Affichage", Content = BuildDisplayPanel() });
    }

    private UIElement BuildDisplayPanel()
    {
        var items = MainWindow.Current?.NavigationItems().Where(i => i.Visible).ToList() ?? new();
        foreach (var i in items) _navEntries.Add(new NavEntry { Tag = i.Tag, Label = i.Label });

        var panel = new StackPanel { Margin = new Thickness(0), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = "Réorganisez le menu de gauche avec les flèches et choisissez la page qui s'ouvre au lancement. " +
                   "Ces choix sont propres à ce poste ; vos données ne sont pas modifiées."
        });
        panel.Children.Add(new TextBlock { Text = "Ordre du menu", Style = (Style)FindResource("SectionText"), Margin = new Thickness(0, 0, 0, 6) });

        _navList = new ListBox { ItemsSource = _navEntries, Height = 230, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var up = new Button { Content = "▲  Monter", Style = (Style)FindResource("SecondaryButton") };
        var down = new Button { Content = "▼  Descendre", Style = (Style)FindResource("SecondaryButton") };
        up.Click += (_, _) => MoveNav(-1);
        down.Click += (_, _) => MoveNav(1);
        var buttons = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(up);
        buttons.Children.Add(down);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_navList);
        row.Children.Add(buttons);
        panel.Children.Add(row);

        panel.Children.Add(new TextBlock { Text = "Page d'accueil", Style = (Style)FindResource("SectionText"), Margin = new Thickness(0, 18, 0, 6) });
        _startBox = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = items.Select(i => new NavEntry { Tag = i.Tag, Label = i.Label }).ToList() };
        var current = App.Settings.StartPage ?? "livre";
        _startBox.SelectedItem = ((IEnumerable<NavEntry>)_startBox.ItemsSource).FirstOrDefault(e => e.Tag == current)
                                 ?? ((IEnumerable<NavEntry>)_startBox.ItemsSource).FirstOrDefault();
        panel.Children.Add(_startBox);

        var save = new Button { Content = "Enregistrer l'affichage", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 18, 8, 0) };
        save.Click += (_, _) => SaveDisplay(reset: false);
        var reset = new Button { Content = "Rétablir l'ordre d'origine", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 18, 0, 0) };
        reset.Click += (_, _) => SaveDisplay(reset: true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(save);
        actions.Children.Add(reset);
        panel.Children.Add(actions);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
    }

    private void MoveNav(int delta)
    {
        if (_navList?.SelectedIndex is not { } i || i < 0) return;
        var j = i + delta;
        if (j < 0 || j >= _navEntries.Count) return;
        _navEntries.Move(i, j);
        _navList.SelectedIndex = j;
    }

    private void SaveDisplay(bool reset)
    {
        if (reset)
        {
            App.Settings.NavOrder = new List<string>();
            App.Settings.StartPage = null;
        }
        else
        {
            App.Settings.NavOrder = _navEntries.Select(e => e.Tag).ToList();
            App.Settings.StartPage = (_startBox?.SelectedItem as NavEntry)?.Tag is { } t && t != "livre" ? t : null;
        }
        App.Settings.Save();
        MainWindow.Current?.ApplyNavigationPreferences();
        if (reset)
        {
            _navEntries.Clear();
            foreach (var i in MainWindow.Current?.NavigationItems().Where(i => i.Visible) ?? Enumerable.Empty<(string Tag, string Label, bool Visible)>())
                _navEntries.Add(new NavEntry { Tag = i.Tag, Label = i.Label });
        }
        Dialogs.ToastWindow.Show("✔ Affichage enregistré");
    }
}
