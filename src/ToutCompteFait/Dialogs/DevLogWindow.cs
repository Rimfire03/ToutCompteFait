using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Dialogs;

/// <summary>Nouveautés de la version : devlog écrit en français, affiché après une mise à jour (ou à la demande).</summary>
public sealed class DevLogWindow : Window
{
    public DevLogWindow(IReadOnlyList<DevLogEntry> entries)
    {
        Title = "Nouveautés — " + Editions.AppName;
        Width = 560;
        MaxHeight = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 8) };
        foreach (var e in entries)
        {
            panel.Children.Add(new TextBlock { Text = e.Title, Style = (Style)Application.Current.FindResource("TitleText") });
            if (!string.IsNullOrWhiteSpace(e.Date) && DateOnly.TryParse(e.Date, out var d))
                panel.Children.Add(new TextBlock { Text = $"Version {e.Version} — {d:dd/MM/yyyy}", Style = (Style)Application.Current.FindResource("MutedText"), Margin = new Thickness(0, 0, 0, 8) });
            foreach (var item in e.Items)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var bullet = new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.Bold };
                DockPanel.SetDock(bullet, Dock.Left);
                row.Children.Add(bullet);
                row.Children.Add(new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(row);
            }
            panel.Children.Add(new Border { Height = 12 });
        }

        var ok = new Button
        {
            Content = "J'ai compris", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(22, 4, 22, 16), Style = (Style)Application.Current.FindResource("PrimaryButton")
        };
        ok.Click += (_, _) => Close();

        var root = new DockPanel();
        DockPanel.SetDock(ok, Dock.Bottom);
        root.Children.Add(ok);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel });
        Content = root;
    }

    /// <summary>Devlog embarqué dans l'exécutable (vide si absent).</summary>
    public static IReadOnlyList<DevLogEntry> LoadEmbedded()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("devlog.json");
            if (s is null) return Array.Empty<DevLogEntry>();
            using var r = new StreamReader(s, System.Text.Encoding.UTF8);
            return DevLog.Parse(r.ReadToEnd());
        }
        catch
        {
            return Array.Empty<DevLogEntry>();
        }
    }
}