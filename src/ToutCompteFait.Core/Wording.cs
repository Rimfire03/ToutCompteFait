using System.Text.RegularExpressions;

namespace ToutCompteFait.Core;

/// <summary>Vocabulaire propre à une version : pour l'Autoentreprise on parle d'« année fiscale » et non d'« exercice ».
/// Adaptation d'affichage seulement (messages, écrans, PDF) : les données, le journal d'audit et les noms internes ne
/// changent jamais. Sans effet pour les autres versions.</summary>
public static class Wording
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Adjectifs placés devant le nom : « le dernier exercice » → « la dernière année fiscale ».
    private static readonly Dictionary<string, string> Before = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nouvel"] = "nouvelle", ["ancien"] = "ancienne", ["dernier"] = "dernière", ["premier"] = "première",
        ["seul"] = "seule", ["prochain"] = "prochaine", ["même"] = "même", ["autre"] = "autre", ["bon"] = "bonne"
    };

    // Adjectifs placés après le nom : « exercice validé » → « année fiscale validée ».
    private static readonly Dictionary<string, string> After = new(StringComparer.OrdinalIgnoreCase)
    {
        ["validé"] = "validée", ["ouvert"] = "ouverte", ["clos"] = "close", ["clôturé"] = "clôturée", ["précédent"] = "précédente",
        ["suivant"] = "suivante", ["affiché"] = "affichée", ["comparé"] = "comparée", ["concerné"] = "concernée",
        ["courant"] = "courante", ["terminé"] = "terminée", ["verrouillé"] = "verrouillée", ["sélectionné"] = "sélectionnée",
        ["antérieur"] = "antérieure", ["actuel"] = "actuelle", ["passé"] = "passée", ["nouveau"] = "nouvelle",
        ["choisi"] = "choisie", ["existant"] = "existante"
    };

    private static readonly Regex DeterminerBefore = new(
        @"\b(un|le|cet|ce|du|au|tout|chaque)\s+(?:(nouvel|ancien|dernier|premier|seul|prochain|même|autre|bon)\s+)?(exercices?)\b", Opt);
    private static readonly Regex BareBefore = new(@"\b(nouvel|ancien|dernier|premier|seul|prochain|bon)(s?)\s+(exercices?)\b", Opt);
    private static readonly Regex DeterminerPlural = new(@"\b(tous\s+les|tous)\s+(exercices)\b", Opt);
    private static readonly Regex AfterAdjective = new(
        @"\b(années?\s+fiscales?)(\s+(?:non |déjà |encore |toujours |bien |pas )?)(" + string.Join("|", After.Keys) + @")(s?)\b", Opt);
    private static readonly Regex Noun = new(@"\bexercices?\b", Opt);
    private static readonly Regex NounPlural = new(@"\bexercices\b", Opt);

    // « Paramétrage du dossier » devient « Gestion de l'entreprise » (rubrique où l'on retrouve le statut et les informations).
    private static readonly Regex Folder = new(@"paramétrage du dossier", Opt);

    /// <summary>Vrai si le texte contient un mot ou une expression à adapter (test rapide avant tout remplacement).</summary>
    public static bool Needs(string? s) =>
        s is not null && (s.IndexOf("xercice", StringComparison.OrdinalIgnoreCase) >= 0
                          || s.IndexOf("paramétrage du dossier", StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>Texte adapté à la version courante (inchangé hors Autoentreprise).</summary>
    public static string Adapt(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!Editions.Current.IsBusiness) return text;
        return Apply(text);
    }

    /// <summary>Remplacement et accords, sans tenir compte de la version (testable).</summary>
    public static string Apply(string text)
    {
        if (!Needs(text)) return text;
        text = Folder.Replace(text, m => MatchCase(m.Value, "gestion de l'entreprise"));
        if (text.IndexOf("xercice", StringComparison.OrdinalIgnoreCase) < 0) return text;

        // 1. Déterminant (+ adjectif antéposé) : genre féminin.
        text = DeterminerBefore.Replace(text, m =>
        {
            var det = m.Groups[1].Value;
            var adj = m.Groups[2].Success ? m.Groups[2].Value : null;
            var plural = m.Groups[3].Value.EndsWith('s') || m.Groups[3].Value.EndsWith('S');
            var feminine = det.ToLowerInvariant() switch
            {
                "un" => "une", "le" => "la", "cet" => "cette", "ce" => "cette", "tout" => "toute", "chaque" => "chaque",
                "du" => "de la", "au" => "à la", _ => det
            };
            feminine = MatchCase(det, feminine);
            var adjF = adj is null ? "" : MatchCase(adj, Before[adj]) + " ";
            return $"{feminine} {adjF}{(plural ? "années fiscales" : "année fiscale")}";
        });
        text = BareBefore.Replace(text, m =>
            MatchCase(m.Groups[1].Value, Before[m.Groups[1].Value]) + m.Groups[2].Value + " " +
            (m.Groups[3].Value.EndsWith('s') ? "années fiscales" : "année fiscale"));
        text = DeterminerPlural.Replace(text, m => MatchCase(m.Groups[1].Value, m.Groups[1].Value.StartsWith("tous les", StringComparison.OrdinalIgnoreCase)
            ? "toutes les" : "toutes") + " années fiscales");

        // 2. Le nom lui-même (l'article élidé « l'exercice » devient « l'année fiscale » : même élision).
        text = NounPlural.Replace(text, m => MatchCase(m.Value, "années fiscales"));
        text = Noun.Replace(text, m => MatchCase(m.Value, "année fiscale"));

        // 3. Accord des adjectifs placés après le nom.
        text = AfterAdjective.Replace(text, m =>
        {
            var fem = After[m.Groups[3].Value];
            var plural = m.Groups[1].Value.Contains("fiscales", StringComparison.OrdinalIgnoreCase);
            var word = MatchCase(m.Groups[3].Value, fem);
            if (plural && !word.EndsWith('s')) word += "s";
            return m.Groups[1].Value + m.Groups[2].Value + word;
        });
        return text;
    }

    private static string MatchCase(string original, string replacement) =>
        original.Length > 0 && char.IsUpper(original[0]) && replacement.Length > 0
            ? char.ToUpperInvariant(replacement[0]) + replacement[1..]
            : replacement;
}
