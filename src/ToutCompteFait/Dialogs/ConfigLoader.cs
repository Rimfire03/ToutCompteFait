using System.Windows;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Services;

namespace ToutCompteFait.Dialogs;

/// <summary>Choix et lecture d'un fichier de configuration du serveur (voir <see cref="ServerConfigFile"/>).</summary>
internal static class ConfigLoader
{
    /// <summary>Demande un fichier .tcfconfig ; null si annulé ou invalide (l'erreur est affichée).</summary>
    public static DbConnectionInfo? Pick(Window? owner)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Charger un fichier de configuration",
            Filter = "Configuration Tout Compte Fait (*" + ServerConfigFile.Extension + ")|*" + ServerConfigFile.Extension +
                     "|Tous les fichiers (*.*)|*.*"
        };
        if (owner is not null ? dlg.ShowDialog(owner) != true : dlg.ShowDialog() != true) return null;
        return ReadFile(dlg.FileName);
    }

    /// <summary>Génère le fichier de configuration du serveur (informations de connexion à la base) : demande si le mot de
    /// passe doit y figurer, puis l'emplacement du fichier. Utilisé par Paramètres → Base de données et par le menu utilisateur.</summary>
    public static void Export(Window? owner, DbConnectionInfo info)
    {
        var includePassword = info.Password.Length > 0 &&
            Ui.Confirm("Inclure le mot de passe du serveur dans le fichier ?\n\n" +
                       "Oui : le nouvel utilisateur n'aura rien à saisir, mais le mot de passe sera lisible en clair dans le fichier " +
                       "(à ne transmettre que par un canal sûr).\n" +
                       "Non : il devra saisir le mot de passe lui-même au premier lancement.", "Mot de passe dans le fichier");
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Télécharger le fichier de configuration",
            FileName = ServerConfigFile.DefaultFileName,
            Filter = "Configuration Tout Compte Fait (*" + ServerConfigFile.Extension + ")|*" + ServerConfigFile.Extension
        };
        if (owner is not null ? dlg.ShowDialog(owner) != true : dlg.ShowDialog() != true) return;
        try
        {
            ServerConfigFile.Write(dlg.FileName, info, includePassword);
            Ui.Info("Fichier de configuration enregistré :\n" + dlg.FileName + "\n\n" +
                    "Il contient les informations de connexion au serveur de base de données" + (includePassword ? " (mot de passe compris)." : " (sans le mot de passe).") +
                    "\nAu premier lancement, le nouvel utilisateur choisit « Charger un fichier de configuration » ou le dépose sur la fenêtre.");
        }
        catch (Exception ex)
        {
            Ui.Error("Impossible d'écrire le fichier : " + ex.InnermostMessage());
        }
    }

    /// <summary>Lit un fichier de configuration connu (choisi ou déposé) ; null si invalide (l'erreur est affichée).</summary>
    public static DbConnectionInfo? ReadFile(string path)
    {
        try
        {
            return ServerConfigFile.Read(path);
        }
        catch (ComptaException ex)
        {
            Ui.Error(ex.Message);
            return null;
        }
    }
}
