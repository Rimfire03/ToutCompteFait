using System.Text.RegularExpressions;

namespace ToutCompteFait.Core;

/// <summary>Ventilation proposée pour une ligne du relevé : même libellé, toujours classé dans la même catégorie.</summary>
public sealed record BankSuggestion(int LineIndex, long CategoryId, string CategoryName, CategoryKind Kind, int Occurrences);

public sealed partial class Compta
{
    /// <summary>Nombre minimal d'écritures passées de même libellé, toutes classées dans la même catégorie, pour proposer.</summary>
    public const int SuggestionMinOccurrences = 2;

    private static readonly Regex LabelNoise = new(@"\d+|[^\p{L}]+", RegexOptions.CultureInvariant);

    /// <summary>Clé de comparaison d'un libellé bancaire : sans moyen de paiement, chiffres ni ponctuation (dates,
    /// références, numéros de carte), en minuscules.</summary>
    public static string LabelKey(string? label)
    {
        var clean = PaymentLabel.Split(label).Clean;
        var key = LabelNoise.Replace(clean.ToLowerInvariant(), " ").Trim();
        return Regex.Replace(key, @"\s+", " ");
    }

    /// <summary>Pour chaque ligne <see cref="BankLineStatus.New"/> du plan, cherche dans l'historique (tous exercices) les
    /// écritures de même libellé et de même sens, entièrement classées dans une seule catégorie : si au moins
    /// <see cref="SuggestionMinOccurrences"/> écritures existent et qu'<b>aucune</b> n'a été classée ailleurs, la
    /// catégorie de l'exercice de la ligne est proposée. Rien n'est enregistré : l'utilisateur valide.</summary>
    public IReadOnlyDictionary<int, BankSuggestion> SuggestBankCategories(BankImportPlan plan)
    {
        var result = new Dictionary<int, BankSuggestion>();
        var wanted = new HashSet<string>();
        for (var i = 0; i < plan.Lines.Count; i++)
            if (plan.Lines[i].Status is BankLineStatus.New or BankLineStatus.ToConfirm && LabelKey(plan.Lines[i].Line.Label) is { Length: >= 3 } k)
                wanted.Add(k);
        if (wanted.Count == 0) return result;

        // (clé, sens) → catégories distinctes rencontrées (kind + nom) et nombre d'écritures.
        var history = new Dictionary<(string Key, bool In), (HashSet<(CategoryKind, string)> Cats, int Count, bool Mixed)>();
        foreach (var y in Years())
        {
            var names = Categories(y.Id).ToDictionary(c => c.Id);
            foreach (var r in Entries(y.Id))
            {
                var e = r.Entry;
                if (e.Kind != EntryKind.Normal || e.Frozen || e.RefundOfId is not null) continue;
                var key = LabelKey(e.Label);
                if (!wanted.Contains(key)) continue;
                var k = (key, e.Direction == Direction.In);
                if (!history.TryGetValue(k, out var h)) h = (new HashSet<(CategoryKind, string)>(), 0, false);
                // Une écriture non ventilée ou ventilée sur plusieurs catégories ne prouve rien : elle ne vote pas,
                // mais elle empêche la proposition si elle est ventilée autrement (plusieurs catégories).
                if (r.Allocations.Count == 0) { history[k] = h; continue; }
                var cats = r.Allocations.Select(a => names.TryGetValue(a.CategoryId, out var c) ? (c.Kind, c.Name) : default).Distinct().ToList();
                if (cats.Count != 1 || cats[0].Item2 is null) { history[k] = (h.Cats, h.Count, true); continue; }
                h.Cats.Add(cats[0]);
                history[k] = (h.Cats, h.Count + 1, h.Mixed);
            }
        }

        var yearCats = new Dictionary<long, List<Category>>();
        for (var i = 0; i < plan.Lines.Count; i++)
        {
            var p = plan.Lines[i];
            if (p.Status is not (BankLineStatus.New or BankLineStatus.ToConfirm) || p.YearId is not { } yid) continue;
            var isIn = p.Line.AmountCents >= 0;
            if (!history.TryGetValue((LabelKey(p.Line.Label), isIn), out var h)) continue;
            if (h.Mixed || h.Count < SuggestionMinOccurrences || h.Cats.Count != 1) continue;
            var (kind, name) = h.Cats.First();
            if (kind != (isIn ? CategoryKind.Product : CategoryKind.Charge)) continue;
            if (!yearCats.TryGetValue(yid, out var cats)) yearCats[yid] = cats = Categories(yid);
            var cat = cats.FirstOrDefault(c => c.Kind == kind && string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (cat is null) continue;
            result[i] = new BankSuggestion(i, cat.Id, cat.Name, kind, h.Count);
        }
        return result;
    }
}
