using System.Windows;
using System.Windows.Controls;
using ToutCompteFait.Core;

namespace ToutCompteFait.Dialogs;

public enum FirstRunChoice { Cancel, CreateLocal, OpenFile, ConnectServer, LoadConfig }

/// <summary>Premier lancement (aucune base trouvée) : créer une base locale vide, ouvrir un fichier existant ou se
/// connecter à une base distante (serveur PostgreSQL, MariaDB / MySQL, SQL Server).</summary>
public sealed class FirstRunWindow : Window
{
    public FirstRunChoice Choice { get; private set; } = FirstRunChoice.Cancel;

    /// <summary>Fichier de configuration déposé sur la fenêtre (choix « Charger un fichier de configuration » sans passer
    /// par la boîte de sélection).</summary>
    public string? DroppedConfigPath { get; private set; }

    public FirstRunWindow(string expectedPath)
    {
        Title = Editions.AppName + " — Première utilisation";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushBackground");

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new ToutCompteFait.Controls.AppLogo
        {
            Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10)
        });
        root.Children.Add(new TextBlock
        {
            Text = "Où se trouvent vos données ?",
            Style = (Style)Application.Current.FindResource("TitleText")
        });
        root.Children.Add(new TextBlock
        {
            Text = "Aucune base de données n'a été trouvée à l'emplacement habituel :\n" + expectedPath + "\n\n" +
                   "Choisissez comment démarrer. Un serveur de base de données est conseillé dès que plusieurs personnes " +
                   "utilisent le logiciel ; ce choix peut être modifié plus tard dans Paramétrage appli → Base de données.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 16),
            Style = (Style)Application.Current.FindResource("MutedText")
        });

        root.Children.Add(MakeChoice("Se connecter à une base distante (serveur)",
            "PostgreSQL, MariaDB / MySQL ou SQL Server : les données sont sur le serveur, partagées entre les postes.",
            FirstRunChoice.ConnectServer, primary: true));
        root.Children.Add(MakeChoice("Charger un fichier de configuration…",
            "Fichier .tcfconfig remis par l'administrateur : il contient les informations du serveur.",
            FirstRunChoice.LoadConfig, primary: false));
        root.Children.Add(MakeChoice("Créer une nouvelle base vide sur ce poste",
            "Fichier SQLite local à l'emplacement ci-dessus ; vous créerez ensuite le compte administrateur.",
            FirstRunChoice.CreateLocal, primary: false));
        root.Children.Add(MakeChoice("Ouvrir un fichier de base existant (.db)…",
            "Un fichier SQLite déjà utilisé (copie, sauvegarde ou partage réseau).",
            FirstRunChoice.OpenFile, primary: false));

        var quit = new Button
        {
            Content = "Quitter",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Style = (Style)Application.Current.FindResource("SecondaryButton"),
            IsCancel = true
        };
        root.Children.Add(quit);
        Content = root;

        AllowDrop = true;
        PreviewDragOver += (_, e) => FileDrop.Accept(e);
        PreviewDrop += (_, e) =>
        {
            e.Handled = true;
            var file = FileDrop.Files(e).FirstOrDefault();
            if (file is null) return;
            DroppedConfigPath = file;
            Choice = FirstRunChoice.LoadConfig;
            DialogResult = true;
        };
    }

    private UIElement MakeChoice(string title, string description, FirstRunChoice choice, bool primary)
    {
        var text = new StackPanel { MinWidth = 470 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        text.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Opacity = 0.8 });
        var button = new Button
        {
            Content = text,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Style = (Style)Application.Current.FindResource(primary ? "PrimaryButton" : "SecondaryButton")
        };
        button.Click += (_, _) =>
        {
            Choice = choice;
            DialogResult = true;
        };
        return button;
    }
}
