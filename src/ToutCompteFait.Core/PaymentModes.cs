namespace ToutCompteFait.Core;

/// <summary>Modes de règlement d'une opération (mention obligatoire du livre des recettes et du registre des achats d'une
/// micro-entreprise). Stockés sous forme de code court ; <see cref="Guess"/> propose un mode d'après un libellé bancaire.</summary>
public static class PaymentModes
{
    public const string Transfer = "VIREMENT";
    public const string DirectDebit = "PRELEVEMENT";
    public const string Card = "CARTE";
    public const string Cheque = "CHEQUE";
    public const string Cash = "ESPECES";
    public const string Other = "AUTRE";

    public static readonly IReadOnlyList<string> All = new[] { Transfer, DirectDebit, Card, Cheque, Cash, Other };

    public static string Label(string? code) => code switch
    {
        Transfer => "Virement",
        DirectDebit => "Prélèvement",
        Card => "Carte bancaire",
        Cheque => "Chèque",
        Cash => "Espèces",
        Other => "Autre",
        _ => ""
    };

    public static string? Normalize(string? code) =>
        code is not null && All.Contains(code.Trim().ToUpperInvariant()) ? code.Trim().ToUpperInvariant() : null;

    /// <summary>Mode déduit d'un libellé de relevé bancaire (null si rien de reconnu). Heuristique : à vérifier.</summary>
    public static string? Guess(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var s = " " + label.ToUpperInvariant() + " ";
        if (s.Contains(" VIR ") || s.Contains("VIREMENT") || s.Contains(" VIR.")) return Transfer;
        if (s.Contains("PRLV") || s.Contains("PRELEV") || s.Contains("PRÉLÈV")) return DirectDebit;
        if (s.Contains("CHEQUE") || s.Contains("CHÈQUE") || s.Contains(" CHQ ") || s.Contains("REMISE CHQ")) return Cheque;
        if (s.Contains("RETRAIT") || s.Contains("VERSEMENT ESP") || s.Contains(" ESPECES") || s.Contains(" ESPÈCES")) return Cash;
        if (s.Contains(" CB ") || s.StartsWith(" CB") || s.Contains("CARTE") || s.Contains("PAIEMENT CB") || s.Contains("PAYPAL")) return Card;
        return null;
    }
}
