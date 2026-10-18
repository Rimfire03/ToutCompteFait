namespace ToutCompteFait.Core;

/// <summary>Étape de la checklist « Premiers pas » : ce qu'il reste à faire pour bien démarrer.</summary>
/// <param name="Target">Étiquette de la page où réaliser l'étape (« dossier », « livre »…).</param>
public sealed record GettingStartedStep(string Id, string Title, string Detail, bool Done, string Target);

public sealed partial class Compta
{
    /// <summary>Checklist de démarrage calculée d'après l'état réel du dossier (aucune case à cocher à la main) : première
    /// période créée, soldes de départ, entreprise renseignée (Autoentreprise), écritures saisies ou importées, écritures
    /// ventilées, première déclaration URSSAF (Autoentreprise).</summary>
    public List<GettingStartedStep> BuildGettingStarted(long? yearId)
    {
        var steps = new List<GettingStartedStep>();
        var year = yearId is { } id ? Year(id) : null;
        steps.Add(new GettingStartedStep("year", "Créer votre première période comptable",
            "Cette période (l'exercice) regroupe toutes vos écritures : elle se crée en un clic.", year is not null, "dossier"));

        var hasOpening = year is not null && Accounts().Any(a => Opening(year.Id, a.Id) != 0);
        var entries = year is null ? new List<EntryRow>() : Entries(year.Id);

        if (Editions.Current.IsBusiness)
            steps.Add(new GettingStartedStep("company", "Renseigner votre entreprise",
                "SIREN, activité et mode de déclaration URSSAF : le logiciel adapte les catégories et les échéances.",
                !string.IsNullOrWhiteSpace(GetBusinessProfile().Name), "dossier"));

        steps.Add(new GettingStartedStep("opening", "Saisir le solde de départ de vos comptes",
            "Le solde de vos comptes bancaires au premier jour de la période, pour que les soldes affichés soient justes.",
            hasOpening || entries.Count > 0, "dossier"));
        steps.Add(new GettingStartedStep("entries", "Importer ou saisir vos premières écritures",
            "Importez le relevé de votre banque (CSV, OFX, PDF) ou saisissez une écriture à la main.",
            entries.Count > 0, "livre"));
        steps.Add(new GettingStartedStep("allocate", "Classer vos écritures en produits et charges",
            "Une écriture sans catégorie reste en jaune : choisissez-lui un produit ou une charge.",
            entries.Count > 0 && !entries.Any(r => NeedsAllocation(r) && !r.Entry.Frozen), "livre"));

        if (Editions.Current.IsBusiness)
            steps.Add(new GettingStartedStep("urssaf", "Faire et marquer votre première déclaration URSSAF",
                "Le Reporting indique les montants à saisir sur le site de l'URSSAF ; marquez la déclaration comme faite une fois envoyée.",
                Declarations().Count > 0, "reporting"));
        return steps;
    }
}
