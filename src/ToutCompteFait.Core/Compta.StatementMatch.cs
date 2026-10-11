using ToutCompteFait.Core.Import;

namespace ToutCompteFait.Core;

/// <summary>Compte reconnu dans l'en-tête d'un relevé, avec l'identifiant qui a servi à le reconnaître.</summary>
public sealed record StatementAccountMatch(Account Account, string Keyword);

public sealed partial class Compta
{
    /// <summary>Longueur minimale (lettres et chiffres) d'un identifiant de relevé : évite les correspondances par hasard.</summary>
    public const int MinStatementKeyLength = 4;

    /// <summary>Découpe les identifiants saisis (séparés par « ; », retour à la ligne ou tabulation).</summary>
    public static List<string> SplitStatementMatch(string? text) =>
        (text ?? "").Split(new[] { ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private static string? CleanStatementMatch(string? text)
    {
        var parts = SplitStatementMatch(text);
        if (parts.Count == 0) return null;
        foreach (var p in parts)
            if (BankStatementParser.NormalizeKey(p).Length < MinStatementKeyLength)
                throw new ComptaException($"L'identifiant de relevé « {p} » est trop court : au moins {MinStatementKeyLength} lettres ou chiffres " +
                                          "(numéro de compte, IBAN, nom du compte tel qu'écrit par la banque…).");
        var joined = string.Join(" ; ", parts);
        if (joined.Length > 500) throw new ComptaException("Les identifiants de relevé sont trop longs (500 caractères maximum).");
        return joined;
    }

    /// <summary>Comptes dont un identifiant figure dans l'en-tête du relevé (texte brut du fichier). Aucun, un seul
    /// (détection fiable) ou plusieurs (ambigu : à choisir à la main).</summary>
    public List<StatementAccountMatch> MatchStatementAccounts(string statementText)
    {
        var header = BankStatementParser.NormalizeKey(BankStatementParser.StatementHeader(statementText));
        var result = new List<StatementAccountMatch>();
        if (header.Length == 0) return result;
        foreach (var a in Accounts())
        {
            foreach (var key in SplitStatementMatch(a.StatementMatch))
            {
                var norm = BankStatementParser.NormalizeKey(key);
                if (norm.Length >= MinStatementKeyLength && header.Contains(norm, StringComparison.Ordinal))
                {
                    result.Add(new StatementAccountMatch(a, key));
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>Au moins un compte a des identifiants de relevé renseignés.</summary>
    public bool HasStatementIdentifiers() => Accounts().Any(a => !string.IsNullOrWhiteSpace(a.StatementMatch));
}
