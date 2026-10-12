namespace ToutCompteFait.Core.Business;

/// <summary>Proposition de type de déclaration URSSAF d'après un code APE / NAF (rév. 2). C'est une AIDE : un même
/// code peut relever du BIC ou du BNC selon l'activité réelle ; l'utilisateur confirme ou corrige.</summary>
public static class ApeMapper
{
    public sealed record Suggestion(UrssafKind Kind, bool Certain, string Reason);

    // Préfixes (du plus précis au plus général) → type. « Certain » = classement sans ambiguïté courante.
    private static readonly (string Prefix, UrssafKind Kind, bool Certain, string Reason)[] Rules =
    {
        // Libéraux réglementés (CIPAV)
        ("71.11", UrssafKind.LiberalCipav, true, "architecture"),
        ("71.12B", UrssafKind.LiberalCipav, false, "ingénierie, études techniques"),
        ("74.10", UrssafKind.LiberalCipav, false, "design, décoration"),
        ("70.22", UrssafKind.LiberalCipav, false, "conseil pour les affaires et la gestion"),

        // Commerce de véhicules : vente, sauf réparation
        ("45.2", UrssafKind.Service, true, "entretien et réparation de véhicules"),
        ("45", UrssafKind.Sale, true, "commerce de véhicules"),
        ("46", UrssafKind.Sale, true, "commerce de gros"),
        ("47", UrssafKind.Sale, true, "commerce de détail"),
        ("55", UrssafKind.Sale, true, "hébergement (fourniture de logement)"),
        ("56", UrssafKind.Sale, true, "restauration (denrées à consommer sur place)"),

        // Prestations commerciales ou artisanales
        ("41", UrssafKind.Service, true, "construction de bâtiments"),
        ("42", UrssafKind.Service, true, "génie civil"),
        ("43", UrssafKind.Service, true, "travaux de construction spécialisés"),
        ("49", UrssafKind.Service, true, "transports terrestres"),
        ("50", UrssafKind.Service, true, "transports par eau"),
        ("51", UrssafKind.Service, true, "transports aériens"),
        ("52", UrssafKind.Service, true, "entreposage et services auxiliaires des transports"),
        ("53", UrssafKind.Service, true, "activités de poste et de courrier"),
        ("77", UrssafKind.Service, true, "location et location-bail"),
        ("79", UrssafKind.Service, true, "agences de voyage"),
        ("80", UrssafKind.Service, true, "enquêtes et sécurité"),
        ("81", UrssafKind.Service, true, "services relatifs aux bâtiments, aménagement paysager"),
        ("82", UrssafKind.Service, false, "services administratifs et de soutien"),
        ("95", UrssafKind.Service, true, "réparation d'ordinateurs et de biens personnels"),
        ("96", UrssafKind.Service, true, "services personnels (coiffure, soins, pressing…)"),
        ("68", UrssafKind.Service, false, "activités immobilières"),
        ("61", UrssafKind.Service, false, "télécommunications"),
        ("33", UrssafKind.Service, true, "réparation et installation de machines"),

        // Fabrication (vente des produits fabriqués)
        ("10", UrssafKind.Sale, false, "industries alimentaires (vente des produits fabriqués)"),
        ("11", UrssafKind.Sale, false, "fabrication de boissons"),
        ("13", UrssafKind.Sale, false, "fabrication de textiles"),
        ("14", UrssafKind.Sale, false, "fabrication de vêtements"),
        ("15", UrssafKind.Sale, false, "industrie du cuir et de la chaussure"),
        ("16", UrssafKind.Sale, false, "travail du bois"),
        ("18", UrssafKind.Sale, false, "imprimerie"),
        ("23", UrssafKind.Sale, false, "fabrication d'autres produits minéraux"),
        ("25", UrssafKind.Sale, false, "fabrication de produits métalliques"),
        ("31", UrssafKind.Sale, false, "fabrication de meubles"),
        ("32", UrssafKind.Sale, false, "autres industries manufacturières (bijouterie…)"),

        // Libéraux (SSI)
        ("62", UrssafKind.LiberalSsi, false, "programmation, conseil et services informatiques"),
        ("63", UrssafKind.LiberalSsi, false, "services d'information"),
        ("58", UrssafKind.LiberalSsi, false, "édition"),
        ("59", UrssafKind.LiberalSsi, false, "production audiovisuelle"),
        ("60", UrssafKind.LiberalSsi, false, "programmation et diffusion"),
        ("69", UrssafKind.LiberalSsi, true, "activités juridiques et comptables"),
        ("70", UrssafKind.LiberalSsi, false, "conseil de gestion"),
        ("71", UrssafKind.LiberalSsi, false, "ingénierie, contrôles et analyses techniques"),
        ("72", UrssafKind.LiberalSsi, true, "recherche-développement"),
        ("73", UrssafKind.LiberalSsi, true, "publicité et études de marché"),
        ("74", UrssafKind.LiberalSsi, true, "activités spécialisées (photographie, traduction…)"),
        ("75", UrssafKind.LiberalSsi, true, "activités vétérinaires"),
        ("85", UrssafKind.LiberalSsi, true, "enseignement"),
        ("86", UrssafKind.LiberalSsi, true, "activités pour la santé humaine"),
        ("88", UrssafKind.LiberalSsi, false, "action sociale"),
        ("90", UrssafKind.LiberalSsi, false, "activités créatives, artistiques et de spectacle"),
        ("93", UrssafKind.LiberalSsi, false, "activités sportives, récréatives et de loisirs"),
    };

    /// <summary>« 6202A », « 62.02A », « 62.02a » → « 62.02A » ; null si le format n'est pas celui d'un code APE.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var s = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (s.Length != 5 || !s.Take(4).All(char.IsDigit) || !char.IsLetter(s[4])) return null;
        return $"{s[..2]}.{s.Substring(2, 2)}{s[4]}";
    }

    /// <summary>Type proposé pour un code APE, ou null si le code n'est pas reconnu.</summary>
    public static Suggestion? Suggest(string? apeCode)
    {
        var code = Normalize(apeCode);
        if (code is null) return null;
        foreach (var (prefix, kind, certain, reason) in Rules)
            if (code.StartsWith(prefix, StringComparison.Ordinal))
                return new Suggestion(kind, certain, reason);
        return null;
    }
}