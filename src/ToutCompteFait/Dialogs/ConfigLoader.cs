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
        try
        {
            return ServerConfigFile.Read(dlg.FileName);
        }
        catch (ComptaException ex)
        {
            Ui.Error(ex.Message);
            return null;
        }
    }
}
