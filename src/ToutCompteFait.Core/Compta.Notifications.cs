namespace ToutCompteFait.Core;

public enum NotificationLevel { Info, Warning, Alert }

/// <summary>Alerte du centre de notifications : titre, détail, gravité et page vers laquelle l'ouvrir.</summary>
/// <param name="Target">Étiquette de la page à ouvrir (« livre », « bilan », « reporting »…).</param>
public sealed record AppNotification(string Id, NotificationLevel Level, string Title, string Detail, string Target);

public sealed partial class Compta
{
    /// <summary>Alertes en cours, de la plus grave à la moins grave : déclarations URSSAF à faire (Autoentreprise),
    /// écritures à ventiler de l'exercice affiché, exercice terminé non clôturé. Lecture seule, sans effet de bord.</summary>
    public List<AppNotification> BuildNotifications(DateOnly today, long? yearId)
    {
        var list = new List<AppNotification>();

        if (Editions.Current.IsBusiness)
        {
            foreach (var p in PendingUrssafDeclarations(today))
            {
                var late = today > p.DueDate;
                var days = Math.Abs(p.DueDate.DayNumber - today.DayNumber);
                list.Add(new AppNotification("urssaf:" + p.Key, late ? NotificationLevel.Alert : NotificationLevel.Warning,
                    $"Déclaration URSSAF à faire : {p.Label}",
                    late ? $"En retard de {days} jour(s) : à déclarer avant le {p.DueDate:dd/MM/yyyy}."
                         : $"À déclarer avant le {p.DueDate:dd/MM/yyyy} (J-{days}).",
                    "reporting"));
            }
        }

        if (yearId is { } id && Year(id) is { } year)
        {
            var toFill = Entries(id).Count(r => NeedsAllocation(r) && !r.Entry.Frozen);
            if (toFill > 0)
                list.Add(new AppNotification("fill:" + id, NotificationLevel.Warning,
                    $"{toFill} écriture(s) à ventiler",
                    "Classez-les en produit ou en charge : elles empêchent la validation de l'exercice et faussent les statistiques.",
                    "livre"));
            if (!year.Validated && year.End < today)
                list.Add(new AppNotification("close:" + id, NotificationLevel.Info,
                    $"Exercice {year.Label} terminé, non clôturé",
                    $"La période s'est terminée le {year.End:dd/MM/yyyy} : vérifiez le bilan puis validez l'exercice.",
                    "bilan"));
        }
        return list.OrderByDescending(n => n.Level).ThenBy(n => n.Title, StringComparer.CurrentCulture).ToList();
    }
}
