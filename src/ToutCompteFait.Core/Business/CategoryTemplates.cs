namespace ToutCompteFait.Core.Business;

/// <summary>Une catégorie proposée par un modèle : nom compréhensible, numéro de compte (FEC) usuel et, pour un produit,
/// type de déclaration URSSAF.</summary>
public sealed record TemplateItem(CategoryKind Kind, string Name, string AccountNumber, UrssafKind? Urssaf = null);

/// <summary>Modèle de catégories pour une activité (vente, fabrication, prestation, profession libérale). Le numéro de compte
/// est celui du plan comptable général habituellement utilisé : il n'est qu'une proposition, modifiable ensuite.</summary>
public sealed record CategoryTemplate(string Id, string Name, string Description, IReadOnlyList<TemplateItem> Items);

public static class CategoryTemplates
{
    private static TemplateItem P(string name, string account, UrssafKind kind) => new(CategoryKind.Product, name, account, kind);
    private static TemplateItem C(string name, string account) => new(CategoryKind.Charge, name, account);

    public static IReadOnlyList<CategoryTemplate> All { get; } = new List<CategoryTemplate>
    {
        new("vente", "Vente de marchandises (commerce, revente)",
            "Vous achetez des produits pour les revendre : boutique, vente en ligne, brocante.",
            new[]
            {
                P("Ventes de marchandises", "707000", UrssafKind.Sale),
                C("Achats de marchandises", "607000"), C("Frais de port et transport", "624000"), C("Emballages", "602600"),
                C("Frais bancaires et plateformes de paiement", "627000"), C("Assurance", "616000"),
                C("Publicité et communication", "623000"), C("Fournitures administratives", "606400")
            }),
        new("fabrication", "Fabrication, artisanat (matières premières)",
            "Vous achetez des matières premières pour fabriquer ou transformer : artisan, créateur, restauration.",
            new[]
            {
                P("Ventes de produits fabriqués", "701000", UrssafKind.Sale), P("Prestations de services", "706000", UrssafKind.Service),
                C("Matières premières", "601000"), C("Fournitures et petit matériel", "606300"), C("Sous-traitance", "611000"),
                C("Énergie (électricité, gaz)", "606100"), C("Transport et livraison", "624000"), C("Assurance", "616000"),
                C("Publicité et communication", "623000"), C("Frais bancaires", "627000"), C("Impôts et taxes (CFE)", "635000")
            }),
        new("prestation", "Prestation de services",
            "Vous vendez votre temps ou votre savoir-faire : conseil, dépannage, formation, services à la personne.",
            new[]
            {
                P("Prestations de services", "706000", UrssafKind.Service),
                C("Fournitures administratives", "606400"), C("Logiciels et abonnements", "651000"), C("Déplacements", "625100"),
                C("Téléphone et internet", "626000"), C("Assurance professionnelle", "616000"), C("Honoraires (comptable…)", "622600"),
                C("Publicité et communication", "623000"), C("Frais bancaires", "627000"), C("Impôts et taxes (CFE)", "635000")
            }),
        new("liberale", "Profession libérale (BNC)",
            "Vous exercez une profession libérale : consultant, thérapeute, graphiste, formateur indépendant.",
            new[]
            {
                P("Honoraires", "706000", UrssafKind.LiberalSsi),
                C("Loyer du cabinet ou coworking", "613200"), C("Documentation et formation", "618100"), C("Assurance responsabilité civile", "616000"),
                C("Honoraires comptables", "622600"), C("Déplacements", "625100"), C("Téléphone et internet", "626000"),
                C("Logiciels et abonnements", "651000"), C("Frais bancaires", "627000"), C("Impôts et taxes (CFE)", "635000")
            })
    };

    public static CategoryTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);
}
