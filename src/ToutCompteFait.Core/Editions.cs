using System.Reflection;

namespace ToutCompteFait.Core;

/// <summary>Version (« édition ») du logiciel : le même code est compilé en plusieurs versions qui s'installent côte à côte
/// (un exécutable, un dossier d'installation, des réglages et une licence par version). L'édition est choisie à la
/// compilation (<c>-p:Edition=asso</c>, métadonnée d'assembly <c>Edition</c>) ; les numéros de version sont communs.
/// <para>Règles de nommage (identiques dans <c>editions.json</c>, vérifiées par les tests) : exécutable
/// <c>ToutCompteFait&lt;Id&gt;.exe</c>, nom affiché « Tout Compte Fait &lt;Id&gt; ». La version <c>asso</c>, historique,
/// garde les noms de données d'origine (<c>ToutCompteFait</c> : dossier de réglages, clé de registre, magasin de licence)
/// pour que les installations et licences existantes continuent de fonctionner sans manipulation.</para></summary>
public sealed record AppEdition(string Id)
{
    /// <summary>Identifiant de la version « historique » : ses noms de données ne portent pas de suffixe.</summary>
    public const string Legacy = "asso";

    /// <summary>« Asso », « Pro »… (identifiant avec majuscule initiale).</summary>
    public string Label => Id.Length == 0 ? "" : char.ToUpperInvariant(Id[0]) + Id[1..];

    /// <summary>« Tout Compte Fait Asso » : nom affiché (fenêtres, raccourci, installateur).</summary>
    public string AppName => "Tout Compte Fait " + Label;

    /// <summary>« version Asso » : mention ajoutée au numéro de version commun.</summary>
    public string VersionMention => "version " + Label;

    /// <summary>Nom de base de l'exécutable et des fichiers publiés : <c>ToutCompteFaitAsso</c>.</summary>
    public string ExeBaseName => "ToutCompteFait" + Label;

    public string ExeFileName => ExeBaseName + ".exe";

    /// <summary>Nom des emplacements propres à l'installation : dossier de réglages (%LOCALAPPDATA%), fichier de réglages,
    /// clé de registre <c>HKLM\SOFTWARE\TomLine\&lt;DataName&gt;</c>, magasin de licence, dossier d'installation.
    /// <c>ToutCompteFait</c> pour la version historique, sinon <see cref="ExeBaseName"/>.</summary>
    public string DataName => Id == Legacy ? "ToutCompteFait" : ExeBaseName;

    /// <summary>Dossier parent commun des installations (par machine) : <c>C:\Program Files\Tout Compte Fait</c>.</summary>
    public const string InstallRootFolder = "Tout Compte Fait";

    /// <summary>Sous-dossier de cette version dans <see cref="InstallRootFolder"/> : <c>Asso</c>, <c>Autoentreprise</c>.</summary>
    public string InstallSubFolder => Label;

    /// <summary>Ancien dossier d'installation (avant le regroupement sous <see cref="InstallRootFolder"/>) : directement sous
    /// Program Files, <c>ToutCompteFait</c> pour la version historique, sinon le nom de l'exécutable. Sert à reprendre les
    /// données (Donnees, Sauvegardes) laissées en place par la mise à jour.</summary>
    public string LegacyInstallFolder => Id == Legacy ? "ToutCompteFait" : ExeBaseName;

    /// <summary>Produit côté serveur de licences : commun à toutes les versions (la version est portée par la licence).</summary>
    public string ProductSlug => "tout-compte-fait";

    /// <summary>Version envoyée au serveur de licences : une licence ne donne droit qu'à une seule version.</summary>
    public string LicenseEdition => Id;

    /// <summary>Nom par défaut de la base sur serveur (propre à la version : deux versions ne partagent pas une base).</summary>
    public string DefaultServerDatabaseName => Id == Legacy ? "tout_compte_fait" : "tout_compte_fait_" + Id;

    /// <summary>Fichiers joints à une release GitHub pour cette version : <c>ToutCompteFaitAsso-v1.3.0-win-x64.zip</c>.</summary>
    public string AssetPrefix => ExeBaseName + "-v";

    /// <summary>Préfixe des fichiers joints avant l'existence des versions (releases ≤ 1.2.x) : repris pour la version
    /// historique seulement.</summary>
    public string? LegacyAssetPrefix => Id == Legacy ? "ToutCompteFait-v" : null;

    // ------------------------------------------------------------------ fonctions propres à la version

    /// <summary>Remboursements aux membres (note de frais d'un membre de l'équipe) : propre aux associations ; absent de
    /// la version Autoentreprise (écran, type d'écriture proposé, aide).</summary>
    public bool HasMemberRefunds => Id != "autoentreprise";

    /// <summary>Version pour une entreprise individuelle : profil de l'entreprise (SIREN, codes APE), ventilation des recettes\n    /// par type de déclaration URSSAF, onglet Reporting de déclaration du chiffre d'affaires.</summary>
    public bool IsBusiness => Id == "autoentreprise";

    /// <summary>Identifiant valide : minuscules et chiffres, commençant par une lettre.</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 20 && char.IsAsciiLetterLower(id[0]) && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));
}

public static class Editions
{
    /// <summary>Version de ce programme (métadonnée d'assembly <c>Edition</c> posée par la compilation ; « asso » sinon,
    /// notamment dans les tests).</summary>
    public static AppEdition Current { get; } = ReadCurrent();

    public static string AppName => Current.AppName;

    private static AppEdition ReadCurrent()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var id = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "Edition")?.Value?.Trim().ToLowerInvariant();
        return new AppEdition(AppEdition.IsValidId(id) ? id! : AppEdition.Legacy);
    }
}