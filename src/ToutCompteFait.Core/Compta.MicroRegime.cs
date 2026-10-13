using System.Globalization;
using ToutCompteFait.Core.Business;

namespace ToutCompteFait.Core;

public enum NoticeLevel { Info, Warning, Alert }

/// <summary>Situation de l'entreprise face aux plafonds du régime micro (règle des deux dépassements consécutifs).</summary>
public sealed record MicroRegimeStatus(int Year, bool PreviousExceeded, bool CurrentExceeded, NoticeLevel Level, string Message);

/// <summary>Avis sur la franchise en base de TVA d'une activité (vente ou prestations).</summary>
public sealed record VatNotice(string Activity, NoticeLevel Level, string Message, DateOnly? LiableFrom);

public sealed partial class Compta
{
    private static string Eur(long cents) => (cents / 100m).ToString("N0", Money.Fr) + " €";

    // ------------------------------------------------------------------ régime micro : deux dépassements consécutifs

    /// <summary>Un dépassement des plafonds (total ou prestations) une seule année est sans conséquence ; deux années civiles
    /// consécutives font sortir du régime micro au 1er janvier de l'année suivante. Le plafond de l'année de création est
    /// proratisé (voir <see cref="ComputeUrssafCeilings"/>).</summary>
    public MicroRegimeStatus ComputeMicroRegimeStatus(int year)
    {
        var cur = ComputeUrssafCeilings(year);
        var prev = ComputeUrssafCeilings(year - 1);
        var prevHas = prev.TotalCents > 0;
        var prevEx = prevHas && prev.Exceeded;
        var curEx = cur.Exceeded;

        var proNote = cur.Prorated
            ? $" (plafond de {year} ajusté au prorata : {cur.ProrataDays} jour(s) d'activité sur {cur.DaysInYear})"
            : "";
        if (prevEx && curEx)
            return new MicroRegimeStatus(year, true, true, NoticeLevel.Alert,
                $"Plafond du régime micro dépassé en {year - 1} et en {year}{proNote} : vous sortez du régime micro au 1er janvier {year + 1} " +
                "(régime réel : comptabilité complète, TVA, déclaration de résultat). Rapprochez-vous de votre expert-comptable.");
        if (prevEx)
            return new MicroRegimeStatus(year, true, false, NoticeLevel.Warning,
                $"Plafond dépassé en {year - 1}. Un nouveau dépassement en {year}{proNote} (total {Eur(cur.Params.CeilingTotalCents)}, " +
                $"prestations {Eur(cur.Params.CeilingServicesCents)}) vous ferait sortir du régime micro au 1er janvier {year + 1}.");
        if (curEx)
            return new MicroRegimeStatus(year, false, true, NoticeLevel.Warning,
                $"Plafond dépassé en {year}{proNote} (premier dépassement) : sans conséquence si {year + 1} reste sous les plafonds ; " +
                $"un second dépassement consécutif ferait sortir du régime micro au 1er janvier {year + 2}.");
        var ratio = Math.Max(cur.TotalRatio, cur.ServicesRatio);
        if (ratio >= 0.8)
            return new MicroRegimeStatus(year, false, false, NoticeLevel.Info,
                $"{ratio:P0} d'un plafond du régime micro atteint en {year}{proNote}.");
        return new MicroRegimeStatus(year, false, false, NoticeLevel.Info,
            $"Sous les plafonds du régime micro en {year}{proNote}.");
    }

    // ------------------------------------------------------------------ franchise de TVA : seuils et dates

    /// <summary>Avis sur la franchise en base de TVA (art. 293 B du CGI), par activité :
    /// dépassement du seuil majoré → TVA exigible dès le 1er jour du mois de dépassement ; dépassement du seuil de base →
    /// franchise maintenue jusqu'au 31 décembre, TVA à partir du 1er janvier suivant. Règle générale : à confirmer avec le
    /// service des impôts des entreprises (SIE).</summary>
    public IReadOnlyList<VatNotice> ComputeVatNotices(int year)
    {
        var notices = new List<VatNotice>();
        var prm = GetUrssafParams();
        foreach (var (activity, isService, baseCents, maxCents) in new[]
                 {
                     ("Ventes de marchandises", false, prm.VatSaleCents, prm.VatSaleMaxCents),
                     ("Prestations de services", true, prm.VatServiceCents, prm.VatServiceMaxCents)
                 })
        {
            long Cumulative(DateOnly to)
            {
                var byKind = TurnoverByKind(new DateOnly(to.Year, 1, 1), to, out _, out _);
                return isService ? byKind.Where(kv => kv.Key.IsService()).Sum(kv => kv.Value) : byKind[UrssafKind.Sale];
            }

            // Année précédente : un dépassement du seuil de base rend la TVA exigible depuis le 1er janvier courant.
            var prevTotal = Cumulative(new DateOnly(year - 1, 12, 31));
            if (prevTotal > baseCents)
            {
                var from = new DateOnly(year, 1, 1);
                notices.Add(new VatNotice(activity, NoticeLevel.Alert,
                    $"{activity} : chiffre d'affaires {year - 1} ({Eur(prevTotal)}) au-dessus du seuil de franchise ({Eur(baseCents)}). " +
                    $"La TVA est exigible depuis le {from:dd/MM/yyyy} : facturez la TVA, retirez la mention « TVA non applicable, art. 293 B du CGI » " +
                    "de vos factures, demandez votre numéro de TVA au SIE et déposez vos déclarations de TVA.", from));
                continue;
            }

            // Année en cours : premier mois où le cumul dépasse le seuil majoré / le seuil de base.
            DateOnly? majorFrom = null, baseExceeded = null;
            for (var m = 1; m <= 12; m++)
            {
                var end = new DateOnly(year, m, DateTime.DaysInMonth(year, m));
                var total = Cumulative(end);
                if (majorFrom is null && total > maxCents) majorFrom = new DateOnly(year, m, 1);
                if (baseExceeded is null && total > baseCents) baseExceeded = end;
                if (majorFrom is not null) break;
            }

            if (majorFrom is { } mf)
                notices.Add(new VatNotice(activity, NoticeLevel.Alert,
                    $"{activity} : seuil majoré de franchise ({Eur(maxCents)}) dépassé en {mf.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("fr-FR"))}. " +
                    $"La TVA est exigible dès le {mf:dd/MM/yyyy} (1er jour du mois de dépassement) : facturez la TVA à partir de cette date, " +
                    "retirez la mention « TVA non applicable, art. 293 B du CGI », demandez votre numéro de TVA au SIE et déposez vos déclarations.", mf));
            else if (baseExceeded is not null)
                notices.Add(new VatNotice(activity, NoticeLevel.Warning,
                    $"{activity} : seuil de franchise ({Eur(baseCents)}) dépassé en {year}. Vous restez en franchise jusqu'au 31/12/{year} " +
                    $"(tant que le seuil majoré de {Eur(maxCents)} n'est pas dépassé) ; la TVA sera exigible à partir du 01/01/{year + 1}. " +
                    "Préparez dès maintenant votre numéro de TVA et vos factures.", new DateOnly(year + 1, 1, 1)));
            else
            {
                var total = Cumulative(new DateOnly(year, 12, 31));
                if (baseCents > 0 && (double)total / baseCents >= 0.9)
                    notices.Add(new VatNotice(activity, NoticeLevel.Info,
                        $"{activity} : {(double)total / baseCents:P0} du seuil de franchise de TVA ({Eur(baseCents)}) atteint en {year}.", null));
            }
        }
        return notices;
    }
}
