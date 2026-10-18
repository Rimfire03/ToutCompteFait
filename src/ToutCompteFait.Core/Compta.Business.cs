using System.Globalization;
using ToutCompteFait.Core.Business;
using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

/// <summary>Version Autoentreprise : profil de l'entreprise, types de déclaration URSSAF des catégories de produits,
/// déclarations de chiffre d'affaires. Tout est dans des tables hors des tables verrouillées avec les exercices.</summary>
public sealed partial class Compta
{
    // ------------------------------------------------------------------ réglages (clé / valeur)

    private static Dictionary<string, string> ReadBusinessSettings(AppDbContext ctx) =>
        ctx.BusinessSettings.AsNoTracking().AsEnumerable().ToDictionary(s => s.Key, s => s.Value);

    private static void WriteBusinessSettings(AppDbContext ctx, IReadOnlyDictionary<string, string?> values)
    {
        var existing = ctx.BusinessSettings.ToDictionary(s => s.Key);
        foreach (var (key, value) in values)
        {
            existing.TryGetValue(key, out var row);
            if (string.IsNullOrEmpty(value))
            {
                if (row is not null) ctx.BusinessSettings.Remove(row);
            }
            else if (row is null) ctx.BusinessSettings.Add(new BusinessSettingEntity { Key = key, Value = value });
            else row.Value = value;
        }
    }

    // ------------------------------------------------------------------ profil de l'entreprise

    public BusinessProfile GetBusinessProfile()
    {
        using var l = Open();
        var s = ReadBusinessSettings(l.Ctx);
        string? G(string k) => s.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        return new BusinessProfile
        {
            Siren = G("profile.siren"),
            Siret = G("profile.siret"),
            Name = G("profile.name"),
            Address = G("profile.address"),
            PostalCode = G("profile.postal"),
            City = G("profile.city"),
            CreationDate = DateOnly.TryParseExact(G("profile.created"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
            LegalNature = G("profile.nature"),
            Rna = G("profile.rna"),
            ApeMain = G("profile.ape"),
            ApeCodes = (G("profile.apes") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Mode = G("profile.mode") == "M" ? DeclarationMode.Monthly : DeclarationMode.Quarterly,
            FetchedAt = DateTime.TryParse(G("profile.fetched"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null
        };
    }

    public void SaveBusinessProfile(BusinessProfile p)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        var apes = p.ApeCodes.Select(a => ApeMapper.Normalize(a) ?? a.Trim()).Where(a => a.Length > 0).Distinct().ToList();
        using var tx = Begin();
        var ctx = Open().Ctx;
        WriteBusinessSettings(ctx, new Dictionary<string, string?>
        {
            ["profile.siren"] = p.Siren?.Trim(),
            ["profile.siret"] = p.Siret?.Trim(),
            ["profile.name"] = p.Name?.Trim(),
            ["profile.address"] = p.Address?.Trim(),
            ["profile.postal"] = p.PostalCode?.Trim(),
            ["profile.city"] = p.City?.Trim(),
            ["profile.created"] = p.CreationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["profile.nature"] = p.LegalNature?.Trim(),
            ["profile.rna"] = p.Rna?.Trim(),
            ["profile.ape"] = ApeMapper.Normalize(p.ApeMain) ?? p.ApeMain?.Trim(),
            ["profile.apes"] = string.Join(";", apes),
            ["profile.mode"] = p.Mode == DeclarationMode.Monthly ? "M" : "Q",
            ["profile.fetched"] = p.FetchedAt?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
        });
        Save(ctx);
        Log(ctx, "Entreprise : profil enregistré",
            $"{p.Name} · SIREN {p.Siren} · APE {string.Join(", ", apes)} · déclaration {(p.Mode == DeclarationMode.Monthly ? "mensuelle" : "trimestrielle")}");
        tx.Commit();
    }

    // ------------------------------------------------------------------ taux, plafonds, ACRE

    public UrssafParams GetUrssafParams()
    {
        using var l = Open();
        var s = ReadBusinessSettings(l.Ctx);
        var d = new UrssafParams();
        long Cents(string key, long dflt) => s.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : dflt;
        var rates = d.RatesBp.ToDictionary(kv => kv.Key, kv => (int)Cents("rate." + kv.Key.Code(), kv.Value));
        return d with
        {
            RatesBp = rates,
            CeilingTotalCents = Cents("ceil.total", d.CeilingTotalCents),
            CeilingServicesCents = Cents("ceil.services", d.CeilingServicesCents),
            VatSaleCents = Cents("vat.sale", d.VatSaleCents),
            VatSaleMaxCents = Cents("vat.sale.max", d.VatSaleMaxCents),
            VatServiceCents = Cents("vat.service", d.VatServiceCents),
            VatServiceMaxCents = Cents("vat.service.max", d.VatServiceMaxCents),
            AcreDiscountPercent = (int)Cents("acre.discount", d.AcreDiscountPercent),
            AcreUntil = DateOnly.TryParseExact(s.GetValueOrDefault("acre.until"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u) ? u : null
        };
    }

    public void SaveUrssafParams(UrssafParams p)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        if (p.RatesBp.Values.Any(r => r is < 0 or > 10000)) throw new ComptaException("Un taux de cotisations doit être compris entre 0 et 100 %.");
        if (p.AcreDiscountPercent is < 0 or > 100) throw new ComptaException("La réduction ACRE doit être comprise entre 0 et 100 %.");
        var values = new Dictionary<string, string?>
        {
            ["ceil.total"] = p.CeilingTotalCents.ToString(CultureInfo.InvariantCulture),
            ["ceil.services"] = p.CeilingServicesCents.ToString(CultureInfo.InvariantCulture),
            ["vat.sale"] = p.VatSaleCents.ToString(CultureInfo.InvariantCulture),
            ["vat.sale.max"] = p.VatSaleMaxCents.ToString(CultureInfo.InvariantCulture),
            ["vat.service"] = p.VatServiceCents.ToString(CultureInfo.InvariantCulture),
            ["vat.service.max"] = p.VatServiceMaxCents.ToString(CultureInfo.InvariantCulture),
            ["acre.discount"] = p.AcreDiscountPercent.ToString(CultureInfo.InvariantCulture),
            ["acre.until"] = p.AcreUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        foreach (var k in UrssafKinds.All)
            values["rate." + k.Code()] = (p.RatesBp.TryGetValue(k, out var r) ? r : 0).ToString(CultureInfo.InvariantCulture);
        using var tx = Begin();
        var ctx = Open().Ctx;
        WriteBusinessSettings(ctx, values);
        Save(ctx);
        Log(ctx, "Entreprise : taux et plafonds enregistrés",
            string.Join(" · ", UrssafKinds.All.Select(k => $"{k.Short()} {(p.RatesBp.GetValueOrDefault(k)) / 100m:0.##} %")) +
            (p.AcreUntil is { } u ? $" · ACRE jusqu'au {u:dd/MM/yyyy}" : ""));
        tx.Commit();
    }

    // ------------------------------------------------------------------ type de déclaration des catégories de produits

    /// <summary>Type URSSAF de chaque nom de catégorie de produit (clé en minuscules).</summary>
    public Dictionary<string, UrssafKind> CategoryUrssafKinds()
    {
        using var l = Open();
        var map = new Dictionary<string, UrssafKind>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var r in l.Ctx.CategoryUrssaf.AsNoTracking().AsEnumerable())
            if (UrssafKinds.FromCode(r.Kind) is { } k) map[r.Name] = k;
        return map;
    }

    /// <summary>Type URSSAF d'une catégorie : celui enregistré, à défaut celui que son nom indique (« BIC vente – … »).</summary>
    public static UrssafKind? ResolveUrssafKind(IReadOnlyDictionary<string, UrssafKind> map, string categoryName) =>
        map.TryGetValue(categoryName, out var k) ? k : UrssafKinds.FromCategoryName(categoryName);

    public void SetCategoryUrssafKind(string categoryName, UrssafKind? kind)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        if (kind is not null && PersonalProductNames().Contains(categoryName))
            throw new ComptaException($"« {categoryName} » est un produit personnel (apport de l'exploitant) : il n'a pas de type de déclaration URSSAF.");
        using var tx = Begin();
        var ctx = Open().Ctx;
        SetCategoryUrssafKind(ctx, categoryName, kind);
        Save(ctx);
        Log(ctx, "Type de déclaration URSSAF d'une catégorie", $"« {categoryName} » : {kind?.Label() ?? "(aucun)"}");
        tx.Commit();
    }

    private static void SetCategoryUrssafKind(AppDbContext ctx, string name, UrssafKind? kind)
    {
        var existing = ctx.CategoryUrssaf.AsEnumerable().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (kind is null)
        {
            if (existing is not null) ctx.CategoryUrssaf.Remove(existing);
        }
        else if (existing is null) ctx.CategoryUrssaf.Add(new CategoryUrssafEntity { Name = name, Kind = kind.Value.Code() });
        else existing.Kind = kind.Value.Code();
    }

    /// <summary>Crée un produit (recette) d'un type de déclaration, nommé « BIC vente » ou « BIC vente – nom ».</summary>
    public Category AddActivityCategory(long yearId, UrssafKind kind, string? customName)
    {
        using var tx = Begin();
        var cat = AddCategory(yearId, CategoryKind.Product, UrssafKinds.CategoryName(kind, customName));
        var ctx = Open().Ctx;
        SetCategoryUrssafKind(ctx, cat.Name, kind);
        Save(ctx);
        tx.Commit();
        return cat;
    }

    // ------------------------------------------------------------------ profil court de première ouverture

    private const string OnboardedKey = "profile.onboarded";

    /// <summary>Faut-il proposer le profil court (3 questions) ? Seulement pour un dossier neuf : entreprise non renseignée,
    /// aucune écriture dans l'exercice, question pas encore posée ni écartée.</summary>
    public bool ShouldOfferBusinessOnboarding(long yearId)
    {
        using (var l = Open())
            if (l.Ctx.BusinessSettings.AsNoTracking().Any(s => s.Key == OnboardedKey)) return false;
        return string.IsNullOrWhiteSpace(GetBusinessProfile().Name) && Entries(yearId).Count == 0;
    }

    /// <summary>Applique le profil : modèle de catégories de l'activité, mode de déclaration URSSAF, gestion des
    /// immobilisations ; la question n'est plus posée ensuite. <paramref name="templateId"/> null = aucun modèle.</summary>
    public void CompleteBusinessOnboarding(long yearId, string? templateId, DeclarationMode mode, bool useAssets)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        if (templateId is not null && CategoryTemplates.Find(templateId) is { } t) ApplyCategoryTemplate(yearId, t.Items);
        SaveBusinessProfile(GetBusinessProfile() with { Mode = mode });
        if (useAssets) SaveAssetSettings(true, AssetThresholdCents());
        SkipBusinessOnboarding();
    }

    /// <summary>Ne plus proposer le profil court.</summary>
    public void SkipBusinessOnboarding()
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        using var tx = Begin();
        var ctx = Open().Ctx;
        WriteBusinessSettings(ctx, new Dictionary<string, string?> { [OnboardedKey] = "1" });
        Save(ctx);
        tx.Commit();
    }

    /// <summary>Ajoute à l'exercice les catégories d'un modèle d'activité qui n'existent pas encore (comparaison du nom sans
    /// tenir compte de la casse), avec leur numéro de compte usuel et, pour un produit, son type URSSAF. Renvoie le nombre de
    /// catégories ajoutées ; les catégories déjà présentes ne sont jamais modifiées.</summary>
    public int ApplyCategoryTemplate(long yearId, IEnumerable<TemplateItem> items)
    {
        RequireOpen(yearId, Permission.ManageStructure);
        var existing = Categories(yearId).Select(c => (c.Kind, c.Name.ToLowerInvariant())).ToHashSet();
        var personalProducts = PersonalProductNames();
        var added = 0;
        foreach (var it in items)
        {
            if (existing.Contains((it.Kind, it.Name.ToLowerInvariant()))) continue;
            AddCategory(yearId, it.Kind, it.Name);
            SetCategoryAccountNumber(it.Kind, it.Name, it.AccountNumber);
            if (it.Kind == CategoryKind.Product && it.Urssaf is { } k && !personalProducts.Contains(it.Name)) SetCategoryUrssafKind(it.Name, k);
            added++;
        }
        return added;
    }

    /// <summary>Un renommage de catégorie emporte son type URSSAF (clé = nom).</summary>
    private static void MoveCategoryUrssaf(AppDbContext ctx, string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.CurrentCultureIgnoreCase)) return;
        var row = ctx.CategoryUrssaf.AsEnumerable().FirstOrDefault(c => string.Equals(c.Name, oldName, StringComparison.CurrentCultureIgnoreCase));
        if (row is null) return;
        var kind = row.Kind;
        ctx.CategoryUrssaf.Remove(row);
        if (!ctx.CategoryUrssaf.AsEnumerable().Any(c => string.Equals(c.Name, newName, StringComparison.CurrentCultureIgnoreCase)))
            ctx.CategoryUrssaf.Add(new CategoryUrssafEntity { Name = newName, Kind = kind });
    }

    // ------------------------------------------------------------------ chiffre d'affaires par type (encaissements)

    /// <summary>Chiffre d'affaires encaissé entre deux dates, par type de déclaration : somme des ventilations sur les
    /// produits des écritures d'argent (virements et opérations sans mouvement exclus ; écritures annulées déjà
    /// neutralisées). <paramref name="unclassified"/> : produits sans type ; <paramref name="pending"/> : entrées d'argent
    /// de la période pas encore ventilées (le total peut être incomplet).</summary>
    private Dictionary<UrssafKind, long> TurnoverByKind(DateOnly from, DateOnly to, out long unclassified, out int pending)
    {
        var kinds = CategoryUrssafKinds();
        var personalProducts = PersonalProductNames();
        var result = UrssafKinds.All.ToDictionary(k => k, _ => 0L);
        unclassified = 0;
        pending = 0;
        foreach (var year in Years().Where(y => y.End >= from && y.Start <= to))
        {
            var cats = Categories(year.Id).ToDictionary(c => c.Id);
            foreach (var row in Entries(year.Id))
            {
                var e = row.Entry;
                if (e.Kind != EntryKind.Normal || e.Date < from || e.Date > to) continue;
                if (row.Allocations.Count == 0 && e.Direction == Direction.In) pending++;
                foreach (var a in row.Allocations)
                {
                    if (!cats.TryGetValue(a.CategoryId, out var cat) || cat.Kind != CategoryKind.Product) continue;
                    if (personalProducts.Contains(cat.Name)) continue;   // apport personnel : pas du chiffre d'affaires
                    if (ResolveUrssafKind(kinds, cat.Name) is { } k) result[k] += a.AmountCents;
                    else unclassified += a.AmountCents;
                }
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ rapport de déclaration

    public UrssafReport ComputeUrssafReport(UrssafPeriod period)
    {
        var prm = GetUrssafParams();
        var byKind = TurnoverByKind(period.Start, period.End, out var unclassified, out var pending);
        var used = CategoryUrssafKinds().Values.ToHashSet();
        var lines = new List<UrssafLine>();
        foreach (var k in UrssafKinds.All)
        {
            var turnover = byKind[k];
            if (turnover == 0 && !used.Contains(k)) continue;
            var rate = prm.RateFor(k, period.End);
            // Cotisations estimées : arrondies au centime, jamais négatives (un avoir ne donne pas de crédit).
            var contribution = (long)Math.Max(0, Math.Round(turnover * rate / 10000m, 0, MidpointRounding.AwayFromZero));
            lines.Add(new UrssafLine(k, turnover, rate, contribution));
        }
        var declared = Declarations().FirstOrDefault(d => d.PeriodKey == period.Key);
        return new UrssafReport(period, lines, lines.Sum(x => x.TurnoverCents), lines.Sum(x => x.ContributionCents),
            unclassified, pending, declared?.DeclaredAt, declared?.DeclaredBy);
    }

    /// <summary>Chiffre d'affaires cumulé de l'année civile face aux plafonds du régime micro et aux seuils de TVA.</summary>
    public UrssafCeilings ComputeUrssafCeilings(int calendarYear)
    {
        var byKind = TurnoverByKind(new DateOnly(calendarYear, 1, 1), new DateOnly(calendarYear, 12, 31), out _, out _);
        var total = byKind.Values.Sum();
        var services = byKind.Where(kv => kv.Key.IsService()).Sum(kv => kv.Value);
        var prm = GetUrssafParams().ForYear(calendarYear);
        // Année de création : plafonds du régime micro ajustés au prorata du temps d'activité (art. 50-0 CGI).
        int? prorataDays = null;
        var daysInYear = DateTime.IsLeapYear(calendarYear) ? 366 : 365;
        if (GetBusinessProfile().CreationDate is { } created && created.Year == calendarYear && created.DayOfYear > 1)
        {
            prorataDays = daysInYear - created.DayOfYear + 1;
            long Scale(long c) => (long)Math.Round(c * (decimal)prorataDays.Value / daysInYear, 0, MidpointRounding.AwayFromZero);
            prm = prm with { CeilingTotalCents = Scale(prm.CeilingTotalCents), CeilingServicesCents = Scale(prm.CeilingServicesCents) };
        }
        return new UrssafCeilings(calendarYear, total, services, byKind[UrssafKind.Sale], prm, prorataDays, daysInYear);
    }

    // ------------------------------------------------------------------ déclarations faites

    public List<UrssafDeclaration> Declarations()
    {
        using var l = Open();
        return l.Ctx.UrssafDeclarations.AsNoTracking().AsEnumerable()
            .Select(d => new UrssafDeclaration(d.PeriodKey,
                DateOnly.ParseExact(d.PeriodStart, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateOnly.ParseExact(d.PeriodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTime.Parse(d.DeclaredAt, CultureInfo.InvariantCulture), d.DeclaredBy, d.TurnoverCents, d.Note, d.DeclaredCents))
            .OrderByDescending(d => d.Start).ToList();
    }

    /// <summary>Périodes terminées dont la déclaration n'est pas marquée « faite » (de la plus ancienne à la plus récente).
    /// Remonte au plus à 12 mois, et pas avant le début de l'activité (création de l'entreprise ou premier exercice) :
    /// sert à l'alerte affichée à l'ouverture de l'application.</summary>
    public List<UrssafPeriod> PendingUrssafDeclarations(DateOnly today)
    {
        var profile = GetBusinessProfile();
        var earliest = today.AddMonths(-12);
        var starts = new List<DateOnly>();
        if (profile.CreationDate is { } created) starts.Add(created);
        var years = Years();
        if (years.Count > 0) starts.Add(years.Min(y => y.Start));
        if (starts.Count == 0) return new List<UrssafPeriod>();   // aucune activité connue : rien à déclarer
        var from = starts.Min() > earliest ? starts.Min() : earliest;
        var done = Declarations().Select(d => d.PeriodKey).ToHashSet();
        var result = new List<UrssafPeriod>();
        var p = UrssafPeriod.ForDate(today, profile.Mode);
        for (var i = 0; i < 24; i++)
        {
            p = UrssafPeriod.ForDate(p.Start.AddDays(-1), profile.Mode);   // période précédente : terminée
            if (p.End < from) break;
            if (!done.Contains(p.Key)) result.Insert(0, p);
        }
        return result;
    }

    /// <summary>Marque la période comme déclarée à l'URSSAF (trace : date, auteur, montant du chiffre d'affaires).</summary>
    public void MarkUrssafDeclared(UrssafPeriod period, string? note = null, long? declaredCents = null)
    {
        Require(Permission.WriteLedger);
        RequireWritable();
        var report = ComputeUrssafReport(period);
        using var tx = Begin();
        var ctx = Open().Ctx;
        var row = ctx.UrssafDeclarations.FirstOrDefault(d => d.PeriodKey == period.Key);
        if (row is null)
        {
            row = new UrssafDeclarationEntity { PeriodKey = period.Key };
            ctx.UrssafDeclarations.Add(row);
        }
        row.PeriodStart = period.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        row.PeriodEnd = period.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        row.DeclaredAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        row.DeclaredBy = Session?.DisplayName;
        row.TurnoverCents = report.TotalTurnoverCents;
        row.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        row.DeclaredCents = declaredCents;
        Save(ctx);
        Log(ctx, "Déclaration URSSAF marquée comme faite", $"{period.Label} : chiffre d'affaires calculé {Money.Format(report.TotalTurnoverCents)} €" +
            (declaredCents is { } dc ? $", montant déclaré {Money.Format(dc)} €" : ""));
        tx.Commit();
    }

    /// <summary>État d'une période dans le calendrier des échéances.</summary>
    public enum UrssafPeriodState { Declared, Late, Due, Running, Upcoming }

    /// <summary>Une ligne du calendrier : période, état et, si déclarée, montants calculé et déclaré.</summary>
    public sealed record UrssafCalendarEntry(UrssafPeriod Period, UrssafPeriodState State, UrssafDeclaration? Declaration);

    /// <summary>Périodes de déclaration d'une année civile avec leur état : déclarée, en retard (échéance dépassée), à faire
    /// (période terminée), en cours ou à venir.</summary>
    public List<UrssafCalendarEntry> UrssafCalendar(int calendarYear, DateOnly today)
    {
        var mode = GetBusinessProfile().Mode;
        var declared = Declarations().ToDictionary(d => d.PeriodKey);
        return UrssafPeriod.OfYear(calendarYear, mode).Select(p =>
        {
            declared.TryGetValue(p.Key, out var d);
            var state = d is not null ? UrssafPeriodState.Declared
                : today <= p.End ? (today >= p.Start ? UrssafPeriodState.Running : UrssafPeriodState.Upcoming)
                : today > p.DueDate ? UrssafPeriodState.Late : UrssafPeriodState.Due;
            return new UrssafCalendarEntry(p, state, d);
        }).ToList();
    }

    public void UnmarkUrssafDeclared(string periodKey)
    {
        Require(Permission.WriteLedger);
        RequireWritable();
        using var tx = Begin();
        var ctx = Open().Ctx;
        var row = ctx.UrssafDeclarations.FirstOrDefault(d => d.PeriodKey == periodKey);
        if (row is null) return;
        ctx.UrssafDeclarations.Remove(row);
        Save(ctx);
        Log(ctx, "Déclaration URSSAF : marque retirée", periodKey);
        tx.Commit();
    }
}