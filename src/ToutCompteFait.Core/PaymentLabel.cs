using System.Text.RegularExpressions;

namespace ToutCompteFait.Core;

/// <summary>
/// Sépare un libellé bancaire brut (« CARTE 02/10 PARC DE MOULINS », « PRLV PayPal Europe… », « VIR INST Sumup… »)
/// en moyen de paiement (CB, Prélèvement, Virement…) et libellé nettoyé. Affichage seulement : le libellé enregistré
/// n'est jamais modifié (doublons d'import, empreinte, journal d'audit).
/// </summary>
public static class PaymentLabel
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Chaque règle : préfixe reconnu en début de libellé → moyen. Le préfixe (et la date jj/mm qui le suit) est retiré.
    private static readonly (Regex Prefix, string Mode)[] Rules =
    {
        (new Regex(@"^\s*(?:paiement\s+(?:par\s+)?(?:cb|carte)|carte|cb)\b[\s:.-]*(?:\d{1,2}/\d{1,2}(?:/\d{2,4})?\b[\s:.-]*)?", Opt), "CB"),
        (new Regex(@"^\s*(?:pr[ée]l[èe]vement(?:\s+sepa)?|prlv(?:mt|t)?(?:\s+sepa)?)\b[\s:.-]*", Opt), "Prélèvement"),
        (new Regex(@"^\s*(?:vir(?:ement)?(?:\s+(?:inst(?:antan[ée])?|sepa|re[cç]u|[ée]mis))*|virt|vrmt)\b[\s:.-]*", Opt), "Virement"),
        (new Regex(@"^\s*(?:remise\s+)?(?:ch[èe]que|chq)\b[\s:.-]*(?:n[°o]?\s*\d+\b[\s:.-]*)?", Opt), "Chèque"),
        (new Regex(@"^\s*retrait(?:\s+dab|\s+esp[èe]ces)?\b[\s:.-]*(?:\d{1,2}/\d{1,2}(?:/\d{2,4})?\b[\s:.-]*)?", Opt), "Retrait"),
        (new Regex(@"^\s*esp[èe]ces\b[\s:.-]*", Opt), "Espèces"),
    };

    // Libellés conservés tels quels, seul le moyen est déduit.
    private static readonly Regex Fees = new(@"^\s*(?:frais|commission|agios|int[ée]r[êe]ts)\b", Opt);

    /// <summary>Moyen de paiement déduit du libellé (« » si aucun préfixe connu) et libellé sans ce préfixe.</summary>
    public static (string Mode, string Clean) Split(string? label)
    {
        var raw = (label ?? "").Trim();
        if (raw.Length == 0) return ("", "");
        foreach (var (prefix, mode) in Rules)
        {
            var m = prefix.Match(raw);
            if (!m.Success) continue;
            var rest = raw[m.Length..].Trim();
            return (mode, rest.Length > 0 ? rest : raw);
        }
        return Fees.IsMatch(raw) ? ("Frais", raw) : ("", raw);
    }
}
