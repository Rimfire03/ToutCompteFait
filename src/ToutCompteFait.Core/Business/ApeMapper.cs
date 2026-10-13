namespace ToutCompteFait.Core.Business;

/// <summary>Proposition de type de déclaration URSSAF d'après un code APE / NAF (rév. 2). C'est une AIDE : un même
/// code peut relever du BIC ou du BNC selon l'activité réelle, et certains codes donnent droit à PLUSIEURS types
/// (vente et prestation, par exemple) : le type principal est proposé en premier, les autres types possibles sont
/// signalés (<see cref="Suggestion.Alternatives"/>) ; l'utilisateur confirme ou corrige.</summary>
public static class ApeMapper
{
    /// <param name="Kind">Type le plus courant pour ce code.</param>
    /// <param name="Certain">Classement sans ambiguïté courante (aucune alternative à signaler).</param>
    /// <param name="Alternatives">Autres types de déclaration possibles pour une activité de ce code.</param>
    public sealed record Suggestion(UrssafKind Kind, bool Certain, string Reason, IReadOnlyList<UrssafKind> Alternatives)
    {
        /// <summary>Type principal puis types alternatifs.</summary>
        public IEnumerable<UrssafKind> AllKinds => new[] { Kind }.Concat(Alternatives).Distinct();
    }

    private static readonly UrssafKind[] None = Array.Empty<UrssafKind>();
    private static readonly UrssafKind[] Svc = { UrssafKind.Service };
    private static readonly UrssafKind[] Sal = { UrssafKind.Sale };
    private static readonly UrssafKind[] SalSvc = { UrssafKind.Sale, UrssafKind.Service };
    private static readonly UrssafKind[] Ssi = { UrssafKind.LiberalSsi };
    private static readonly UrssafKind[] SvcSsi = { UrssafKind.Service, UrssafKind.LiberalSsi };

    // Préfixes (du plus précis au plus général) → type principal + alternatives. « Certain » = classement sans ambiguïté.
    private static readonly (string Prefix, UrssafKind Kind, bool Certain, string Reason, UrssafKind[] Alt)[] Rules =
    {
        // Libéraux réglementés (CIPAV) : CIPAV ou SSI selon l'activité exacte
        ("71.11", UrssafKind.LiberalCipav, true, "architecture", None),
        ("71.12B", UrssafKind.LiberalCipav, false, "ingénierie, études techniques", Ssi),
        ("74.10", UrssafKind.LiberalCipav, false, "design, décoration", Ssi),
        ("70.22", UrssafKind.LiberalCipav, false, "conseil pour les affaires et la gestion", Ssi),

        // Intermédiaires du commerce (agents, commissions)
        ("46.1", UrssafKind.LiberalSsi, false, "intermédiaires du commerce de gros (commissions)", Svc),

        // Commerce de véhicules : vente ; réparation = prestation ; motos : les deux
        ("45.4", UrssafKind.Sale, false, "commerce et réparation de motocycles : vente et prestation", Svc),
        ("45.2", UrssafKind.Service, false, "entretien et réparation de véhicules (vente de pièces possible)", Sal),
        ("45", UrssafKind.Sale, true, "commerce de véhicules", None),
        ("46", UrssafKind.Sale, true, "commerce de gros", None),
        ("47", UrssafKind.Sale, true, "commerce de détail", None),
        ("55.2", UrssafKind.Sale, false, "hébergement de courte durée : fourniture de logement ou location meublée", Svc),
        ("55", UrssafKind.Sale, true, "hébergement (fourniture de logement)", None),
        ("56.2", UrssafKind.Sale, false, "traiteurs et restauration collective : vente de repas ou prestation de service", Svc),
        ("56", UrssafKind.Sale, true, "restauration (denrées à consommer sur place)", None),

        // Prestations commerciales ou artisanales
        ("41", UrssafKind.Service, true, "construction de bâtiments", None),
        ("42", UrssafKind.Service, true, "génie civil", None),
        ("43", UrssafKind.Service, false, "travaux de construction spécialisés (vente de matériaux sans pose = vente)", Sal),
        ("49", UrssafKind.Service, true, "transports terrestres", None),
        ("50", UrssafKind.Service, true, "transports par eau", None),
        ("51", UrssafKind.Service, true, "transports aériens", None),
        ("52", UrssafKind.Service, true, "entreposage et services auxiliaires des transports", None),
        ("53", UrssafKind.Service, true, "activités de poste et de courrier", None),
        ("77", UrssafKind.Service, true, "location et location-bail", None),
        ("79", UrssafKind.Service, true, "agences de voyage", None),
        ("80", UrssafKind.Service, true, "enquêtes et sécurité", None),
        ("81", UrssafKind.Service, true, "services relatifs aux bâtiments, aménagement paysager", None),
        ("82", UrssafKind.Service, false, "services administratifs et de soutien", Ssi),
        ("95", UrssafKind.Service, false, "réparation d'ordinateurs et de biens personnels (vente d'objets possible)", Sal),
        ("96", UrssafKind.Service, false, "services personnels (coiffure, soins, pressing… ; vente de produits possible)", Sal),
        ("68", UrssafKind.Service, false, "activités immobilières", None),
        ("61", UrssafKind.Service, false, "télécommunications", None),
        ("33", UrssafKind.Service, true, "réparation et installation de machines", None),

        // Fabrication (vente des produits fabriqués ; prestations de réparation ou de façon possibles)
        ("10", UrssafKind.Sale, false, "industries alimentaires (vente des produits fabriqués)", Svc),
        ("11", UrssafKind.Sale, false, "fabrication de boissons", Svc),
        ("13", UrssafKind.Sale, false, "fabrication de textiles", Svc),
        ("14", UrssafKind.Sale, false, "fabrication de vêtements", Svc),
        ("15", UrssafKind.Sale, false, "industrie du cuir et de la chaussure", Svc),
        ("16", UrssafKind.Sale, false, "travail du bois", Svc),
        ("18", UrssafKind.Sale, false, "imprimerie", Svc),
        ("23", UrssafKind.Sale, false, "fabrication d'autres produits minéraux", Svc),
        ("25", UrssafKind.Sale, false, "fabrication de produits métalliques", Svc),
        ("31", UrssafKind.Sale, false, "fabrication de meubles", Svc),
        ("32", UrssafKind.Sale, false, "autres industries manufacturières (bijouterie…)", Svc),

        // Libéraux (SSI) : BNC ou prestation commerciale selon la nature de l'activité
        ("62", UrssafKind.LiberalSsi, false, "programmation, conseil et services informatiques", Svc),
        ("63", UrssafKind.LiberalSsi, false, "services d'information", Svc),
        ("58", UrssafKind.LiberalSsi, false, "édition", SalSvc),
        ("59", UrssafKind.LiberalSsi, false, "production audiovisuelle", Svc),
        ("60", UrssafKind.LiberalSsi, false, "programmation et diffusion", Svc),
        ("69", UrssafKind.LiberalSsi, true, "activités juridiques et comptables", None),
        ("70", UrssafKind.LiberalSsi, false, "conseil de gestion", Svc),
        ("71", UrssafKind.LiberalSsi, false, "ingénierie, contrôles et analyses techniques", Svc),
        ("72", UrssafKind.LiberalSsi, true, "recherche-développement", None),
        ("73", UrssafKind.LiberalSsi, false, "publicité et études de marché (prestation commerciale ou BNC)", Svc),
        ("74.2", UrssafKind.LiberalSsi, false, "photographie (BNC ou prestation / vente de tirages)", SalSvc),
        ("74", UrssafKind.LiberalSsi, true, "activités spécialisées (traduction…)", None),
        ("75", UrssafKind.LiberalSsi, true, "activités vétérinaires", None),
        ("85.53", UrssafKind.LiberalSsi, false, "enseignement de la conduite", Svc),
        ("85", UrssafKind.LiberalSsi, true, "enseignement", None),
        ("86", UrssafKind.LiberalSsi, true, "activités pour la santé humaine", None),
        ("88", UrssafKind.LiberalSsi, false, "action sociale", Svc),
        ("90", UrssafKind.LiberalSsi, false, "activités créatives, artistiques et de spectacle", Svc),
        ("93", UrssafKind.LiberalSsi, false, "activités sportives, récréatives et de loisirs", Svc),
    };

    /// <summary>« 6202A », « 62.02A », « 62.02a » → « 62.02A » ; null si le format n'est pas celui d'un code APE.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var s = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (s.Length != 5 || !s.Take(4).All(char.IsDigit) || !char.IsLetter(s[4])) return null;
        return $"{s[..2]}.{s.Substring(2, 2)}{s[4]}";
    }

    /// <summary>Type proposé pour un code APE (avec les alternatives possibles), ou null si le code n'est pas reconnu.</summary>
    public static Suggestion? Suggest(string? apeCode)
    {
        var code = Normalize(apeCode);
        if (code is null) return null;
        foreach (var (prefix, kind, certain, reason, alt) in Rules)
            if (code.StartsWith(prefix, StringComparison.Ordinal))
                return new Suggestion(kind, certain, reason, alt.Where(k => k != kind).ToList());
        return null;
    }

    /// <summary>Tous les types possibles pour un ensemble de codes APE : types principaux d'abord, puis alternatives.</summary>
    public static List<UrssafKind> AllKinds(IEnumerable<string?> apeCodes)
    {
        var suggestions = apeCodes.Select(Suggest).Where(s => s is not null).Select(s => s!).ToList();
        return suggestions.Select(s => s.Kind).Concat(suggestions.SelectMany(s => s.Alternatives)).Distinct().ToList();
    }
}