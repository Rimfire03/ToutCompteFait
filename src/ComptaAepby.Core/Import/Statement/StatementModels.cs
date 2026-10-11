using System.Text.Json;

namespace ComptaAepby.Core.Import;

/// <summary>Un mot lu sur une page (texte numérique du PDF ou résultat d'OCR), avec son rectangle.
/// Coordonnées en unités de la page, origine en haut à gauche.</summary>
public sealed record StatementWord(string Text, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterY => Y + Height / 2;
}

public sealed record StatementPage(int Number, double Width, double Height, IReadOnlyList<StatementWord> Words);

public enum StatementColumnKind { Date, ValueDate, Label, Debit, Credit, Amount, Balance }

public sealed record StatementColumn(StatementColumnKind Kind, double Left, double Right, string Header)
{
    public double Center => (Left + Right) / 2;
    public bool IsNumeric => Kind is StatementColumnKind.Debit or StatementColumnKind.Credit
        or StatementColumnKind.Amount or StatementColumnKind.Balance;
}

/// <summary>Mots-clés propres à une banque / une mise en page. Tous en minuscules sans accents, un mot par entrée
/// (sauf les expressions de soldes). Modifiable : un profil peut être enregistré en JSON et rechargé.</summary>
public sealed class StatementProfile
{
    public string Name { get; set; } = "Relevé générique";
    public string Description { get; set; } = "";

    public string[] DateHeaders { get; set; } = { "date", "jour" };
    public string[] ValueHeaders { get; set; } = { "valeur" };
    public string[] LabelHeaders { get; set; } =
        { "libelle", "libelles", "operation", "operations", "nature", "detail", "details", "designation", "description", "intitule", "mouvement", "mouvements" };
    public string[] DebitHeaders { get; set; } = { "debit", "debits", "debiteur" };
    public string[] CreditHeaders { get; set; } = { "credit", "credits", "crediteur" };
    public string[] AmountHeaders { get; set; } = { "montant", "montants" };
    public string[] BalanceHeaders { get; set; } = { "solde" };

    /// <summary>Expressions (sans accents) qui introduisent le solde de début de période.</summary>
    public string[] OpeningPhrases { get; set; } =
        { "ancien solde", "solde precedent", "solde de depart", "solde initial", "solde debut", "dernier solde", "solde au debut", "solde reporte au" };
    /// <summary>Expressions qui introduisent le solde de fin de période.</summary>
    public string[] ClosingPhrases { get; set; } =
        { "nouveau solde", "solde final", "solde fin", "solde de cloture", "solde en fin", "solde cloture", "solde a la date" };
    /// <summary>« solde au 31/08 » : début s'il précède toute opération, fin sinon.</summary>
    public string[] AmbiguousBalancePhrases { get; set; } = { "solde au", "solde du", "votre solde", "solde crediteur", "solde debiteur" };
    /// <summary>Lignes ignorées (reports de page, pieds de page, mentions légales).</summary>
    public string[] NoisePhrases { get; set; } =
    {
        "report", "a reporter", "suite page", "page ", "www.", "siege social", "r.c.s", "rcs ", "orias", "capital de",
        "iban", "bic ", "n° de compte", "numero de compte", "releve de compte", "releve d'identite"
    };

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public static StatementProfile? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<StatementProfile>(json); }
        catch (JsonException) { return null; }
    }
}

public static class StatementProfiles
{
    public static StatementProfile Generic() => new()
    {
        Name = "Relevé générique (détection automatique)",
        Description = "Reconnaît les en-têtes Date / Libellé / Débit / Crédit (ou Montant) et les soldes."
    };

    public static StatementProfile CaisseEpargne() => new()
    {
        Name = "Caisse d'Épargne (à affiner avec un relevé réel)",
        Description = "Mots-clés génériques ; à ajuster dès qu'un relevé de la Caisse d'Épargne est disponible.",
        // Les relevés Caisse d'Épargne / BPCE emploient volontiers « Nature de l'opération » et « Date de valeur ».
        LabelHeaders = new StatementProfile().LabelHeaders.Concat(new[] { "libelle/reference", "reference" }).ToArray()
    };

    public static IReadOnlyList<StatementProfile> BuiltIn() => new[] { Generic(), CaisseEpargne() };

    /// <summary>Profils personnalisés : un fichier .json par profil dans <paramref name="folder"/>.</summary>
    public static List<StatementProfile> LoadCustom(string folder)
    {
        var list = new List<StatementProfile>();
        if (!Directory.Exists(folder)) return list;
        foreach (var f in Directory.EnumerateFiles(folder, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var p = StatementProfile.FromJson(File.ReadAllText(f));
                if (p is not null && p.Name.Length > 0) list.Add(p);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }
}

/// <summary>Une opération lue sur le relevé, modifiable avant import.</summary>
public sealed class StatementRow
{
    public int Page { get; set; }
    public DateOnly? Date { get; set; }
    public string DateText { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Montant signé (débit négatif, crédit positif).</summary>
    public long? AmountCents { get; set; }
    public long? BalanceAfterCents { get; set; }
    /// <summary>Caractères corrigés par le lecteur (O→0…) : à vérifier sur l'image.</summary>
    public bool Repaired { get; set; }
    public List<string> Notes { get; } = new();
    /// <summary>Rectangle de la ligne dans la page (unités de la page), pour l'afficher sur l'image.</summary>
    public double Top { get; set; }
    public double Bottom { get; set; }
    public double Left { get; set; }
    public double Right { get; set; }
    public bool Include { get; set; } = true;

    public bool IsComplete => Date.HasValue && AmountCents.HasValue && AmountCents.Value != 0;
    public bool NeedsAttention => !IsComplete || Repaired || Notes.Count > 0;
}

public sealed class StatementReadResult
{
    public List<StatementRow> Rows { get; } = new();
    public long? OpeningCents { get; set; }
    public long? ClosingCents { get; set; }
    public long? TotalDebitsCents { get; set; }
    public long? TotalCreditsCents { get; set; }
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }
    public int PageCount { get; set; }
    /// <summary>Numéro du compte (11 chiffres) et intitulé imprimés au-dessus du tableau, quand le PDF regroupe plusieurs comptes.</summary>
    public string? AccountNumber { get; set; }
    public string? AccountLabel { get; set; }
    /// <summary>Remarques générales (colonnes déduites, année introuvable, aucun en-tête…).</summary>
    public List<string> Messages { get; } = new();
}

public enum StatementCheckState { Verified, Mismatch, Unverifiable }

/// <summary>Résultat du contrôle arithmétique : c'est ce contrôle, pas l'OCR, qui garantit l'exactitude.</summary>
public sealed record StatementCheck(StatementCheckState State, int IncompleteRows, int IncludedRows, long SumCents,
    IReadOnlyList<string> Details)
{
    /// <summary>Import sans condition : tout est complet et le relevé « tombe juste ».</summary>
    public bool CanImport => IncludedRows > 0 && IncompleteRows == 0 && State == StatementCheckState.Verified;
    /// <summary>Import possible seulement si l'utilisateur certifie avoir comparé avec le relevé (aucun solde lu).</summary>
    public bool CanImportAfterManualCheck => IncludedRows > 0 && IncompleteRows == 0 && State == StatementCheckState.Unverifiable;
}
