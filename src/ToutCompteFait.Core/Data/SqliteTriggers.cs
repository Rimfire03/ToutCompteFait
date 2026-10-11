using ToutCompteFait.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core.Data;

/// <summary>Déclencheurs SQLite : seconde barrière (derrière <see cref="YearLockGuard"/>) qui interdit toute
/// modification d'un exercice validé, la suppression d'une écriture et toute altération du journal d'audit, même par
/// un autre outil ouvrant le fichier.
/// <para>ÉVOLUTION DU SCHÉMA : ces déclencheurs sont du code, pas de la donnée. Ils sont supprimés avant toute migration
/// (<see cref="Drop"/>) pour que celle-ci puisse corriger ou compléter des données, y compris celles des exercices
/// validés, puis recréés à chaque ouverture par le poste rédacteur (<see cref="EnsureYearLocks"/> : suppression puis
/// création, donc une définition améliorée dans une nouvelle version remplace l'ancienne). Une migration ne doit jamais
/// changer un montant, une date, un libellé ou une ventilation d'un exercice validé : l'empreinte
/// (<see cref="Compta.ComputeChecksum"/>) le révélerait.</para></summary>
public static class SqliteTriggers
{
    private const string Abort = "SELECT RAISE(ABORT, 'Exercice validé : modification impossible');";

    /// <summary>Préfixes des déclencheurs gérés ici (les autres, comme le suivi de dernière saisie, sont laissés).</summary>
    private static readonly string[] Prefixes = { "lock_", "audit_no_", "entries_no_" };

    /// <summary>Supprime tous les déclencheurs de verrouillage (avant une migration ou un vidage volontaire).</summary>
    public static void Drop(AppDbContext ctx)
    {
        if (ctx.Provider != DatabaseProvider.Sqlite) return;
        var names = ctx.Database.SqlQueryRaw<string>("SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'trigger'")
            .AsEnumerable().Where(n => Prefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal))).ToList();
        foreach (var n in names) Exec(ctx, $"DROP TRIGGER IF EXISTS \"{n}\";");
    }

    public static void EnsureYearLocks(AppDbContext ctx)
    {
        if (ctx.Provider != DatabaseProvider.Sqlite) return;
        Drop(ctx);

        // Tables portant directement year_id.
        foreach (var table in new[] { "entries", "categories", "operations", "adjustments", "year_openings" })
        {
            Exec(ctx, $"""
                CREATE TRIGGER IF NOT EXISTS lock_{table}_ins BEFORE INSERT ON {table}
                WHEN (SELECT status FROM fiscal_years WHERE id = NEW.year_id) = 'VALIDATED'
                BEGIN {Abort} END;
                """);
            Exec(ctx, $"""
                CREATE TRIGGER IF NOT EXISTS lock_{table}_upd BEFORE UPDATE ON {table}
                WHEN (SELECT status FROM fiscal_years WHERE id = OLD.year_id) = 'VALIDATED'
                  OR (SELECT status FROM fiscal_years WHERE id = NEW.year_id) = 'VALIDATED'
                BEGIN {Abort} END;
                """);
            Exec(ctx, $"""
                CREATE TRIGGER IF NOT EXISTS lock_{table}_del BEFORE DELETE ON {table}
                WHEN (SELECT status FROM fiscal_years WHERE id = OLD.year_id) = 'VALIDATED'
                BEGIN {Abort} END;
                """);
        }

        // Ventilations : verrouillées via l'écriture parente.
        Exec(ctx, $"""
            CREATE TRIGGER IF NOT EXISTS lock_allocations_ins BEFORE INSERT ON allocations
            WHEN (SELECT y.status FROM entries e JOIN fiscal_years y ON y.id = e.year_id WHERE e.id = NEW.entry_id) = 'VALIDATED'
            BEGIN {Abort} END;
            """);
        Exec(ctx, $"""
            CREATE TRIGGER IF NOT EXISTS lock_allocations_upd BEFORE UPDATE ON allocations
            WHEN (SELECT y.status FROM entries e JOIN fiscal_years y ON y.id = e.year_id WHERE e.id = OLD.entry_id) = 'VALIDATED'
              OR (SELECT y.status FROM entries e JOIN fiscal_years y ON y.id = e.year_id WHERE e.id = NEW.entry_id) = 'VALIDATED'
            BEGIN {Abort} END;
            """);
        Exec(ctx, $"""
            CREATE TRIGGER IF NOT EXISTS lock_allocations_del BEFORE DELETE ON allocations
            WHEN (SELECT y.status FROM entries e JOIN fiscal_years y ON y.id = e.year_id WHERE e.id = OLD.entry_id) = 'VALIDATED'
            BEGIN {Abort} END;
            """);

        // Justificatifs : verrouillés via l'écriture parente (même règle que les ventilations).
        Exec(ctx, $"""
            CREATE TRIGGER IF NOT EXISTS lock_attachments_ins BEFORE INSERT ON attachments
            WHEN (SELECT y.status FROM entries e JOIN fiscal_years y ON y.id = e.year_id WHERE e.id = NEW.entry_id) = 'VALIDATED'
            BEGIN {Abort} END;
            """);
        Exec(ctx, $"""
            CREATE TRIGGER IF NOT EXISTS lock_attachments_upd BEFORE UPDATE ON attachments
            WHEN (SELECT y.status FROM entries e JOIN fiscal_years y ON y.id = e.year_id WHERE e.id = OLD.entry_id) = 'VALIDATED'
            BEGIN {Abort} END;
            """);
        Exec(ctx, $"""
            CREATE TRIGGER IF NOT EXISTS lock_attachments_del BEFORE DELETE ON attachments
            WHEN (SELECT y.status FROM entries e JOIN fiscal_years y ON y.id = e.year_id WHERE e.id = OLD.entry_id) = 'VALIDATED'
            BEGIN {Abort} END;
            """);

        // Exercice : ni suppression, ni modification d'un exercice validé (la réouverture VALIDATED -> OPEN passe par
        // ReopenYear, tracée dans le journal). Seule l'empreinte peut être réécrite (changement de version de la
        // formule, voir Compta.UpgradeIntegritySeals) : le libellé, la période, la clôture restent figés.
        Exec(ctx, $"""
            CREATE TRIGGER IF NOT EXISTS lock_years_del BEFORE DELETE ON fiscal_years
            WHEN OLD.status = 'VALIDATED'
            BEGIN SELECT RAISE(ABORT, 'Exercice validé : suppression impossible'); END;
            """);
        Exec(ctx, $"""
            CREATE TRIGGER IF NOT EXISTS lock_years_upd BEFORE UPDATE ON fiscal_years
            WHEN OLD.status = 'VALIDATED' AND NEW.status = 'VALIDATED'
              AND (NEW.label IS NOT OLD.label OR NEW.start_date IS NOT OLD.start_date OR NEW.end_date IS NOT OLD.end_date
                   OR NEW.validated_at IS NOT OLD.validated_at OR NEW.validated_by IS NOT OLD.validated_by
                   OR NEW.closing_json IS NOT OLD.closing_json)
            BEGIN {Abort} END;
            """);

        // Écritures : jamais supprimées (annulées par contre-passation). Le vidage volontaire de la base supprime ce
        // déclencheur le temps de l'opération (Compta.ResetDatabaseData).
        Exec(ctx, """
            CREATE TRIGGER IF NOT EXISTS entries_no_delete BEFORE DELETE ON entries
            BEGIN SELECT RAISE(ABORT, 'Une écriture ne se supprime pas : elle s''annule par contre-passation'); END;
            """);

        // Journal d'audit : ajout seulement. Une ligne ne peut passer de « sans empreinte » (avant le chaînage) à
        // « empreintée » qu'une fois, sans changer son contenu.
        Exec(ctx, """
            CREATE TRIGGER IF NOT EXISTS audit_no_update BEFORE UPDATE ON audit_log
            WHEN OLD.hash IS NOT NULL OR NEW.at IS NOT OLD.at OR NEW.user_name IS NOT OLD.user_name
              OR NEW.action IS NOT OLD.action OR NEW.detail IS NOT OLD.detail
            BEGIN SELECT RAISE(ABORT, 'Le journal d''audit ne peut pas être modifié'); END;
            """);
        Exec(ctx, """
            CREATE TRIGGER IF NOT EXISTS audit_no_delete BEFORE DELETE ON audit_log
            BEGIN SELECT RAISE(ABORT, 'Le journal d''audit ne peut pas être purgé'); END;
            """);
    }

    private static void Exec(AppDbContext ctx, string sql) => ctx.Database.ExecuteSqlRaw(sql);
}
