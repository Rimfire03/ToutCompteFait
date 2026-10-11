namespace ToutCompteFait.Core.Services;

/// <summary>Calcul des échéances de la sauvegarde automatique (quotidienne, hebdomadaire ou mensuelle).</summary>
public static class BackupSchedule
{
    public const string Daily = "Daily";
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";

    public static TimeSpan TimeOfDay(AppSettings s) =>
        TimeSpan.TryParseExact(s.AutoBackupTime, @"hh\:mm", null, out var t) ? t : TimeSpan.FromHours(2);

    /// <summary>Dernière échéance planifiée située avant (ou à) <paramref name="now"/>.</summary>
    public static DateTime LastDue(AppSettings s, DateTime now)
    {
        var time = TimeOfDay(s);
        switch (s.AutoBackupFrequency)
        {
            case Weekly:
            {
                var d = now.Date + time;
                if (d > now) d = d.AddDays(-1);
                while ((int)d.DayOfWeek != s.AutoBackupDayOfWeek) d = d.AddDays(-1);
                return d;
            }
            case Monthly:
            {
                var d = InMonth(now.Year, now.Month, s.AutoBackupDayOfMonth) + time;
                if (d > now)
                {
                    var prev = now.AddMonths(-1);
                    d = InMonth(prev.Year, prev.Month, s.AutoBackupDayOfMonth) + time;
                }
                return d;
            }
            default:
            {
                var d = now.Date + time;
                return d > now ? d.AddDays(-1) : d;
            }
        }
    }

    /// <summary>Première échéance strictement après <paramref name="now"/>.</summary>
    public static DateTime Next(AppSettings s, DateTime now)
    {
        var last = LastDue(s, now);
        var time = TimeOfDay(s);
        return s.AutoBackupFrequency switch
        {
            Weekly => last.AddDays(7),
            Monthly => InMonth(last.AddMonths(1).Year, last.AddMonths(1).Month, s.AutoBackupDayOfMonth) + time,
            _ => last.AddDays(1)
        };
    }

    /// <summary>Une sauvegarde est due si une échéance est passée depuis la dernière exécution (rattrape aussi
    /// une échéance manquée pendant que le logiciel était fermé).</summary>
    public static bool IsDue(AppSettings s, DateTime now) =>
        s.AutoBackupEnabled && (s.AutoBackupLastRun ?? DateTime.MinValue) < LastDue(s, now);

    private static DateTime InMonth(int year, int month, int day) =>
        new(year, month, Math.Clamp(day, 1, DateTime.DaysInMonth(year, month)));
}

