using System.Globalization;
using System.Text.Json;
using ToutCompteFait.Core.Business;
using ToutCompteFait.Core.Security;

namespace ToutCompteFait.Core;

/// <summary>Résultat d'une mise à jour du barème : ce qui a changé, ce qui a été conservé parce que l'utilisateur l'avait modifié.</summary>
public sealed record BaremeResult(bool Applied, string Label, IReadOnlyList<string> Changed, IReadOnlyList<string> KeptCustom, string Message);

public sealed partial class Compta
{
    private const string BaremeLabelKey = "bareme.label", BaremeFromKey = "bareme.from", BaremeHistoryKey = "bareme.history",
        BaremeAutoKey = "bareme.auto", BaremeCheckedKey = "bareme.checked";

    private sealed class SnapshotDto
    {
        public string From { get; set; } = "";
        public string Until { get; set; } = "";
        public Dictionary<string, int> Rates { get; set; } = new();
        public Dictionary<string, int> Vfl { get; set; } = new();
    }

    private static IReadOnlyList<RateSnapshot> ReadRateHistory(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue(BaremeHistoryKey, out var json) || string.IsNullOrWhiteSpace(json)) return Array.Empty<RateSnapshot>();
        try
        {
            Dictionary<UrssafKind, int> Map(Dictionary<string, int> d) =>
                UrssafKinds.All.ToDictionary(k => k, k => d.GetValueOrDefault(k.Code()));
            return (JsonSerializer.Deserialize<List<SnapshotDto>>(json) ?? new())
                .Select(d => new RateSnapshot(DateOnly.ParseExact(d.From, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DateOnly.ParseExact(d.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture), Map(d.Rates), Map(d.Vfl)))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return Array.Empty<RateSnapshot>();   // historique illisible : on garde les taux actuels
        }
    }

    private static string WriteRateHistory(IEnumerable<RateSnapshot> history)
    {
        Dictionary<string, int> Map(IReadOnlyDictionary<UrssafKind, int> d) => d.ToDictionary(kv => kv.Key.Code(), kv => kv.Value);
        return JsonSerializer.Serialize(history.Select(s => new SnapshotDto
        {
            From = s.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Until = s.Until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Rates = Map(s.RatesBp), Vfl = Map(s.VflRatesBp)
        }).ToList());
    }

    /// <summary>Révision du barème actuellement appliquée (celle livrée avec le logiciel tant qu'aucune mise à jour n'a eu lieu).</summary>
    public (string Label, DateOnly From) BaremeApplied()
    {
        using var l = Open();
        var s = ReadBusinessSettings(l.Ctx);
        var from = DateOnly.TryParseExact(s.GetValueOrDefault(BaremeFromKey), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : Bareme.Builtin.From;
        return (s.TryGetValue(BaremeLabelKey, out var label) && label.Length > 0 ? label : Bareme.Builtin.Label, from);
    }

    /// <summary>Mise à jour automatique du barème au démarrage : activée par défaut.</summary>
    public bool BaremeAutoEnabled()
    {
        using var l = Open();
        return ReadBusinessSettings(l.Ctx).GetValueOrDefault(BaremeAutoKey) != "0";
    }

    public void SetBaremeAuto(bool on)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        using var tx = Begin();
        var ctx = Open().Ctx;
        WriteBusinessSettings(ctx, new Dictionary<string, string?> { [BaremeAutoKey] = on ? null : "0" });
        Save(ctx);
        Log(ctx, "Barème : mise à jour automatique", on ? "activée" : "désactivée");
        tx.Commit();
    }

    public DateTime? BaremeLastCheck()
    {
        using var l = Open();
        return DateTime.TryParse(ReadBusinessSettings(l.Ctx).GetValueOrDefault(BaremeCheckedKey), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>Note la date de la dernière vérification (silencieux si la base est en lecture seule).</summary>
    public void MarkBaremeChecked(DateTime when)
    {
        if (_factory.ReadOnly) return;
        try
        {
            using var l = Open();
            var ctx = l.Ctx;
            WriteBusinessSettings(ctx, new Dictionary<string, string?> { [BaremeCheckedKey] = when.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) });
            Save(ctx);
        }
        catch
        {
            // repère informatif seulement
        }
    }

    /// <summary>Applique la dernière révision en vigueur (<paramref name="today"/>) du fichier de barème. Seules les valeurs qui
    /// portent encore celles de la révision précédente (ou d'un ancien barème par défaut) sont remplacées : une valeur modifiée à
    /// la main est conservée. Les taux remplacés sont gardés dans l'historique, pour que les périodes passées gardent leur taux.</summary>
    public BaremeResult ApplyBareme(IReadOnlyList<BaremeEntry> entries, DateOnly today)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        var target = entries.Where(e => e.From <= today).OrderByDescending(e => e.From).FirstOrDefault();
        var applied = BaremeApplied();
        if (target is null) return new BaremeResult(false, applied.Label, Array.Empty<string>(), Array.Empty<string>(), "Aucune révision du barème n'est encore en vigueur.");
        if (target.From <= applied.From || target.Label == applied.Label)
            return new BaremeResult(false, applied.Label, Array.Empty<string>(), Array.Empty<string>(), $"Barème à jour ({applied.Label}).");

        var previous = entries.FirstOrDefault(e => e.Label == applied.Label) ?? Bareme.Builtin;
        var cur = GetUrssafParams();
        var legacy = UrssafParams.Legacy;
        var changed = new List<string>();
        var kept = new List<string>();

        bool Same(long value, long prev, long old) => value == prev || value == old;
        void Note(string label, string from, string to, bool replace, bool differs)
        {
            if (!differs) return;
            if (replace) changed.Add($"{label} : {from} → {to}");
            else kept.Add($"{label} : {from} conservé (valeur personnalisée), le barème propose {to}");
        }
        string Pct(int bp) => $"{bp / 100m:0.##} %";
        string Eur(long cents) => $"{cents / 100m:N0} €";

        var rates = new Dictionary<UrssafKind, int>();
        var vfl = new Dictionary<UrssafKind, int>();
        foreach (var k in UrssafKinds.All)
        {
            var r = cur.RatesBp.GetValueOrDefault(k);
            var rep = Same(r, previous.RatesBp[k], legacy.RatesBp[k]);
            rates[k] = rep ? target.RatesBp[k] : r;
            Note("Cotisations " + k.Short(), Pct(r), Pct(target.RatesBp[k]), rep, r != target.RatesBp[k]);
            var v = cur.VflRatesBp.GetValueOrDefault(k);
            var repV = v == previous.VflRatesBp[k];
            vfl[k] = repV ? target.VflRatesBp[k] : v;
            Note("Impôt (versement libératoire) " + k.Short(), Pct(v), Pct(target.VflRatesBp[k]), repV, v != target.VflRatesBp[k]);
        }
        long Pick(string label, long value, long prev, long old, long next)
        {
            var rep = Same(value, prev, old);
            Note(label, Eur(value), Eur(next), rep, value != next);
            return rep ? next : value;
        }
        var next2 = cur with
        {
            RatesBp = rates,
            VflRatesBp = vfl,
            CeilingTotalCents = Pick("Plafond de chiffre d'affaires", cur.CeilingTotalCents, previous.CeilingTotalCents, legacy.CeilingTotalCents, target.CeilingTotalCents),
            CeilingServicesCents = Pick("Plafond des prestations de services", cur.CeilingServicesCents, previous.CeilingServicesCents, legacy.CeilingServicesCents, target.CeilingServicesCents),
            VatSaleCents = Pick("Franchise de TVA (vente)", cur.VatSaleCents, previous.VatSaleCents, legacy.VatSaleCents, target.VatSaleCents),
            VatSaleMaxCents = Pick("Franchise de TVA majorée (vente)", cur.VatSaleMaxCents, previous.VatSaleMaxCents, legacy.VatSaleMaxCents, target.VatSaleMaxCents),
            VatServiceCents = Pick("Franchise de TVA (prestations)", cur.VatServiceCents, previous.VatServiceCents, legacy.VatServiceCents, target.VatServiceCents),
            VatServiceMaxCents = Pick("Franchise de TVA majorée (prestations)", cur.VatServiceMaxCents, previous.VatServiceMaxCents, legacy.VatServiceMaxCents, target.VatServiceMaxCents),
            AcreDiscountPercent = cur.AcreDiscountPercent == previous.AcreDiscountPercent ? target.AcreDiscountPercent : cur.AcreDiscountPercent
        };
        if (next2.AcreDiscountPercent != cur.AcreDiscountPercent)
            changed.Add($"Réduction ACRE : {cur.AcreDiscountPercent} % → {next2.AcreDiscountPercent} %");

        // Les taux remplacés restent valables pour les périodes qui se terminent avant la date d'effet de la nouvelle révision.
        var history = cur.History.ToList();
        var ratesChanged = UrssafKinds.All.Any(k => rates[k] != cur.RatesBp.GetValueOrDefault(k) || vfl[k] != cur.VflRatesBp.GetValueOrDefault(k));
        if (ratesChanged)
            history.Add(new RateSnapshot(history.Count > 0 ? history[^1].Until : DateOnly.MinValue, target.From, cur.RatesBp, cur.VflRatesBp));

        using var tx = Begin();
        SaveUrssafParams(next2);
        var ctx = Open().Ctx;
        WriteBusinessSettings(ctx, new Dictionary<string, string?>
        {
            [BaremeLabelKey] = target.Label,
            [BaremeFromKey] = target.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [BaremeHistoryKey] = history.Count > 0 ? WriteRateHistory(history) : null
        });
        Save(ctx);
        Log(ctx, "Barème mis à jour automatiquement",
            $"{applied.Label} → {target.Label}" + (changed.Count > 0 ? "\n" + string.Join("\n", changed) : " (aucune valeur modifiée)") +
            (kept.Count > 0 ? "\nConservé :\n" + string.Join("\n", kept) : ""));
        tx.Commit();
        return new BaremeResult(true, target.Label, changed, kept,
            $"Barème {target.Label} appliqué" + (changed.Count == 0 ? " : aucune de vos valeurs n'avait à changer." : "."));
    }
}