using ComptaAepby.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ComptaAepby.Core.Data;

/// <summary>Déclencheurs SQLite : seconde barrière (derrière <see cref="YearLockGuard"/>) qui interdit toute
/// modification d'un exercice validé, même par un autre outil ouvrant le fichier. Idempotent
/// (<c>CREATE TRIGGER IF NOT EXISTS</c>) : rappelé à chaque ouverture par le poste rédacteur.</summary>
public static class SqliteTriggers
{
    private const string Abort = "SELECT RAISE(ABORT, 'Exercice validé : modification impossible');";

    public static void EnsureYearLocks(AppDbContext ctx)
    {
        if (ctx.Provider != DatabaseProvider.Sqlite) return;

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

        // Exercice : ni suppression ni modification d'un exercice validé (la réouverture VALIDATED -> OPEN passe
        // par ReopenYear, tracée dans le journal).
        Exec(ctx, $"""
            CREATE TRIGGER IF NOT EXISTS lock_years_del BEFORE DELETE ON fiscal_years
            WHEN OLD.status = 'VALIDATED'
            BEGIN SELECT RAISE(ABORT, 'Exercice validé : suppression impossible'); END;
            """);
        Exec(ctx, $"""
            CREATE TRIGGER IF NOT EXISTS lock_years_upd BEFORE UPDATE ON fiscal_years
            WHEN OLD.status = 'VALIDATED' AND NEW.status = 'VALIDATED'
            BEGIN {Abort} END;
            """);
    }

    private static void Exec(AppDbContext ctx, string sql) => ctx.Database.ExecuteSqlRaw(sql);
}
