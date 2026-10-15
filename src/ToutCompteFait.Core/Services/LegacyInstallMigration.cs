namespace ToutCompteFait.Core.Services;

/// <summary>Reprise des données après le changement d'arborescence de l'installateur : les versions précédentes
/// s'installaient dans <c>C:\Program Files\ToutCompteFait</c> (un dossier par version) ; elles sont maintenant dans
/// <c>C:\Program Files\Tout Compte Fait\&lt;Version&gt;</c>. La mise à jour par MSI ne déplace pas <c>Donnees</c> ni
/// <c>Sauvegardes</c> (créés à l'exécution) : au premier lancement, leur contenu est COPIÉ vers le nouveau dossier.
/// Les fichiers d'origine ne sont jamais supprimés ni modifiés.</summary>
public static class LegacyInstallMigration
{
    private static readonly string[] Folders = { "Donnees", "Sauvegardes" };

    /// <summary>Ancien dossier d'installation de cette version.</summary>
    public static string LegacyDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Editions.Current.LegacyInstallFolder);

    /// <summary>Version installée (MSI) seulement : reprend les données de l'ancien dossier ; retourne le nombre de fichiers
    /// copiés (0 si rien à faire). Sans effet pour une copie portable.</summary>
    public static int RunIfInstalled()
    {
        try
        {
            if (!InstallMode.IsInstalled) return 0;
            return Migrate(LegacyDirectory, AppSettings.AppDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;   // lecture ou écriture impossible : le logiciel démarre avec le dossier courant
        }
    }

    /// <summary>Copie <c>Donnees</c> et <c>Sauvegardes</c> de <paramref name="legacy"/> vers <paramref name="current"/>, pour
    /// chaque dossier dont la destination ne contient encore aucun fichier. Retourne le nombre de fichiers copiés.</summary>
    public static int Migrate(string legacy, string current)
    {
        if (!Directory.Exists(legacy)) return 0;
        if (string.Equals(Norm(legacy), Norm(current), StringComparison.OrdinalIgnoreCase)) return 0;
        var copied = 0;
        foreach (var folder in Folders)
        {
            var from = Path.Combine(legacy, folder);
            var to = Path.Combine(current, folder);
            if (!Directory.Exists(from)) continue;
            if (Directory.Exists(to) && Directory.EnumerateFiles(to, "*", SearchOption.AllDirectories).Any()) continue;
            copied += CopyTree(from, to);
        }
        return copied;
    }

    private static int CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        var n = 0;
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            File.Copy(file, target, overwrite: false);
            n++;
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
            n += CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
        return n;
    }

    private static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
}