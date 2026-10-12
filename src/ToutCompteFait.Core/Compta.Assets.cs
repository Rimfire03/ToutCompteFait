using System.Globalization;
using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

/// <summary>Prorata temporis de la première année : au mois (le mois d'acquisition compte en entier) ou au jour.</summary>
public enum AssetProrata { Monthly, Daily }

/// <summary>Immobilisation amortie linéairement. <see cref="Existing"/> : bien déjà possédé repris à sa valeur nette
/// (aucun achat dans le livre) ; sinon l'achat figure en charge dans le livre et l'immobilisation le neutralise.</summary>
public sealed record FixedAsset(long Id, string Label, DateOnly AcquiredOn, long CostCents, int Years, AssetProrata Prorata,
    bool Existing, string? CategoryName, long? EntryId, DateOnly? DisposedOn, string? Note)
{
    public bool Disposed => DisposedOn is not null;
}

/// <summary>Situation d'une immobilisation sur un exercice : valeur nette à l'ouverture, achat neutralisé, dotation,
/// valeur nette sortie (cession / mise au rebut) et valeur nette à la clôture.</summary>
public sealed record AssetYearLine(FixedAsset Asset, long Opening, long Reclass, long Depreciation, long Disposal,
    long Closing, long CumulativeAtClose);

/// <summary>Calcul des amortissements (linéaire, au mois ou au jour). Fonction pure : aucune donnée n'est stockée, les
/// règles de modification du registre garantissent qu'un exercice validé ne change plus.</summary>
public static class AssetCalc
{
    /// <summary>Amortissement cumulé du premier jour d'utilisation jusqu'à <paramref name="t"/> inclus (plafonné au coût,
    /// arrêté à la date de sortie).</summary>
    public static long Cumulative(FixedAsset a, DateOnly t)
    {
        if (a.DisposedOn is { } d && t > d) t = d;
        if (t < a.AcquiredOn || a.Years <= 0) return 0;
        decimal elapsed, total;
        if (a.Prorata == AssetProrata.Daily)
        {
            elapsed = t.DayNumber - a.AcquiredOn.DayNumber + 1;
            total = a.AcquiredOn.AddYears(a.Years).DayNumber - a.AcquiredOn.DayNumber;
        }
        else
        {
            elapsed = (t.Year - a.AcquiredOn.Year) * 12 + t.Month - a.AcquiredOn.Month + 1;
            total = a.Years * 12;
        }
        if (elapsed >= total) return a.CostCents;
        return (long)Math.Round(a.CostCents * elapsed / total, MidpointRounding.AwayFromZero);
    }

    public static AssetYearLine ForYear(FixedAsset a, DateOnly start, DateOnly end)
    {
        var zero = new AssetYearLine(a, 0, 0, 0, 0, 0, 0);
        if (a.AcquiredOn > end) return zero;
        if (a.DisposedOn is { } gone && gone < start) return zero;

        var before = a.AcquiredOn < start;
        var cumPrev = before ? Cumulative(a, start.AddDays(-1)) : 0;
        var cumEnd = Cumulative(a, end);
        var opening = before ? a.CostCents - cumPrev : (a.Existing ? a.CostCents : 0);
        var reclass = !before && !a.Existing ? a.CostCents : 0;
        var depreciation = cumEnd - cumPrev;
        var disposedInYear = a.DisposedOn is { } d && d >= start && d <= end;
        var disposal = disposedInYear ? a.CostCents - cumEnd : 0;
        var closing = a.DisposedOn is { } d2 && d2 <= end ? 0 : a.CostCents - cumEnd;
        return new AssetYearLine(a, opening, reclass, depreciation, disposal, closing, cumEnd);
    }
}

public sealed partial class Compta
{
    // ------------------------------------------------------------------ réglages

    private const string AssetsEnabledKey = "assets.enabled";
    private const string AssetThresholdKey = "assets.threshold_cents";

    /// <summary>Seuil par défaut du petit matériel (500 €) : en dessous, l'achat reste une charge directe.</summary>
    public const long DefaultAssetThresholdCents = 50000;

    public bool AssetsEnabled()
    {
        using var l = Open();
        return l.Ctx.BusinessSettings.AsNoTracking().Where(s => s.Key == AssetsEnabledKey).Select(s => s.Value).FirstOrDefault() == "1";
    }

    public long AssetThresholdCents()
    {
        using var l = Open();
        var v = l.Ctx.BusinessSettings.AsNoTracking().Where(s => s.Key == AssetThresholdKey).Select(s => s.Value).FirstOrDefault();
        return long.TryParse(v, out var cents) && cents >= 0 ? cents : DefaultAssetThresholdCents;
    }

    public void SaveAssetSettings(bool enabled, long thresholdCents)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        if (thresholdCents < 0) throw new ComptaException("Le seuil ne peut pas être négatif.");
        using var tx = Begin();
        var ctx = Open().Ctx;
        WriteBusinessSettings(ctx, new Dictionary<string, string?>
        {
            [AssetsEnabledKey] = enabled ? "1" : null,
            [AssetThresholdKey] = thresholdCents == DefaultAssetThresholdCents ? null : thresholdCents.ToString(CultureInfo.InvariantCulture)
        });
        Save(ctx);
        Log(ctx, "Réglages des immobilisations", $"{(enabled ? "activées" : "désactivées")} · seuil {Money.Format(thresholdCents)} €");
        tx.Commit();
    }

    // ------------------------------------------------------------------ registre

    private static FixedAsset ToFixedAsset(FixedAssetEntity e) => new(e.Id, e.Label,
        DateOnly.ParseExact(e.AcquiredOn, "yyyy-MM-dd", CultureInfo.InvariantCulture), e.CostCents, e.Years,
        e.Prorata == "D" ? AssetProrata.Daily : AssetProrata.Monthly, e.Origin == "E", e.CategoryName, e.EntryId,
        e.DisposedOn is { Length: > 0 } d ? DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null, e.Note);

    public List<FixedAsset> FixedAssets()
    {
        using var l = Open();
        return l.Ctx.FixedAssets.AsNoTracking().OrderBy(a => a.AcquiredOn).ThenBy(a => a.Id).AsEnumerable().Select(ToFixedAsset).ToList();
    }

    /// <summary>Fin du dernier exercice validé : tout bien acquis avant cette date est figé (il est déjà dans un exercice
    /// validé dont le résultat ne doit plus changer).</summary>
    private DateOnly? LastValidatedEnd() =>
        Years().Where(y => y.Validated).Select(y => (DateOnly?)y.End).OrderByDescending(d => d).FirstOrDefault();

    private void CheckAsset(string label, long cost, int years, DateOnly acquired, DateOnly? disposed)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new ComptaException("Le libellé est obligatoire.");
        if (cost <= 0) throw new ComptaException("La valeur doit être supérieure à zéro.");
        if (years is < 1 or > 50) throw new ComptaException("La durée d'amortissement est de 1 à 50 ans.");
        if (disposed is { } d && d < acquired) throw new ComptaException("La sortie ne peut pas précéder l'acquisition.");
    }

    private void CheckAssetPeriod(DateOnly acquired)
    {
        if (LastValidatedEnd() is { } last && acquired <= last)
            throw new ComptaException(
                $"La date d'acquisition ({acquired:dd/MM/yyyy}) tombe dans un exercice validé (jusqu'au {last:dd/MM/yyyy}), dont le résultat ne peut plus changer.\n" +
                "Pour un bien acheté avant : choisissez « bien déjà possédé », la date de début de l'exercice ouvert, sa valeur nette et la durée restante.");
    }

    public long AddFixedAsset(string label, DateOnly acquiredOn, long costCents, int years, AssetProrata prorata, bool existing,
        string? categoryName = null, long? entryId = null, string? note = null)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        CheckAsset(label, costCents, years, acquiredOn, null);
        CheckAssetPeriod(acquiredOn);
        using var tx = Begin();
        var ctx = Open().Ctx;
        var e = new FixedAssetEntity
        {
            Label = label.Trim(), AcquiredOn = D(acquiredOn), CostCents = costCents, Years = years,
            Prorata = prorata == AssetProrata.Daily ? "D" : "M", Origin = existing ? "E" : "P",
            CategoryName = NullIfBlank(categoryName), EntryId = entryId, Note = NullIfBlank(note), CreatedAt = Now()
        };
        ctx.FixedAssets.Add(e);
        Save(ctx);
        Log(ctx, "Immobilisation ajoutée", DescribeAsset(e));
        tx.Commit();
        return e.Id;
    }

    /// <summary>Modifie une immobilisation. La valeur, la date, la durée et le mode ne changent plus une fois le bien
    /// couvert par un exercice validé ; le libellé et le commentaire restent modifiables.</summary>
    public void UpdateFixedAsset(long id, string label, DateOnly acquiredOn, long costCents, int years, AssetProrata prorata,
        bool existing, string? categoryName, string? note)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        using var tx = Begin();
        var ctx = Open().Ctx;
        var e = ctx.FixedAssets.FirstOrDefault(x => x.Id == id) ?? throw new ComptaException("Immobilisation introuvable.");
        var current = ToFixedAsset(e);
        var frozen = LastValidatedEnd() is { } last && current.AcquiredOn <= last;
        var changesCore = acquiredOn != current.AcquiredOn || costCents != current.CostCents || years != current.Years ||
                          prorata != current.Prorata || existing != current.Existing;
        if (frozen && changesCore)
            throw new ComptaException("Ce bien est déjà compté dans un exercice validé : sa valeur, sa date, sa durée et son mode ne peuvent plus changer.");
        CheckAsset(label, costCents, years, acquiredOn, current.DisposedOn);
        if (!frozen) CheckAssetPeriod(acquiredOn);
        var before = DescribeAsset(e);
        e.Label = label.Trim();
        e.AcquiredOn = D(acquiredOn);
        e.CostCents = costCents;
        e.Years = years;
        e.Prorata = prorata == AssetProrata.Daily ? "D" : "M";
        e.Origin = existing ? "E" : "P";
        e.CategoryName = NullIfBlank(categoryName);
        e.Note = NullIfBlank(note);
        Save(ctx);
        Log(ctx, "Immobilisation modifiée", $"Avant : {before}\nAprès : {DescribeAsset(e)}");
        tx.Commit();
    }

    /// <summary>Sortie d'une immobilisation (cession, mise au rebut) à une date postérieure aux exercices validés ;
    /// <c>null</c> annule la sortie. La valeur nette restante est passée en charge sur l'exercice de la sortie.</summary>
    public void DisposeFixedAsset(long id, DateOnly? date)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        using var tx = Begin();
        var ctx = Open().Ctx;
        var e = ctx.FixedAssets.FirstOrDefault(x => x.Id == id) ?? throw new ComptaException("Immobilisation introuvable.");
        var a = ToFixedAsset(e);
        if (date is { } d)
        {
            if (d < a.AcquiredOn) throw new ComptaException("La sortie ne peut pas précéder l'acquisition.");
            if (LastValidatedEnd() is { } last && d <= last)
                throw new ComptaException($"La sortie doit être postérieure au dernier exercice validé ({last:dd/MM/yyyy}).");
        }
        else if (a.DisposedOn is { } old && LastValidatedEnd() is { } last2 && old <= last2)
            throw new ComptaException("La sortie est déjà comptée dans un exercice validé : elle ne peut plus être annulée.");
        e.DisposedOn = date is { } dd ? D(dd) : null;
        Save(ctx);
        Log(ctx, date is null ? "Sortie d'immobilisation annulée" : "Sortie d'immobilisation", DescribeAsset(e));
        tx.Commit();
    }

    public void DeleteFixedAsset(long id)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        using var tx = Begin();
        var ctx = Open().Ctx;
        var e = ctx.FixedAssets.FirstOrDefault(x => x.Id == id) ?? throw new ComptaException("Immobilisation introuvable.");
        if (LastValidatedEnd() is { } last && ToFixedAsset(e).AcquiredOn <= last)
            throw new ComptaException("Ce bien est déjà compté dans un exercice validé : il ne peut pas être supprimé.");
        var text = DescribeAsset(e);
        ctx.FixedAssets.Remove(e);
        Save(ctx);
        Log(ctx, "Immobilisation supprimée", text);
        tx.Commit();
    }

    private static string DescribeAsset(FixedAssetEntity e) =>
        $"« {e.Label} » · {Money.Format(e.CostCents)} € · acquise le {DateOnly.ParseExact(e.AcquiredOn, "yyyy-MM-dd", CultureInfo.InvariantCulture):dd/MM/yyyy}" +
        $" · {e.Years} an(s) · prorata {(e.Prorata == "D" ? "au jour" : "au mois")}" +
        (e.Origin == "E" ? " · bien déjà possédé" : "") +
        (e.DisposedOn is { Length: > 0 } d ? $" · sortie le {DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture):dd/MM/yyyy}" : "");

    /// <summary>Situation de chaque immobilisation sur un exercice (biens sans effet sur l'exercice omis).</summary>
    public List<AssetYearLine> AssetLines(long yearId)
    {
        var y = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        return FixedAssets().Select(a => AssetCalc.ForYear(a, y.Start, y.End))
            .Where(l => l.Opening != 0 || l.Reclass != 0 || l.Depreciation != 0 || l.Disposal != 0 || l.Closing != 0).ToList();
    }
}
