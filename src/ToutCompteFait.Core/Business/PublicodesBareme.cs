using System.Globalization;
using System.Text.RegularExpressions;

namespace ToutCompteFait.Core.Business;

/// <summary>Lit les taux de cotisations, le versement libératoire et les plafonds du régime micro dans les règles publiques du
/// simulateur officiel de l'URSSAF (dépôt « betagouv/mon-entreprise », licence MIT, fichiers <c>.publicodes</c>). Les règles sont
/// des listes de la forme « si date &gt;= 01/2026 alors 25.6 % » : on retient la valeur en vigueur à la date du jour. Toute ligne
/// inattendue fait échouer la lecture (on préfère ne rien proposer que lire une valeur de travers). Les seuils de TVA ne sont pas
/// dans ces règles : ils viennent du barème de repli.</summary>
public static class PublicodesBareme
{
    private const string Root = "https://raw.githubusercontent.com/betagouv/mon-entreprise/master/modele-social/r%C3%A8gles/";
    public const string ContributionsUrl = Root + "dirigeant/auto-entrepreneur/cotisations-et-contributions.publicodes";
    public const string TaxUrl = Root + "dirigeant/auto-entrepreneur/imp%C3%B4t.publicodes";
    public const string CompanyUrl = Root + "entreprise/entreprise.publicodes";

    private static readonly Regex IfLine = new(@"^\s*- si: date (>=|<) (\d{2})/(\d{4})\s*$", RegexOptions.Compiled);
    private static readonly Regex ThenLine = new(@"^\s*alors: ([0-9]+(?:\.[0-9]+)?)\s*(%|€/an)\s*$", RegexOptions.Compiled);
    private static readonly Regex ElseLine = new(@"^\s*- sinon: ([0-9]+(?:\.[0-9]+)?)\s*(%|€/an)\s*$", RegexOptions.Compiled);

    /// <summary>Construit la révision en vigueur à <paramref name="today"/> à partir des trois fichiers de règles ; les valeurs
    /// non couvertes (TVA, ACRE) viennent de <paramref name="baseline"/>. Lève <see cref="FormatException"/> si une règle est
    /// introuvable ou n'a pas la forme attendue.</summary>
    public static BaremeEntry Build(string contributions, string tax, string company, DateOnly today, BaremeEntry baseline)
    {
        var dates = new List<DateOnly>();

        int RateOf(string kindHeader)
        {
            // « taux: » est la règle du taux de cotisations de l'activité, avec son historique par date.
            var lines = Block(contributions, @"^    " + Regex.Escape(kindHeader) + @":\s*$", kindHeader, "        taux:");
            return (int)Math.Round(Evaluate(lines, today, dates, kindHeader) * 100, MidpointRounding.AwayFromZero);
        }

        var rates = new Dictionary<UrssafKind, int>
        {
            [UrssafKind.Sale] = RateOf("vente restauration hébergement"),
            [UrssafKind.Service] = RateOf("service BIC"),
            [UrssafKind.LiberalSsi] = RateOf("service BNC"),
            [UrssafKind.LiberalCipav] = RateOf("service BNC Cipav")
        };

        var section = Section(tax, @"^dirigeant \. auto-entrepreneur \. impôt \. versement libératoire \. montant:\s*$", "versement libératoire");
        int Vfl(string activity)
        {
            var m = Regex.Match(section, @"entreprise \. chiffre d'affaires \. " + Regex.Escape(activity) + @"\s*\n\s*- ([0-9]+(?:\.[0-9]+)?)%");
            if (!m.Success) throw new FormatException($"Taux du versement libératoire introuvable ({activity}).");
            return (int)Math.Round(decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 100, MidpointRounding.AwayFromZero);
        }
        var vflSale = Vfl("vente restauration hébergement");
        var vflService = Vfl("service BIC");
        var vflBnc = Vfl("service BNC");
        var vfl = new Dictionary<UrssafKind, int>
        {
            [UrssafKind.Sale] = vflSale, [UrssafKind.Service] = vflService, [UrssafKind.LiberalSsi] = vflBnc, [UrssafKind.LiberalCipav] = vflBnc
        };

        long Ceiling(string header)
        {
            var lines = Block(company, @"^entreprise \. chiffre d'affaires \. seuil micro \. " + header + @":\s*$", header, null);
            return (long)Math.Round(Evaluate(lines, today, dates, header) * 100m, MidpointRounding.AwayFromZero);
        }
        var total = Ceiling("total");
        var services = Ceiling("libérale");

        foreach (var (k, r) in rates)
            if (r is < 0 or > 4000) throw new FormatException($"Taux de cotisations invraisemblable ({k.Code()}).");
        if (total <= 0 || services <= 0 || services > total) throw new FormatException("Plafonds du régime micro invraisemblables.");

        var from = dates.Where(d => d <= today).DefaultIfEmpty(baseline.From).Max();
        return baseline with
        {
            Label = from.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            From = from,
            RatesBp = rates,
            VflRatesBp = vfl,
            CeilingTotalCents = total,
            CeilingServicesCents = services
        };
    }

    // Lit une règle « liste de valeurs selon la date » : lignes du bloc après son en-tête (et après « taux: » si indiqué).
    private static List<string> Block(string text, string headerRegex, string name, string? takeAfter = null)
    {
        var all = text.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(all, l => Regex.IsMatch(l, headerRegex));
        if (start < 0) throw new FormatException($"Règle introuvable : {name}.");
        var i = start + 1;
        if (takeAfter is not null)
        {
            while (i < all.Length && all[i].TrimEnd() != takeAfter) i++;
            if (i >= all.Length) throw new FormatException($"Règle sans « taux » : {name}.");
            i++;
        }
        var indent = takeAfter is null ? 0 : takeAfter.Length - takeAfter.TrimStart().Length;
        var lines = new List<string>();
        for (; i < all.Length; i++)
        {
            var l = all[i];
            if (l.Trim().Length == 0) { if (lines.Count > 0) break; continue; }
            var lead = l.Length - l.TrimStart().Length;
            if (lead <= indent && lines.Count > 0) break;
            lines.Add(l);
        }
        return lines;
    }

    private static string Section(string text, string headerRegex, string name)
    {
        var all = text.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(all, l => Regex.IsMatch(l, headerRegex));
        if (start < 0) throw new FormatException($"Règle introuvable : {name}.");
        var end = Array.FindIndex(all, start + 1, l => l.Length > 0 && !char.IsWhiteSpace(l[0]));
        return string.Join("\n", all.Skip(start + 1).Take((end < 0 ? all.Length : end) - start - 1));
    }

    private static decimal Evaluate(List<string> lines, DateOnly today, List<DateOnly> dates, string name)
    {
        decimal? result = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var t = l.Trim();
            if (t.StartsWith("unité:") || t == "variations:") continue;
            if (IfLine.Match(l) is { Success: true } m)
            {
                if (i + 1 >= lines.Count || ThenLine.Match(lines[i + 1]) is not { Success: true } v)
                    throw new FormatException($"Règle illisible : {name}.");
                var d = new DateOnly(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), 1);
                dates.Add(d);
                var applies = m.Groups[1].Value == ">=" ? today >= d : today < d;
                if (applies && result is null) result = decimal.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture);
                i++;
            }
            else if (ElseLine.Match(l) is { Success: true } e)
            {
                result ??= decimal.Parse(e.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else throw new FormatException($"Règle de forme inattendue : {name} ({t}).");
        }
        return result ?? throw new FormatException($"Aucune valeur en vigueur : {name}.");
    }
}