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
    /// <summary>Millésime des valeurs par défaut ci-dessous (affiché dans les réglages). À mettre à jour, avec les valeurs
    /// et <see cref="Legacy"/>, à chaque révision officielle (plafonds : tous les 3 ans ; taux : chaque 1er janvier).</summary>
    public const string ReferenceLabel =
        "barème 2026 : plafonds du régime micro 2026-2028, franchise de TVA en vigueur en 2026, taux de cotisations au 1er janvier 2026, " +
        "ACRE à 25 % (micro-entrepreneurs, depuis le 1er juillet 2026)";

    /// <summary>Taux de cotisations par type, en centièmes de pour cent (1230 = 12,30 %).</summary>
    public IReadOnlyDictionary<UrssafKind, int> RatesBp { get; init; } = new Dictionary<UrssafKind, int>
    {
        [UrssafKind.Sale] = 1230,
        [UrssafKind.Service] = 2120,
        [UrssafKind.LiberalSsi] = 2560,
        [UrssafKind.LiberalCipav] = 2320
    };

    /// <summary>Plafond de chiffre d'affaires du régime micro (activités de vente + prestations) et sous-plafond des
    /// prestations de services, en centimes (2026-2028).</summary>
    public long CeilingTotalCents { get; init; } = 20_310_000;
    public long CeilingServicesCents { get; init; } = 8_360_000;

    /// <summary>Franchise en base de TVA : seuil et seuil majoré (ventes / prestations), en centimes.</summary>
    public long VatSaleCents { get; init; } = 8_500_000;
    public long VatSaleMaxCents { get; init; } = 9_350_000;
    public long VatServiceCents { get; init; } = 3_750_000;
    public long VatServiceMaxCents { get; init; } = 4_125_000;

    /// <summary>ACRE : réduction des cotisations (en %) jusqu'à la date indiquée (inclus). 25 % pour une création depuis
    /// le 1er juillet 2026 ; 50 % pour les créations antérieures (à saisir à la main).</summary>
    public int AcreDiscountPercent { get; init; } = 25;
    public DateOnly? AcreUntil { get; init; }

    /// <summary>Valeurs par défaut des versions précédentes (2023-2025), reconnues pour proposer la mise à jour.</summary>
    public static UrssafParams Legacy { get; } = new()
    {
        RatesBp = new Dictionary<UrssafKind, int>
        {
            [UrssafKind.Sale] = 1230, [UrssafKind.Service] = 2120, [UrssafKind.LiberalSsi] = 2460, [UrssafKind.LiberalCipav] = 2320
        },
        CeilingTotalCents = 18_870_000, CeilingServicesCents = 7_770_000,
        VatSaleCents = 9_100_000, VatSaleMaxCents = 10_100_000, VatServiceCents = 3_680_000, VatServiceMaxCents = 3_910_000,
        AcreDiscountPercent = 50
    };

    /// <summary>Valeurs de référence actuelles (celles d'un <c>new UrssafParams()</c>).</summary>
    public static UrssafParams Reference { get; } = new();

    /// <summary>Réglages qui portent encore une ancienne valeur par défaut (2023-2025) : libellé et valeur proposée. Une
    /// valeur saisie à la main, différente des anciennes valeurs par défaut, n'est jamais signalée.</summary>
    public IReadOnlyList<string> OutdatedFields()
    {
        var list = new List<string>();
        foreach (var k in UrssafKinds.All)
            if (RatesBp.GetValueOrDefault(k) == Legacy.RatesBp[k] && Legacy.RatesBp[k] != Reference.RatesBp[k])
                list.Add($"Taux {k.Short()} : {Legacy.RatesBp[k] / 100m:0.##} % → {Reference.RatesBp[k] / 100m:0.##} %");
        void Check(string label, long value, long old, long current)
        {
            if (value == old && old != current)
                list.Add($"{label} : {old / 100m:N0} € → {current / 100m:N0} €");
        }
        Check("Plafond de chiffre d'affaires", CeilingTotalCents, Legacy.CeilingTotalCents, Reference.CeilingTotalCents);
        Check("Plafond des prestations de services", CeilingServicesCents, Legacy.CeilingServicesCents, Reference.CeilingServicesCents);
        Check("Franchise de TVA (vente)", VatSaleCents, Legacy.VatSaleCents, Reference.VatSaleCents);
        Check("Franchise de TVA majorée (vente)", VatSaleMaxCents, Legacy.VatSaleMaxCents, Reference.VatSaleMaxCents);
        Check("Franchise de TVA (prestations)", VatServiceCents, Legacy.VatServiceCents, Reference.VatServiceCents);
        Check("Franchise de TVA majorée (prestations)", VatServiceMaxCents, Legacy.VatServiceMaxCents, Reference.VatServiceMaxCents);
        return list;
    }

    /// <summary>Remplace par la référence actuelle les seules valeurs qui portent une ancienne valeur par défaut
    /// (les réglages personnalisés et l'ACRE sont conservés).</summary>
    public UrssafParams WithCurrentReference()
    {
        var rates = RatesBp.ToDictionary(kv => kv.Key,
            kv => kv.Value == Legacy.RatesBp[kv.Key] ? Reference.RatesBp[kv.Key] : kv.Value);
        long Pick(long value, long old, long current) => value == old ? current : value;
        return this with
        {
            RatesBp = rates,
            CeilingTotalCents = Pick(CeilingTotalCents, Legacy.CeilingTotalCents, Reference.CeilingTotalCents),
            CeilingServicesCents = Pick(CeilingServicesCents, Legacy.CeilingServicesCents, Reference.CeilingServicesCents),
            VatSaleCents = Pick(VatSaleCents, Legacy.VatSaleCents, Reference.VatSaleCents),
            VatSaleMaxCents = Pick(VatSaleMaxCents, Legacy.VatSaleMaxCents, Reference.VatSaleMaxCents),
            VatServiceCents = Pick(VatServiceCents, Legacy.VatServiceCents, Reference.VatServiceCents),
            VatServiceMaxCents = Pick(VatServiceMaxCents, Legacy.VatServiceMaxCents, Reference.VatServiceMaxCents)
        };
    }

    /// <summary>Plafonds applicables à une année passée : tant que le réglage vaut la référence actuelle, une année
    /// antérieure à 2026 est évaluée avec les plafonds en vigueur alors (2023-2025). Un réglage personnalisé est respecté.</summary>
    public UrssafParams ForYear(int year) => year >= 2026 ? this : this with
    {
        CeilingTotalCents = CeilingTotalCents == Reference.CeilingTotalCents ? Legacy.CeilingTotalCents : CeilingTotalCents,
        CeilingServicesCents = CeilingServicesCents == Reference.CeilingServicesCents ? Legacy.CeilingServicesCents : CeilingServicesCents
    };

    public int RateFor(UrssafKind k, DateOnly periodEnd)
    {
        var rate = RatesBp.TryGetValue(k, out var r) ? r : 0;
        // Historique du taux BNC (SSI) tant que le réglage vaut la référence : 21,1 % jusqu'au 30/06/2024, 23,1 % du
        // 01/07 au 31/12/2024, 24,6 % en 2025 ; les périodes déjà déclarées gardent ainsi leur taux d'époque.
        if (k == UrssafKind.LiberalSsi && rate == Reference.RatesBp[UrssafKind.LiberalSsi])
        {
            if (periodEnd < new DateOnly(2024, 7, 1)) rate = 2110;
            else if (periodEnd < new DateOnly(2025, 1, 1)) rate = 2310;
            else if (periodEnd < new DateOnly(2026, 1, 1)) rate = 2460;
        }
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
public sealed record UrssafCeilings(int Year, long TotalCents, long ServicesCents, long SalesCents, UrssafParams Params,
    int? ProrataDays = null, int DaysInYear = 365)
{
    /// <summary>Plafonds dépassés (CA de l'année civile, plafonds proratisés l'année de création).</summary>
    public bool TotalExceeded => TotalCents > Params.CeilingTotalCents;
    public bool ServicesExceeded => ServicesCents > Params.CeilingServicesCents;
    public bool Exceeded => TotalExceeded || ServicesExceeded;

    /// <summary>Année de création de l'entreprise : plafonds ajustés au prorata du temps d'activité.</summary>
    public bool Prorated => ProrataDays is not null;

    public double TotalRatio => Params.CeilingTotalCents == 0 ? 0 : (double)TotalCents / Params.CeilingTotalCents;
    public double ServicesRatio => Params.CeilingServicesCents == 0 ? 0 : (double)ServicesCents / Params.CeilingServicesCents;
    public bool VatSaleExceeded => SalesCents > Params.VatSaleCents;
    public bool VatServiceExceeded => ServicesCents > Params.VatServiceCents;
}

public sealed record UrssafDeclaration(string PeriodKey, DateOnly Start, DateOnly End, DateTime DeclaredAt, string? DeclaredBy,
    long TurnoverCents, string? Note);