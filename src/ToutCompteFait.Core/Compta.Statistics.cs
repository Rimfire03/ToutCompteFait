namespace ToutCompteFait.Core;

/// <summary>Statistique mensuelle d'un exercice : produits et charges (montants de trésorerie ventilés, par mois de la
/// date de l'écriture), et résultat par opération. Les produits personnels (apports) sont exclus, comme dans le résultat.</summary>
public sealed class MonthlyStatistics
{
    /// <summary>Premier jour de chaque mois couvert par l'exercice, dans l'ordre.</summary>
    public required IReadOnlyList<DateOnly> Months { get; init; }

    public required IReadOnlyList<ComparisonLine> Products { get; init; }
    public required IReadOnlyList<ComparisonLine> Charges { get; init; }
    /// <summary>Résultat (produits − charges) par opération.</summary>
    public required IReadOnlyList<ComparisonLine> Operations { get; init; }
    public required IReadOnlyList<ComparisonLine> OperationProducts { get; init; }
    public required IReadOnlyList<ComparisonLine> OperationCharges { get; init; }
}

public sealed partial class Compta
{
    public MonthlyStatistics ComputeMonthlyStatistics(long yearId)
    {
        var year = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        var months = new List<DateOnly>();
        for (var m = new DateOnly(year.Start.Year, year.Start.Month, 1); m <= year.End; m = m.AddMonths(1)) months.Add(m);
        var n = months.Count;
        int Index(DateOnly d)
        {
            var i = (d.Year - year.Start.Year) * 12 + d.Month - year.Start.Month;
            return Math.Clamp(i, 0, n - 1);
        }

        var cats = Categories(yearId);
        var catById = cats.ToDictionary(c => c.Id);
        var personalCharges = PersonalCategoryNames();
        var personalProducts = PersonalProductNames();
        var perCat = cats.ToDictionary(c => c.Id, _ => new long[n]);
        foreach (var r in Entries(yearId))
        {
            var i = Index(r.Entry.Date);
            foreach (var al in r.Allocations)
                if (perCat.TryGetValue(al.CategoryId, out var arr)) arr[i] += al.AmountCents;
        }

        bool IsPersonal(Category c) => c.Kind == CategoryKind.Product ? personalProducts.Contains(c.Name) : personalCharges.Contains(c.Name);
        List<ComparisonLine> Lines(CategoryKind kind, bool operatingOnly) =>
            cats.Where(c => c.Kind == kind && !(operatingOnly && IsPersonal(c)))
                .OrderBy(c => c.Position).ThenBy(c => c.Id)
                .Select(c => new ComparisonLine(c.Name, perCat[c.Id]))
                .Where(l => !l.IsZero).ToList();

        var opProducts = new List<ComparisonLine>();
        var opCharges = new List<ComparisonLine>();
        var opResults = new List<ComparisonLine>();
        foreach (var op in Operations(yearId))
        {
            var p = new long[n];
            var c = new long[n];
            foreach (var cat in cats.Where(x => x.OperationId == op.Id && !IsPersonal(x)))
            {
                var target = cat.Kind == CategoryKind.Product ? p : c;
                for (var i = 0; i < n; i++) target[i] += perCat[cat.Id][i];
            }
            if (p.All(v => v == 0) && c.All(v => v == 0)) continue;
            opProducts.Add(new ComparisonLine(op.Name, p));
            opCharges.Add(new ComparisonLine(op.Name, c));
            opResults.Add(new ComparisonLine(op.Name, p.Zip(c, (a, b) => a - b).ToArray()));
        }

        return new MonthlyStatistics
        {
            Months = months,
            Products = Lines(CategoryKind.Product, true),
            Charges = Lines(CategoryKind.Charge, true),
            Operations = opResults,
            OperationProducts = opProducts,
            OperationCharges = opCharges
        };
    }
}
