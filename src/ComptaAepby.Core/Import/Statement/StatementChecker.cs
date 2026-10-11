using System.Globalization;

namespace ComptaAepby.Core.Import;

/// <summary>
/// Contrôle arithmétique d'un relevé lu (par OCR ou non). Une erreur de lecture d'un seul chiffre fait « tomber à côté »
/// au moins l'un des trois contrôles : c'est ce qui rend l'import sûr.
/// <list type="bullet">
/// <item>ancien solde + Σ opérations = nouveau solde ;</item>
/// <item>Σ débits et Σ crédits = totaux imprimés sur le relevé ;</item>
/// <item>solde courant imprimé après chaque opération = solde précédent + montant (localise la ligne fautive).</item>
/// </list>
/// Seules les lignes cochées « Inclure » comptent : décocher une ligne signifie « ce n'est pas une opération du relevé ».
/// </summary>
public static class StatementChecker
{
    public static StatementCheck Evaluate(IReadOnlyList<StatementRow> rows, long? opening, long? closing,
        long? totalDebits, long? totalCredits)
    {
        var included = rows.Where(r => r.Include).ToList();
        var complete = included.Where(r => r.IsComplete).ToList();
        var incomplete = included.Count - complete.Count;
        var sum = complete.Sum(r => r.AmountCents!.Value);
        var details = new List<string>();
        var passed = 0;
        var failed = 0;

        if (incomplete > 0)
            details.Add($"{incomplete} ligne(s) incomplète(s) (date ou montant manquant) : corrigez-les ou décochez-les.");

        if (opening is { } o && closing is { } c)
        {
            var expected = o + sum;
            if (expected == c) { passed++; details.Add($"✔ Ancien solde {Fmt(o)} + opérations {Fmt(sum)} = nouveau solde {Fmt(c)}."); }
            else
            {
                failed++;
                details.Add($"✘ Ancien solde {Fmt(o)} + opérations {Fmt(sum)} = {Fmt(expected)}, mais le relevé indique {Fmt(c)} " +
                            $"(écart {Fmt(c - expected)}).");
            }
        }
        else
            details.Add("Soldes de début et de fin non lus : saisissez-les pour activer le contrôle principal.");

        var debits = -complete.Where(r => r.AmountCents < 0).Sum(r => r.AmountCents!.Value);
        var credits = complete.Where(r => r.AmountCents > 0).Sum(r => r.AmountCents!.Value);
        if (totalDebits is { } td)
        {
            if (td == debits) { passed++; details.Add($"✔ Total des débits {Fmt(td)} retrouvé."); }
            else { failed++; details.Add($"✘ Total des débits lu {Fmt(debits)}, relevé {Fmt(td)} (écart {Fmt(td - debits)})."); }
        }
        if (totalCredits is { } tc)
        {
            if (tc == credits) { passed++; details.Add($"✔ Total des crédits {Fmt(tc)} retrouvé."); }
            else { failed++; details.Add($"✘ Total des crédits lu {Fmt(credits)}, relevé {Fmt(tc)} (écart {Fmt(tc - credits)})."); }
        }

        // Solde courant imprimé : contrôle ligne à ligne.
        long? last = opening;
        long acc = 0;
        var chainChecks = 0;
        var chainFails = 0;
        foreach (var r in complete)
        {
            acc += r.AmountCents!.Value;
            if (r.BalanceAfterCents is not { } b) continue;
            if (last is { } l)
            {
                chainChecks++;
                if (l + acc != b)
                {
                    chainFails++;
                    if (chainFails <= 5)
                        details.Add($"✘ Page {r.Page}, {r.Date:dd/MM/yyyy} « {Short(r.Label)} » : solde lu {Fmt(b)}, calculé {Fmt(l + acc)} " +
                                    "(la ligne ou l'une des précédentes depuis le dernier solde imprimé est mal lue).");
                }
            }
            last = b;
            acc = 0;
        }
        if (chainChecks > 0)
        {
            if (chainFails == 0) { passed++; details.Add($"✔ Solde courant cohérent sur {chainChecks} ligne(s)."); }
            else { failed++; if (chainFails > 5) details.Add($"… et {chainFails - 5} autre(s) incohérence(s) de solde."); }
        }

        var state = failed > 0 ? StatementCheckState.Mismatch
            : passed > 0 ? StatementCheckState.Verified
            : StatementCheckState.Unverifiable;
        return new StatementCheck(state, incomplete, included.Count, sum, details);
    }

    public static StatementCheck Evaluate(StatementReadResult r) =>
        Evaluate(r.Rows, r.OpeningCents, r.ClosingCents, r.TotalDebitsCents, r.TotalCreditsCents);

    /// <summary>Lignes importables, au format du reste de l'application.</summary>
    public static List<BankLine> ToBankLines(IEnumerable<StatementRow> rows) =>
        rows.Where(r => r.Include && r.IsComplete)
            .Select(r => new BankLine(r.Date!.Value, string.IsNullOrWhiteSpace(r.Label) ? "(sans libellé)" : r.Label, r.AmountCents!.Value))
            .ToList();

    private static string Fmt(long cents) => Money.Format(cents);

    private static string Short(string s) => s.Length > 30 ? s[..30] + "…" : s;

    /// <summary>Lit un montant saisi à la main (« 1 234,56 », « -12,5 »).</summary>
    public static bool TryParseUserAmount(string? text, out long cents) =>
        BankStatementParser.TryParseAmount(text, out cents);

    /// <summary>Lit une date saisie à la main ; l'année absente est complétée par <paramref name="defaultYear"/>.</summary>
    public static bool TryParseUserDate(string? text, int? defaultYear, out DateOnly date)
    {
        if (BankStatementParser.TryParseDate(text, out date)) return true;
        date = default;
        var t = (text ?? "").Trim();
        var parts = t.Split(new[] { '/', '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && defaultYear is { } y &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var d) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) &&
            m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m))
        {
            date = new DateOnly(y, m, d);
            return true;
        }
        return false;
    }
}
