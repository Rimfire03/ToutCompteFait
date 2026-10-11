using System.Security.Cryptography;
using System.Text;

namespace ToutCompteFait.Core.Licensing;

/// <summary>Contrat de Licence Utilisateur Final (CLUF). L'acceptation est liée à l'EMPREINTE du texte : toute modification
/// du texte ci-dessous (donc de <see cref="Revision"/>, à faire évoluer avec lui) fait redemander l'acceptation à
/// l'ouverture suivante.</summary>
public static class Eula
{
    /// <summary>Révision lisible, affichée à l'utilisateur. À incrémenter à chaque modification du texte.</summary>
    public const string Revision = "1.0 — 12 octobre 2026";

    /// <summary>Empreinte (SHA-256 hexadécimal) du texte : c'est elle qui est comparée à l'acceptation enregistrée.</summary>
    public static string Hash { get; } =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Revision + "\n" + Text.Replace("\r\n", "\n")))).ToLowerInvariant();

    /// <summary>Vrai si l'utilisateur a accepté exactement ce texte.</summary>
    public static bool IsAccepted(string? acceptedHash) =>
        string.Equals(acceptedHash, Hash, StringComparison.Ordinal);

    public const string Text = """
        CONTRAT DE LICENCE UTILISATEUR FINAL — TOUT COMPTE FAIT

        Éditeur : TomLine prod&co (« l'Éditeur »).

        Veuillez lire attentivement ce contrat. En cliquant sur « J'accepte », vous reconnaissez avoir lu et accepté ses
        termes. Si vous les refusez, n'utilisez pas le logiciel.

        1. Objet
        Le logiciel « Tout Compte Fait » (le « Logiciel ») est un outil de suivi de trésorerie destiné aux associations.
        Le présent contrat vous concède un droit personnel, non exclusif et non cessible d'utiliser le Logiciel selon la
        licence qui vous a été délivrée (démonstration, licence complète, fonctionnalités incluses).

        2. Licence et activation
        L'usage du Logiciel est conditionné à une licence valide, vérifiée auprès du serveur de licences de l'Éditeur
        (licences.tlpc.fr) au lancement puis périodiquement. Une licence est limitée au nombre de postes prévu. Vous pouvez
        désactiver un poste depuis le Logiciel (Paramètres, Licence). Une licence révoquée, expirée ou inconnue verrouille
        l'accès au Logiciel ; vos données restent dans votre base et ne sont jamais supprimées par le Logiciel.

        3. Données personnelles et confidentialité
        Vos données comptables (base de données, justificatifs, relevés) restent sur vos postes ou vos serveurs : elles ne
        sont jamais transmises à l'Éditeur. Seules les informations suivantes sont envoyées au serveur de licences : la clé de
        licence, un identifiant de poste anonymisé (empreinte irréversible de l'identifiant matériel), le nom du poste et,
        le cas échéant, le nom du client associé à la licence. Ces informations servent uniquement à vérifier et gérer la
        licence. Vous disposez d'un droit d'accès, de rectification et de suppression en écrivant à l'Éditeur.

        4. Restrictions
        Il est interdit de copier, décompiler, modifier, redistribuer, louer ou sous-licencier le Logiciel, de contourner ou
        désactiver le contrôle de licence, ou de l'utiliser au-delà des droits de la licence. Les contributions et
        composants tiers restent soumis à leurs propres licences.

        5. Propriété intellectuelle
        Le Logiciel, sa documentation et ses éléments graphiques sont la propriété exclusive de l'Éditeur. Le présent contrat
        ne vous en transfère aucun droit de propriété.

        6. Responsabilité de l'utilisateur
        Le Logiciel aide à tenir une comptabilité de trésorerie et à produire des exports (livre-journal, FEC). Il ne dispense
        pas de vérifier l'exactitude des écritures, de conserver les pièces justificatives et sauvegardes exigées par la
        loi, ni de recourir à un expert-comptable lorsque cela est nécessaire. Vous restez seul responsable de votre
        comptabilité, de vos déclarations et de la sauvegarde régulière de vos données.

        7. Garantie et responsabilité de l'Éditeur
        Le Logiciel est fourni « en l'état ». L'Éditeur ne garantit pas l'absence totale d'erreur ou d'interruption. Dans les
        limites autorisées par la loi, sa responsabilité est limitée aux dommages directs et plafonnée au prix payé pour la
        licence ; il ne répond d'aucun dommage indirect (perte de données, d'exploitation ou de chance).

        8. Mises à jour
        L'Éditeur peut publier des mises à jour du Logiciel, installées avec votre accord. Les termes du présent contrat
        peuvent évoluer : en cas de modification, votre acceptation vous sera de nouveau demandée au prochain lancement.

        9. Durée et résiliation
        Le contrat dure aussi longtemps que votre licence est valide. Il prend fin de plein droit en cas de manquement grave
        aux présentes, d'expiration ou de révocation de la licence. Vous devez alors cesser d'utiliser le Logiciel.

        10. Droit applicable
        Le présent contrat est soumis au droit français. À défaut de résolution amiable, les tribunaux français sont seuls
        compétents, sous réserve des règles protectrices applicables aux consommateurs.
        """;
}
