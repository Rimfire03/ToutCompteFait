using ToutCompteFait.Core;
using System.Windows;
using ToutCompteFait.Core.Services;

namespace ToutCompteFait;

/// <summary>Version du logiciel et mise à jour automatique (releases GitHub du dépôt, comme FiltresApp).</summary>
public partial class App
{
    public static UpdateService Updater { get; } = new();

    /// <summary>Version courante (balise &lt;Version&gt; du .csproj), comparée à la dernière release GitHub.</summary>
    public static string CurrentVersion => TomLine.AppInfo.AppVersion.Current(System.Reflection.Assembly.GetExecutingAssembly());

    /// <summary>Canal de la build (« dev » pour une release de la branche dev), vide pour une build Main.</summary>
    public static string BuildChannel => TomLine.AppInfo.AppVersion.Channel(System.Reflection.Assembly.GetExecutingAssembly());

    public static bool IsDevBuild => TomLine.AppInfo.AppVersion.IsDev(BuildChannel);

    public static string AppVersionText => TomLine.AppInfo.AppVersion.Text(CurrentVersion, BuildChannel);

    /// <summary>Vérification selon le canal choisi dans les réglages ; depuis une build Dev, un retour sur Main est
    /// proposé si la dernière version Main partage la version de base de données du logiciel.</summary>
    public static Task<UpdateInfo?> CheckForUpdateAsync() =>
        Updater.CheckForUpdateAsync(CurrentVersion, Settings.UpdateChannel, IsDevBuild, DbContextFactory.LatestVersion);

    /// <summary>La base a été mise à jour par une version plus récente du logiciel : propose de l'installer. La base
    /// n'est pas touchée. Renvoie true si l'installation a été lancée (le logiciel quitte alors sans autre message).</summary>
    private static bool OfferUpdateForNewerDatabase()
    {
        UpdateInfo? info;
        try
        {
            info = Task.Run(() => Updater.CheckForUpdateAsync(CurrentVersion, Settings.UpdateChannel)).GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
        if (info is null) return false;
        var accepted = MessageBox.Show(
            $"La base de données a été mise à jour par une version plus récente du logiciel : votre version ({CurrentVersion}) ne peut pas l'ouvrir.\n\n" +
            $"La version {info.Version} est disponible. La télécharger et l'installer maintenant ? " +
            "La base de données ne sera pas modifiée. Le logiciel va se fermer puis redémarrer automatiquement.",
            "Mise à jour du logiciel nécessaire", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (accepted != MessageBoxResult.Yes) return false;
        try
        {
            Task.Run(() => Updater.DownloadAndApplyAsync(info)).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            ShowUpdateFailure(info, ex.Message);
            return false;
        }
    }

    /// <summary>Vérification silencieuse au démarrage : ne bloque jamais le lancement et n'interrompt l'utilisateur que
    /// si une version plus récente existe. Un échec de la vérification (réseau, API GitHub) reste silencieux ; le bouton
    /// « Vérifier maintenant » des réglages affiche l'erreur réelle.</summary>
    private static async Task CheckForUpdateOnStartupAsync()
    {
        UpdateInfo? info;
        try
        {
            info = await CheckForUpdateAsync();
        }
        catch
        {
            return;
        }
        if (info is null) return;
        if (!Session.Can(ToutCompteFait.Core.Security.Permission.ManageDatabase)) return;   // seul un administrateur installe

        var proceed = Ui.Confirm(
            (info.IsChannelSwitch
                ? $"Vous avez choisi le canal Main. La dernière version Main ({info.Version}) utilise la même base de données que votre version actuelle ({AppVersionText}).\n\n"
                : $"Une nouvelle version {info.Version} est disponible (version actuelle : {AppVersionText}).\n\n") +
            "Voulez-vous la télécharger et l'installer maintenant ? Une copie de sauvegarde de la base de données sera faite avant toute chose. " +
            "Le logiciel va se fermer puis redémarrer automatiquement.\n\n" +
            "Vous pouvez désactiver cette vérification automatique dans Paramètres → Mises à jour.",
            info.IsChannelSwitch ? "Retour sur le canal Main" : "Mise à jour disponible");
        if (!proceed) return;
        await InstallUpdateAsync(info);
    }

    /// <summary>Sauvegarde la base (si elle est inscriptible), télécharge et applique la mise à jour, puis quitte.</summary>
    public static async Task InstallUpdateAsync(UpdateInfo info)
    {
        try
        {
            if (!IsReadOnly)
            {
                try
                {
                    await Task.Run(() => DbFactory.CreateBackup("avant-mise-a-jour"));
                }
                catch (Exception ex)
                {
                    // La mise à jour du logiciel ne modifie pas la base : la sauvegarde préalable est une précaution.
                    if (!Ui.Confirm("La sauvegarde de la base avant la mise à jour a échoué :\n" + ex.InnermostMessage() +
                                    "\n\nInstaller quand même la mise à jour ? (L'installation ne modifie pas vos données ; " +
                                    "une migration éventuelle de la base fera sa propre sauvegarde.)", "Sauvegarde impossible"))
                        return;
                }
            }
            await Updater.DownloadAndApplyAsync(info);
            Current.Shutdown();
        }
        catch (Exception ex)
        {
            ShowUpdateFailure(info, ex.Message);
        }
    }

    /// <summary>La mise à jour automatique a échoué : explique l'erreur et propose la page de téléchargement pour une
    /// installation manuelle (zip portable ou installateur MSI).</summary>
    public static void ShowUpdateFailure(UpdateInfo info, string reason)
    {
        var url = string.IsNullOrWhiteSpace(info.ReleaseUrl) ? info.AssetUrl : info.ReleaseUrl;
        var answer = MessageBox.Show(
            $"La mise à jour automatique vers la version {info.Version} a échoué :\n{reason}\n\n" +
            "Vous pouvez installer cette version manuellement : téléchargez-la depuis la page ci-dessous (installateur .msi " +
            "ou archive .zip), puis lancez-la. Vos données ne sont pas touchées.\n\n" + url +
            "\n\nOuvrir la page de téléchargement maintenant ?",
            "Mise à jour impossible", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Clipboard.SetText(url);
            MessageBox.Show("Impossible d'ouvrir le navigateur. Le lien a été copié dans le presse-papiers :\n" + url,
                "Mise à jour impossible", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}