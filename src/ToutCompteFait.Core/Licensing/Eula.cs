using System.Security.Cryptography;
using System.Text;

namespace ToutCompteFait.Core.Licensing;

/// <summary>Contrat de Licence Utilisateur Final (CLUF). L'acceptation est liée à l'EMPREINTE du texte : toute modification
/// du texte ci-dessous (donc de <see cref="Revision"/>, à faire évoluer avec lui) fait redemander l'acceptation à
/// l'ouverture suivante.</summary>
public static class Eula
{
    /// <summary>Révision lisible, affichée à l'utilisateur. À incrémenter à chaque modification du texte.</summary>
    public const string Revision = "2.1 — 13 octobre 2026";

    /// <summary>Empreinte (SHA-256 hexadécimal) du texte : c'est elle qui est comparée à l'acceptation enregistrée.</summary>
    public static string Hash { get; } =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Revision + "\n" + Text.Replace("\r\n", "\n")))).ToLowerInvariant();

    /// <summary>Vrai si l'utilisateur a accepté exactement ce texte.</summary>
    public static bool IsAccepted(string? acceptedHash) =>
        string.Equals(acceptedHash, Hash, StringComparison.Ordinal);

    public const string Text = """
        CONTRAT DE LICENCE UTILISATEUR FINAL (CLUF) — TOUT COMPTE FAIT

        IDENTIFICATION DE L'ÉDITEUR
        Thomas GUIDEZ, entrepreneur individuel, exerçant sous le nom commercial « TomLine prod&co », immatriculé au Registre du commerce et des sociétés de Cusset sous le numéro 522 433 333, dont l'établissement est situé 51B Rue des Tuileries, 03400 Yzeure (France). Contact : contact@tomlineprodco.fr (ci-après « l'Éditeur »).

        Le présent contrat (le « Contrat ») est conclu entre l'Éditeur et toute personne physique ou morale qui installe ou utilise le logiciel « Tout Compte Fait » (l'« Utilisateur »).

        EN CLIQUANT SUR « J'ACCEPTE », L'UTILISATEUR RECONNAÎT AVOIR LU LE CONTRAT ET EN ACCEPTE L'ENSEMBLE DES CONDITIONS. S'IL NE LES ACCEPTE PAS, IL DOIT CLIQUER SUR « JE REFUSE » ET NE PAS UTILISER LE LOGICIEL.

        ARTICLE 1 — DÉFINITIONS
        « Logiciel » : le programme « Tout Compte Fait », outil de suivi de trésorerie destiné aux associations, ses mises à jour, sa documentation et les fichiers fournis avec lui, à l'exclusion des Données.
        « Données » : les informations saisies, importées ou produites par l'Utilisateur au moyen du Logiciel (base de données, justificatifs, relevés, exports).
        « Licence » : le droit d'utilisation du Logiciel concédé par le Contrat, matérialisé par une clé de licence délivrée par l'Éditeur (démonstration ou licence complète).
        « Consommateur » : toute personne physique qui agit à des fins qui n'entrent pas dans le cadre de son activité commerciale, industrielle, artisanale, libérale ou agricole, au sens de l'article liminaire du Code de la consommation.

        ARTICLE 2 — OBJET ET ACCEPTATION
        Le Contrat définit les conditions dans lesquelles l'Éditeur concède à l'Utilisateur le droit d'utiliser le Logiciel. L'acceptation électronique (clic sur « J'accepte ») vaut signature et engage l'Utilisateur ; l'Éditeur en conserve la trace sur le poste (version du texte et date d'acceptation). L'Utilisateur qui accepte pour le compte d'une association ou d'une société déclare avoir le pouvoir de l'engager.

        ARTICLE 3 — LICENCE CONCÉDÉE
        L'Éditeur concède à l'Utilisateur un droit personnel, non exclusif, non cessible et non transférable d'utiliser le Logiciel, pour ses besoins propres, sur le nombre de postes et pendant la durée prévus par sa Licence. Ce droit couvre l'installation, le chargement, l'affichage et l'exécution du Logiciel, ainsi qu'une copie de sauvegarde à des fins de sécurité. Tous les droits non expressément concédés sont réservés.

        ARTICLE 4 — RESTRICTIONS
        Sauf accord écrit préalable de l'Éditeur et sous réserve des droits que la loi reconnaît impérativement à l'Utilisateur (notamment les articles L. 122-6-1 I, II et IV du Code de la propriété intellectuelle), il est interdit de : (a) vendre, louer, prêter, céder, distribuer ou sous-licencier le Logiciel ; (b) le mettre à disposition de tiers, y compris sous forme de service en ligne ; (c) le modifier, l'adapter, le traduire ou créer des œuvres dérivées ; (d) le décompiler ou le désassembler, hors les cas où la loi l'autorise expressément (interopérabilité) ; (e) contourner, désactiver ou altérer le contrôle de licence ou toute mesure technique de protection ; (f) communiquer sa clé de licence à un tiers ou l'utiliser au-delà du nombre de postes prévu ; (g) supprimer les mentions de propriété ou de droit d'auteur.

        ARTICLE 5 — PROPRIÉTÉ INTELLECTUELLE
        Le Logiciel, sa documentation, ses éléments graphiques et la marque « Tout Compte Fait » sont la propriété exclusive de l'Éditeur et protégés par le Code de la propriété intellectuelle et les conventions internationales. Le Contrat ne transfère à l'Utilisateur aucun droit de propriété. Les Données restent la propriété exclusive de l'Utilisateur.

        ARTICLE 6 — ACTIVATION ET GESTION DE LA LICENCE
        L'usage du Logiciel est conditionné à une Licence valide, vérifiée auprès du serveur de licences de l'Éditeur (licences.tlpc.fr) au lancement puis périodiquement. Une Licence est limitée au nombre de postes prévu ; l'Utilisateur peut désactiver un poste depuis le Logiciel (Paramètres, Licence) pour le réaffecter. Une Licence révoquée, expirée ou inconnue verrouille l'accès au Logiciel ; les Données restent dans la base de l'Utilisateur et ne sont jamais supprimées par le Logiciel. L'Éditeur ne révoque une Licence que pour un motif prévu à l'article 14 ou en cas de remboursement. Le prix, la durée et les modalités de paiement d'une Licence payante sont ceux indiqués à l'Utilisateur au moment de la commande.

        ARTICLE 7 — DONNÉES DE L'UTILISATEUR
        Les Données sont enregistrées sur les postes ou serveurs de l'Utilisateur ; elles ne sont jamais transmises à l'Éditeur, qui n'y a pas accès. L'Utilisateur en est seul responsable : il lui appartient d'effectuer des sauvegardes régulières, de conserver les pièces justificatives et de respecter les obligations légales qui lui incombent. Lorsque les Données contiennent des données personnelles de tiers (adhérents, fournisseurs, salariés), l'Utilisateur en est le responsable de traitement au sens du RGPD et doit remplir les obligations correspondantes.

        ARTICLE 8 — DONNÉES PERSONNELLES (LICENCE)
        L'Éditeur est responsable du traitement des seules données nécessaires à la gestion des licences, conformément au règlement (UE) 2016/679 (RGPD) et à la loi « Informatique et Libertés » du 6 janvier 1978. Données traitées : clé de licence, identifiant de poste anonymisé (empreinte irréversible de l'identifiant matériel), nom du poste, nom du client associé à la licence, version du Logiciel, date et adresse IP de la vérification. Finalités : vérifier et gérer la Licence, prévenir l'usage non autorisé, assurer le support. Bases légales : exécution du Contrat et intérêt légitime de l'Éditeur à protéger son Logiciel. Ces données sont conservées pendant la durée de la Licence, puis pendant la durée de prescription légale, et ne sont ni vendues ni cédées. Elles ne sont accessibles qu'à l'Éditeur et à ses éventuels prestataires techniques d'hébergement, tenus à la confidentialité. L'Utilisateur dispose des droits d'accès, de rectification, d'effacement, de limitation, d'opposition et de portabilité, exerçables en écrivant à contact@tomlineprodco.fr, et du droit d'introduire une réclamation auprès de la CNIL (www.cnil.fr).

        ARTICLE 9 — MISES À JOUR
        L'Éditeur peut publier des mises à jour du Logiciel, installées avec l'accord de l'Utilisateur. Les mises à jour sont soumises au Contrat. Le Contrat peut évoluer : toute modification de ses termes est soumise à une nouvelle acceptation au lancement suivant, et ne s'applique pas tant qu'elle n'est pas acceptée ; à défaut, la version acceptée précédemment continue de régir la relation, mais l'Éditeur peut alors mettre fin à la fourniture de nouvelles versions.

        ARTICLE 10 — COMPOSANTS TIERS
        Le Logiciel intègre des composants de tiers qui restent la propriété de leurs auteurs et sont régis par leurs propres licences. Le Contrat ne modifie pas les droits et obligations résultant de ces licences.

        ARTICLE 11 — GARANTIE
        L'Éditeur s'engage à apporter ses meilleurs soins à la conception du Logiciel, mais ne garantit pas, dans les limites permises par la loi, qu'il sera exempt d'erreur, qu'il fonctionnera sans interruption ou qu'il répondra aux besoins particuliers de l'Utilisateur. L'Éditeur s'efforce de corriger dans un délai raisonnable les anomalies qui lui sont signalées. Le Logiciel aide à tenir une comptabilité de trésorerie et à produire des exports (livre-journal, FEC) : il ne dispense pas l'Utilisateur de vérifier l'exactitude des écritures, de ses déclarations et de recourir, si nécessaire, à un expert-comptable. Les garanties légales dont bénéficie le Consommateur (article 13) ne sont pas affectées.

        ARTICLE 11 bis — NATURE DU LOGICIEL ET AVERTISSEMENT
        Le Logiciel est uniquement un outil d'aide à la gestion financière. Il ne remplace en aucun cas un expert-comptable, un commissaire aux comptes ou tout autre professionnel du chiffre ou du droit, et ne fournit aucun conseil comptable, fiscal, juridique ou financier. Le Logiciel et ses exports (livre-journal, FEC, états, rapports) n'ont fait l'objet d'aucune validation, homologation, certification ou agrément par l'administration fiscale ou par toute autre autorité, et ne sont pas attestés conformes à la réglementation comptable ou fiscale. Des erreurs, inexactitudes ou omissions peuvent donc exister, dans les calculs, les écritures, les contrôles ou les exports. L'Utilisateur reste seul responsable de l'exactitude de ses écritures et de ses déclarations, de la vérification des résultats, de la conservation des justificatifs et du respect de ses obligations comptables, fiscales et sociales ; il lui appartient de faire contrôler ses documents par un professionnel qualifié avant tout dépôt, déclaration ou décision engageante. L'Éditeur n'est pas responsable des conséquences (redressement, pénalités, majorations, décisions de gestion) de l'usage de résultats non vérifiés, sauf faute lourde ou dolosive et dans les limites de l'article 12.

        ARTICLE 12 — RESPONSABILITÉ
        Dans les limites permises par la loi, l'Éditeur n'est responsable que des dommages directs et prévisibles résultant d'un manquement à ses obligations, à l'exclusion des dommages indirects (perte de données, d'exploitation, de chance, de clientèle ou préjudice commercial). Pour l'Utilisateur professionnel ou agissant pour une association, la responsabilité de l'Éditeur, toutes causes confondues, est plafonnée au prix effectivement payé pour la Licence au cours des douze mois précédant le fait générateur. Ces limitations ne s'appliquent ni en cas de faute lourde ou dolosive, ni aux dommages corporels, ni lorsque la loi l'interdit, notamment à l'égard du Consommateur.

        ARTICLE 13 — DISPOSITIONS APPLICABLES AU CONSOMMATEUR
        Si l'Utilisateur est un Consommateur : (a) il bénéficie de la garantie légale de conformité (articles L. 224-25-12 et suivants du Code de la consommation, pour les contenus et services numériques) et de la garantie des vices cachés (articles 1641 et suivants du Code civil), que le Contrat n'exclut ni ne limite ; (b) en cas de Licence payante souscrite à distance, il dispose d'un délai de rétractation de quatorze jours à compter de la conclusion de la commande, sauf s'il demande l'exécution immédiate du service et renonce expressément à son droit de rétractation, conformément à l'article L. 221-28 du Code de la consommation ; la rétractation s'exerce par message à contact@tomlineprodco.fr.

        ARTICLE 14 — DURÉE ET RÉSILIATION
        Le Contrat prend effet à son acceptation et dure autant que la Licence est valide. L'Utilisateur peut y mettre fin à tout moment en cessant d'utiliser le Logiciel et en le désinstallant. En cas de manquement grave de l'Utilisateur à ses obligations (notamment articles 3, 4 et 6), l'Éditeur peut résilier le Contrat de plein droit, quinze jours après une mise en demeure restée sans effet, conformément à l'article 1225 du Code civil. À la fin du Contrat, quelle qu'en soit la cause, l'Utilisateur cesse d'utiliser le Logiciel et en supprime les copies ; il conserve ses Données, qu'il lui appartient d'exporter. Les articles 5, 7, 8, 12 et 16 survivent à la fin du Contrat.

        ARTICLE 15 — DISPOSITIONS GÉNÉRALES
        Le Contrat constitue l'intégralité de l'accord des parties sur son objet. Si une clause était déclarée nulle ou inapplicable, les autres resteraient en vigueur. Le fait de ne pas se prévaloir d'un manquement ne vaut pas renonciation. L'Éditeur peut céder le Contrat à un successeur dans l'exploitation du Logiciel ; l'Utilisateur ne le peut pas sans accord écrit. Aucune partie n'est responsable d'un manquement dû à un cas de force majeure au sens de l'article 1218 du Code civil. Les registres informatiques de l'Éditeur font foi entre les parties, sauf preuve contraire, de l'acceptation du Contrat et de l'état de la Licence. Le Contrat est rédigé en langue française.

        ARTICLE 16 — DROIT APPLICABLE ET LITIGES
        Le Contrat est régi par le droit français. En cas de litige, les parties recherchent d'abord une solution amiable, par réclamation écrite adressée à contact@tomlineprodco.fr. À défaut d'accord dans un délai de trente jours, le litige est porté devant les juridictions françaises compétentes : pour un Utilisateur professionnel, celles du ressort du siège de l'Éditeur (Cusset), dans la mesure où la loi le permet ; pour un Consommateur, celles désignées par les règles légales de compétence, sans que le Contrat le prive des dispositions impératives de protection du pays où il réside.

        © TomLine prod&co — Thomas Guidez. Tous droits réservés.
        """;
}
