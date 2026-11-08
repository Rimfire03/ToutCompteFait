using System.Globalization;
using System.Text.Json;

namespace ToutCompteFait.Core.Business;

/// <summary>Une révision du barème URSSAF / impôt : taux de cotisations, taux du versement libératoire, plafonds du régime micro,
/// franchise de TVA et réduction ACRE, en vigueur à partir d'une date.</summary>
public sealed record BaremeEntry(string Label, DateOnly From,
    IReadOnlyDictionary<UrssafKind, int> RatesBp, IReadOnlyDictionary<UrssafKind, int> VflRatesBp,
    long CeilingTotalCents, long CeilingServicesCents,
    long VatSaleCents, long VatSaleMaxCents, long VatServiceCents, long VatServiceMaxCents, int AcreDiscountPercent);

/// <summary>Barème publié dans le dépôt du logiciel (<c>bareme/urssaf.json</c>) et lu au démarrage pour mettre à jour les taux et
/// plafonds sans nouvelle version du logiciel. Format : <c>{ "schema": 1, "entries": [ { "label", "from", "rates", "vfl",
/// "ceilings", "vat", "acreDiscount" } ] }</c> (taux en centièmes de pour cent, montants en centimes, types par leur code).</summary>
public static class Bareme
{
    public const string Url = "https://raw.githubusercontent.com/Rimfire03/ToutCompteFait/main/bareme/urssaf.json";

    /// <summary>Barème livré avec le logiciel : valeurs par défaut de <see cref="UrssafParams"/>.</summary>
    public static BaremeEntry Builtin { get; } = FromParams("2026", new DateOnly(2026, 1, 1), UrssafParams.Reference);

    public static BaremeEntry FromParams(string label, DateOnly from, UrssafParams p) => new(label, from,
        p.RatesBp, p.VflRatesBp, p.CeilingTotalCents, p.CeilingServicesCents,
        p.VatSaleCents, p.VatSaleMaxCents, p.VatServiceCents, p.VatServiceMaxCents, p.AcreDiscountPercent);

    /// <summary>Lit et contrôle le fichier ; renvoie les révisions classées par date. Lève <see cref="FormatException"/> si le
    /// fichier est invalide (une valeur hors bornes ne doit jamais modifier les réglages).</summary>
    public static IReadOnlyList<BaremeEntry> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("schema", out var schema) || schema.GetInt32() != 1)
                throw new FormatException("Version du fichier de barème non gérée.");
            var list = new List<BaremeEntry>();
            foreach (var e in root.GetProperty("entries").EnumerateArray())
            {
                var label = e.GetProperty("label").GetString();
                if (string.IsNullOrWhiteSpace(label)) throw new FormatException("Révision sans nom.");
                if (!DateOnly.TryParseExact(e.GetProperty("from").GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
                    throw new FormatException($"Date invalide pour « {label} ».");
                var ceil = e.GetProperty("ceilings");
                var vat = e.GetProperty("vat");
                var entry = new BaremeEntry(label, from, Rates(e.GetProperty("rates"), label), Rates(e.GetProperty("vfl"), label),
                    Money(ceil, "total"), Money(ceil, "services"),
                    Money(vat, "sale"), Money(vat, "saleMax"), Money(vat, "service"), Money(vat, "serviceMax"),
                    e.GetProperty("acreDiscount").GetInt32());
                if (entry.AcreDiscountPercent is < 0 or > 100) throw new FormatException($"Réduction ACRE invalide pour « {label} ».");
                list.Add(entry);
            }
            if (list.Count == 0) throw new FormatException("Aucune révision dans le fichier.");
            var sorted = list.OrderBy(x => x.From).ToList();
            if (sorted.Select(x => x.From).Distinct().Count() != sorted.Count || sorted.Select(x => x.Label).Distinct().Count() != sorted.Count)
                throw new FormatException("Deux révisions ont la même date ou le même nom.");
            return sorted;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw ex as FormatException ?? new FormatException("Fichier de barème illisible : " + ex.Message, ex);
        }
    }

    private static long Money(JsonElement parent, string name)
    {
        var v = parent.GetProperty(name).GetInt64();
        if (v <= 0 || v > 100_000_000_000) throw new FormatException($"Montant invalide : {name}.");
        return v;
    }

    private static IReadOnlyDictionary<UrssafKind, int> Rates(JsonElement obj, string label)
    {
        var map = new Dictionary<UrssafKind, int>();
        foreach (var k in UrssafKinds.All)
        {
            var rate = obj.GetProperty(k.Code()).GetInt32();
            if (rate is < 0 or > 10000) throw new FormatException($"Taux invalide ({k.Code()}) pour « {label} ».");
            map[k] = rate;
        }
        return map;
    }
}