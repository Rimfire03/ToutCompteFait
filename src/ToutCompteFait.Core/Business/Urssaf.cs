using System.Globalization;

namespace ToutCompteFait.Core.Business;

/// <summary>Catégorie de déclaration du chiffre d'affaires d'un auto-entrepreneur (micro-entrepreneur) à l'URSSAF.</summary>
public enum UrssafKind
{
    /// <summary>BIC : vente de marchandises, objets, fournitures, denrées, fourniture de logement.</summary>
    Sale,
    /// <summary>BIC : prestations de services commerciales ou artisanales.</summary>
    Service,
    /// <summary>BNC : professions libérales relevant de la SSI.</summary>
    LiberalSsi,
    /// <summary>BNC : professions libérales réglementées relevant de la CIPAV.</summary>
    LiberalCipav
}

public static class UrssafKinds
{
    public static readonly UrssafKind[] All = { UrssafKind.Sale, UrssafKind.Service, UrssafKind.LiberalSsi, UrssafKind.LiberalCipav };

    /// <summary>Code stocké en base.</summary>
    public static string Code(this UrssafKind k) => k switch
    {
        UrssafKind.Sale => "BIC_VENTE",
        UrssafKind.Service => "BIC_SERVICE",
        UrssafKind.LiberalSsi => "BNC_SSI",
        _ => "BNC_CIPAV"
    };

    public static UrssafKind? FromCode(string? code) => code switch
    {
        "BIC_VENTE" => UrssafKind.Sale,
        "BIC_SERVICE" => UrssafKind.Service,
        "BNC_SSI" => UrssafKind.LiberalSsi,
        "BNC_CIPAV" => UrssafKind.LiberalCipav,
        _ => null
    };

    /// <summary>Début du nom d'une catégorie de produit de ce type (ex. « BIC vente », puis éventuellement « – nom »).</summary>
    public static string Short(this UrssafKind k) => k switch
    {
        UrssafKind.Sale => "BIC vente",
        UrssafKind.Service => "BIC prestation",
        UrssafKind.LiberalSsi => "BNC",
        _ => "BNC CIPAV"
    };

    /// <summary>Libellé complet pour les listes de choix.</summary>
    public static string Label(this UrssafKind k) => k switch
    {
        UrssafKind.Sale => "BIC · Vente de marchandises, denrées, fourniture de logement",
        UrssafKind.Service => "BIC · Prestations de services commerciales ou artisanales",
        UrssafKind.LiberalSsi => "BNC · Professions libérales (SSI)",
        _ => "BNC · Professions libérales réglementées (CIPAV)"
    };

    /// <summary>Intitulé de la ligne sur le site de déclaration (autoentrepreneur.urssaf.fr) — à vérifier : le site
    /// peut reformuler ses libellés.</summary>
    public static string FormLabel(this UrssafKind k) => k switch
    {
        UrssafKind.Sale => "Vente de marchandises (BIC)",
        UrssafKind.Service => "Prestations de services commerciales ou artisanales (BIC)",
        UrssafKind.LiberalSsi => "Activités libérales relevant de la SSI (BNC)",
        _ => "Activités libérales relevant de la CIPAV (BNC)"
    };

    /// <summary>Activités de prestation (plafond de chiffre d'affaires et seuil de TVA des services).</summary>
    public static bool IsService(this UrssafKind k) => k != UrssafKind.Sale;

    /// <summary>Type d'une catégorie d'après son nom (« BIC vente – Boutique » → Sale), sinon null.</summary>
    public static UrssafKind? FromCategoryName(string name)
    {
        var n = name.Trim();
        // Du plus long préfixe au plus court (« BNC CIPAV » avant « BNC »).
        foreach (var k in new[] { UrssafKind.LiberalCipav, UrssafKind.Service, UrssafKind.Sale, UrssafKind.LiberalSsi })
        {
            var s = k.Short();
            if (n.Equals(s, StringComparison.OrdinalIgnoreCase) || n.StartsWith(s + " ", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith(s + "–", StringComparison.OrdinalIgnoreCase))
                return k;
        }
        return null;
    }

    /// <summary>Nom d'une catégorie : « BIC vente » ou « BIC vente – Boutique en ligne ».</summary>
    public static string CategoryName(UrssafKind kind, string? customName)
    {
        var c = (customName ?? "").Trim();
        return c.Length == 0 ? kind.Short() : $"{kind.Short()} – {c}";
    }
}

public enum DeclarationMode { Quarterly, Monthly }

/// <summary>Période de déclaration : un mois ou un trimestre civil.</summary>
public sealed record UrssafPeriod(string Key, DateOnly Start, DateOnly End, DateOnly DueDate, string Label)
{
    public static UrssafPeriod ForDate(DateOnly date, DeclarationMode mode) => mode == DeclarationMode.Monthly
        ? Monthly(date.Year, date.Month)
        : Quarterly(date.Year, (date.Month - 1) / 3 + 1);

    public static UrssafPeriod Monthly(int year, int month)
    {
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        // Échéance : dernier jour du mois suivant.
        var due = start.AddMonths(2).AddDays(-1);
        var label = start.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("fr-FR"));
        return new UrssafPeriod($"{year:0000}-{month:00}", start, end, due, char.ToUpperInvariant(label[0]) + label[1..]);
    }

    public static UrssafPeriod Quarterly(int year, int quarter)
    {
        var start = new DateOnly(year, (quarter - 1) * 3 + 1, 1);
        var end = start.AddMonths(3).AddDays(-1);
        // Échéances : 30 avril, 31 juillet, 31 octobre, 31 janvier.
        var due = quarter switch
        {
            1 => new DateOnly(year, 4, 30),
            2 => new DateOnly(year, 7, 31),
            3 => new DateOnly(year, 10, 31),
            _ => new DateOnly(year + 1, 1, 31)
        };
        return new UrssafPeriod($"{year:0000}-T{quarter}", start, end, due, $"{quarter}{(quarter == 1 ? "er" : "e")} trimestre {year}");
    }

    /// <summary>Périodes d'une année civile.</summary>
    public static IReadOnlyList<UrssafPeriod> OfYear(int year, DeclarationMode mode) => mode == DeclarationMode.Monthly
        ? Enumerable.Range(1, 12).Select(m => Monthly(year, m)).ToList()
        : Enumerable.Range(1, 4).Select(q => Quarterly(year, q)).ToList();
}

/// <summary>Taux de cotisations estimés, plafonds et seuils (modifiables : ces valeurs sont révisées par l'État ; celles
/// par défaut sont indicatives et à vérifier avant usage).</summary>
public sealed record UrssafParams
{
    /// <summary>Taux de cotisations par type, en centièmes de pour cent (1230 = 12,30 %).</summary>
    public IReadOnlyDictionary<UrssafKind, int> RatesBp { get; init; } = new Dictionary<UrssafKind, int>
    {
        [UrssafKind.Sale] = 1230,
        [UrssafKind.Service] = 2120,
        [UrssafKind.LiberalSsi] = 2460,
        [UrssafKind.LiberalCipav] = 2320
    };

    /// <summary>Plafond de chiffre d'affaires du régime micro (activités de vente + prestations) et sous-plafond des
    /// prestations de services, en centimes.</summary>
    public long CeilingTotalCents { get; init; } = 18_870_000;
    public long CeilingServicesCents { get; init; } = 7_770_000;

    /// <summary>Franchise en base de TVA : seuil et seuil majoré (ventes / prestations), en centimes.</summary>
    public long VatSaleCents { get; init; } = 9_100_000;
    public long VatSaleMaxCents { get; init; } = 10_100_000;
    public long VatServiceCents { get; init; } = 3_680_000;
    public long VatServiceMaxCents { get; init; } = 3_910_000;

    /// <summary>ACRE : réduction des cotisations (en %) jusqu'à la date indiquée (inclus).</summary>
    public int AcreDiscountPercent { get; init; } = 50;
    public DateOnly? AcreUntil { get; init; }

    public int RateFor(UrssafKind k, DateOnly periodEnd)
    {
        var rate = RatesBp.TryGetValue(k, out var r) ? r : 0;
        if (AcreUntil is { } until && periodEnd <= until)
            rate = (int)Math.Round(rate * (100 - AcreDiscountPercent) / 100.0, MidpointRounding.AwayFromZero);
        return rate;
    }
}

public sealed record BusinessProfile
{
    public string? Siren { get; init; }
    public string? Siret { get; init; }
    public string? Name { get; init; }
    public string? Address { get; init; }
    public string? PostalCode { get; init; }
    public string? City { get; init; }
    public DateOnly? CreationDate { get; init; }
    public string? LegalNature { get; init; }
    /// <summary>Numéro RNA d'une association (W + 9 chiffres), fourni par le registre quand il existe.</summary>
    public string? Rna { get; init; }
    /// <summary>Code APE (NAF rév. 2) principal, tel que renvoyé par la source (ex. « 62.02A »).</summary>
    public string? ApeMain { get; init; }
    /// <summary>Tous les codes APE de l'activité (principal compris), saisis ou récupérés.</summary>
    public IReadOnlyList<string> ApeCodes { get; init; } = Array.Empty<string>();
    public DeclarationMode Mode { get; init; } = DeclarationMode.Quarterly;
    public DateTime? FetchedAt { get; init; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Siren) && string.IsNullOrWhiteSpace(Name);
}

public sealed record UrssafLine(UrssafKind Kind, long TurnoverCents, int RateBp, long ContributionCents);

public sealed record UrssafReport(UrssafPeriod Period, IReadOnlyList<UrssafLine> Lines, long TotalTurnoverCents,
    long TotalContributionCents, long UnclassifiedCents, int PendingEntries, DateTime? DeclaredAt, string? DeclaredBy);

/// <summary>Cumul de l'année civile face aux plafonds et au seuil de TVA.</summary>
public sealed record UrssafCeilings(int Year, long TotalCents, long ServicesCents, long SalesCents, UrssafParams Params)
{
    public double TotalRatio => Params.CeilingTotalCents == 0 ? 0 : (double)TotalCents / Params.CeilingTotalCents;
    public double ServicesRatio => Params.CeilingServicesCents == 0 ? 0 : (double)ServicesCents / Params.CeilingServicesCents;
    public bool VatSaleExceeded => SalesCents > Params.VatSaleCents;
    public bool VatServiceExceeded => ServicesCents > Params.VatServiceCents;
}

public sealed record UrssafDeclaration(string PeriodKey, DateOnly Start, DateOnly End, DateTime DeclaredAt, string? DeclaredBy,
    long TurnoverCents, string? Note);