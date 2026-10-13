namespace ToutCompteFait.Core;

/// <summary>Une ligne de comparaison : un libellé et un montant (centimes) par exercice, dans l'ordre chronologique.</summary>
public sealed record ComparisonLine(string Name, IReadOnlyList<long> Values)
{
    private long Last => Values.Count > 0 ? Values[^1] : 0;
    private long Before => Values.Count > 1 ? Values[^2] : 0;

    /// <summary>Écart entre l'exercice affiché (dernier) et le précédent.</summary>
    public long Delta => Last - Before;

    /// <summary>Écart en pourcentage du précédent ; null si le précédent est nul (pas de base de comparaison).</summary>
    public decimal? Percent => Values.Count > 1 && Before != 0 ? Math.Round((decimal)Delta / Math.Abs(Before) * 100m, 1) : null;

    public bool IsZero => Values.All(v => v == 0);
}

/// <summary>Comparaison des résultats de plusieurs exercices consécutifs : synthèse, produits et charges par catégorie
/// (regroupées par nom), résultat par opération. Calcul pur, à partir des bilans déjà calculés.</summary>
public sealed class YearComparison
{
    /// <summary>Exercices comparés, du plus ancien au plus récent (le dernier est l'exercice affiché).</summary>
    public required IReadOnlyList<FiscalYear> Years { get; init; }

    /// <summary>Produits, charges, résultat, trésorerie et immobilisations à la clôture.</summary>
    public required IReadOnlyList<ComparisonLine> Summary { get; init; }
    public required IReadOnlyList<ComparisonLine> Products { get; init; }
    public required IReadOnlyList<ComparisonLine> Charges { get; init; }
    public required IReadOnlyList<ComparisonLine> Operations { get; init; }

    public const string ProductsName = "Produits";
    public const string ChargesName = "Charges";
    public const string ResultName = "Résultat";
    public const string CashName = "Trésorerie à la clôture";
    public const string AssetsName = "Immobilisations (valeur nette)";
    public const string DepreciationName = "Amortissements et sorties d'immobilisations";

    /// <param name="bilans">Bilans des exercices, du plus ancien au plus récent.</param>
    public static YearComparison Build(IReadOnlyList<Bilan> bilans)
    {
        var n = bilans.Count;
        long[] Zeros() => new long[n];

        // Regroupe par nom (sans tenir compte de la casse) ; l'ordre est celui du plus récent exercice, puis les noms
        // disparus ou apparus plus tôt à la suite.
        List<ComparisonLine> Group(Func<Bilan, IEnumerable<(string Name, long Value)>> pick)
        {
            var order = new List<string>();
            var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
            for (var i = n - 1; i >= 0; i--)
                foreach (var (name, value) in pick(bilans[i]))
                {
                    if (!values.TryGetValue(name, out var arr))
                    {
                        arr = Zeros();
                        values[name] = arr;
                        display[name] = name;
                        order.Add(name);
                    }
                    arr[i] += value;
                }
            return order.Select(k => new ComparisonLine(display[k], values[k])).ToList();
        }

        var products = Group(b => b.Products.Select(p => (p.Name, p.Total)));
        var charges = Group(b => b.Charges.Select(c => (c.Name, c.Total)));
        // Immobilisations : dotations et sorties, nettes des achats portés à l'actif (comme dans le résultat du bilan).
        var fixedAssets = bilans.Select(b => b.AssetsDepreciation + b.AssetsDisposals - b.AssetsReclass).ToArray();
        if (fixedAssets.Any(v => v != 0)) charges.Add(new ComparisonLine(DepreciationName, fixedAssets));

        var operations = Group(b => b.Operations.Select(o => (o.Operation.Name, o.Result)));

        var summary = new List<ComparisonLine>
        {
            new(ProductsName, bilans.Select(b => b.ProductsTotal).ToArray()),
            new(ChargesName, bilans.Select(b => b.ChargesTotal).ToArray()),
            new(ResultName, bilans.Select(b => b.Result).ToArray()),
            new(CashName, bilans.Select(b => b.ClosingTotal).ToArray())
        };
        if (bilans.Any(b => b.AssetsNetClosing != 0))
            summary.Add(new ComparisonLine(AssetsName, bilans.Select(b => b.AssetsNetClosing).ToArray()));

        return new YearComparison
        {
            Years = bilans.Select(b => b.Year).ToList(),
            Summary = summary,
            Products = products.Where(l => !l.IsZero).ToList(),
            Charges = charges.Where(l => !l.IsZero).ToList(),
            Operations = operations.Where(l => !l.IsZero).ToList()
        };
    }
}

public sealed partial class Compta
{
    /// <summary>Exercices à comparer : l'exercice <paramref name="baseYearId"/> et les <paramref name="count"/> − 1 précédents
    /// (dans l'ordre des dates), du plus ancien au plus récent.</summary>
    public YearComparison CompareYears(long baseYearId, int count)
    {
        var all = Years().OrderBy(y => y.Start).ToList();
        var idx = all.FindIndex(y => y.Id == baseYearId);
        if (idx < 0) throw new ComptaException("Exercice introuvable.");
        var take = Math.Clamp(count, 1, 20);
        var selected = all.Skip(Math.Max(0, idx - take + 1)).Take(Math.Min(take, idx + 1)).ToList();
        return YearComparison.Build(selected.Select(y => ComputeBilan(y.Id)).ToList());
    }
}
