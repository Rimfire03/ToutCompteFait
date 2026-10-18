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
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrushSurface");

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new ToutCompteFait.Controls.AppLogo
        {
            Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10)
        });
        root.Children.Add(new TextBlock
        {
            Text = "Bienvenue ! Où garder vos données ?",
            Style = (Style)Application.Current.FindResource("TitleText")
        });
        root.Children.Add(new TextBlock
        {
            Text = "Pour une première utilisation seul(e) sur cet ordinateur, le premier choix convient : il suffit de cliquer dessus. " +
                   "Le logiciel vous demandera ensuite de créer votre compte administrateur, puis votre première période comptable " +
                   "(l'exercice), et vous pourrez saisir vos comptes bancaires.\n\n" +
                   "Ce choix pourra être modifié plus tard dans Paramétrage appli → Base de données. Emplacement prévu :\n" + expectedPath,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 16),
            Style = (Style)Application.Current.FindResource("MutedText")
        });

        root.Children.Add(MakeChoice("Je démarre : créer mes données sur cet ordinateur (recommandé)",
            "Une base vide est créée ici, sans rien à installer. Vous créerez ensuite votre compte administrateur.",
            FirstRunChoice.CreateLocal, primary: true));
        root.Children.Add(MakeChoice("J'ai déjà des données : ouvrir un fichier existant",
            "Un fichier .db déjà utilisé : copie, sauvegarde ou dossier partagé sur le réseau.",
            FirstRunChoice.OpenFile, primary: false));
        root.Children.Add(MakeChoice("Nous sommes plusieurs : me connecter à un serveur partagé",
            "Les données sont sur un serveur (PostgreSQL, MariaDB / MySQL ou SQL Server) partagé entre les postes. Réservé à un usage à plusieurs.",
            FirstRunChoice.ConnectServer, primary: false));
        root.Children.Add(MakeChoice("J'ai reçu un fichier de configuration",
            "Fichier .tcfconfig remis par l'administrateur : il contient les informations du serveur. Vous pouvez aussi le glisser dans cette fenêtre.",
            FirstRunChoice.LoadConfig, primary: false));

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
        var text = new StackPanel { MinWidth = 530 };
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
