using ToutCompteFait.Core.Import;
using ToutCompteFait.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

/// <summary>Contrôle d'un relevé avant import : cohérence du relevé lui-même, comparaison avec le solde du compte dans le
/// logiciel, et solde d'ouverture qui sera fixé s'il s'agit du premier import du compte.</summary>
public sealed record StatementPrecheck(string Note, bool Inconsistent, bool WillSetOpening);

/// <summary>Résultat après import : solde d'ouverture fixé, et comparaison du solde du compte avec celui du relevé.</summary>
public sealed record StatementBalanceReport(string Text, bool Consistent);

public sealed partial class Compta
{
    /// <summary>Solde d'un compte à la fin d'une journée (ouverture de l'exercice + mouvements jusqu'à cette date incluse) ;
    /// null si la date ne tombe dans aucun exercice.</summary>
    public long? AccountBalanceAt(long accountId, DateOnly date)
    {
        var year = Years().FirstOrDefault(y => y.Contains(date));
        if (year is null)
        {
            // Veille du début d'un exercice : c'est son solde d'ouverture.
            var next = Years().FirstOrDefault(y => y.Start == date.AddDays(1));
            return next is null ? null : Opening(next.Id, accountId);
        }
        long movement = 0;
        using (var l = Open())
        {
            var limit = D(date);
            var rows = l.Ctx.Entries.AsNoTracking().Where(e => e.YearId == year.Id && string.Compare(e.EntryDate, limit) <= 0)
                .Select(e => new { e.Kind, e.AccountId, e.ToAccountId, e.Direction, e.AmountCents }).ToList();
            foreach (var e in rows)
            {
                if (e.Kind == "TRANSFER")
                {
                    if (e.AccountId == accountId) movement -= e.AmountCents;
                    if (e.ToAccountId == accountId) movement += e.AmountCents;
                }
                else if (e.AccountId == accountId) movement += e.Direction == "IN" ? e.AmountCents : -e.AmountCents;
            }
        }
        return Opening(year.Id, accountId) + movement;
    }

    /// <summary>Premier import d'un compte : aucune écriture ne le concerne encore (saisie ou importée).</summary>
    public bool IsFirstImport(long accountId)
    {
        using var l = Open();
        return !l.Ctx.Entries.AsNoTracking().Any(e => e.AccountId == accountId || e.ToAccountId == accountId);
    }

    /// <summary>Exercice dont le solde d'ouverture peut être fixé d'après le relevé : celui qui contient le début de la
    /// période, sans exercice précédent (sinon l'ouverture est reprise automatiquement) et encore ouvert.</summary>
    private FiscalYear? OpeningYearFor(DateOnly start)
    {
        var y = Years().FirstOrDefault(x => x.Contains(start));
        return y is not null && y.Open && !OpeningIsAutomatic(y.Id) ? y : null;
    }

    public StatementPrecheck PrecheckStatement(long accountId, IReadOnlyList<BankLine> lines, StatementBalances balances)
    {
        var notes = new List<string>();
        var inconsistent = false;
        var willSet = false;
        if (balances.IsEmpty)
            return new StatementPrecheck("Ce fichier n'indique pas les soldes du relevé : aucun contrôle de solde possible.", false, false);

        var sum = lines.Sum(l => l.AmountCents);
        if (balances is { Opening: { } o, Closing: { } c })
        {
            if (o + sum == c)
                notes.Add($"✔ Relevé cohérent : solde {Money.Format(o)} € {(sum >= 0 ? "+" : "−")} mouvements {Money.Format(Math.Abs(sum))} € = {Money.Format(c)} €.");
            else
            {
                inconsistent = true;
                notes.Add($"⚠ Relevé incohérent : solde d'ouverture {Money.Format(o)} € + mouvements {Money.Format(sum)} € = {Money.Format(o + sum)} €, " +
                          $"alors que le relevé indique {Money.Format(c)} € (écart {Money.Format(c - o - sum)} €). Des lignes manquent peut-être ou sont mal lues.");
            }
        }

        // Solde d'ouverture : à fixer (premier import) ou à comparer avec le solde du compte dans le logiciel.
        var opening = balances.Opening ?? (balances.Closing is { } cl ? cl - sum : null);
        if (opening is { } open && balances.Start is { } start)
        {
            if (IsFirstImport(accountId))
            {
                if (OpeningYearFor(start) is { } y)
                {
                    willSet = true;
                    notes.Add($"Premier import de ce compte : le solde d'ouverture de l'exercice {y.Label} sera fixé à {Money.Format(open)} € (solde du relevé au {start.AddDays(-1):dd/MM/yyyy}).");
                }
                else
                    notes.Add("Premier import de ce compte, mais son solde d'ouverture ne peut pas être fixé ici (exercice précédent, exercice validé ou date hors exercice).");
            }
            else if (AccountBalanceAt(accountId, start.AddDays(-1)) is { } ledger && ledger != open)
            {
                inconsistent = true;
                notes.Add($"⚠ Solde du compte dans le logiciel au {start.AddDays(-1):dd/MM/yyyy} : {Money.Format(ledger)} €, " +
                          $"alors que le relevé part de {Money.Format(open)} € (écart {Money.Format(open - ledger)} €). " +
                          "Vérifiez les écritures antérieures ou le solde d'ouverture.");
            }
        }
        return new StatementPrecheck(string.Join("\n", notes), inconsistent, willSet);
    }

    /// <summary>À appeler après l'import réussi : fixe le solde d'ouverture si c'était le premier import du compte, puis
    /// compare le solde du compte dans le logiciel avec le solde de clôture du relevé.</summary>
    public StatementBalanceReport FinishStatementImport(long accountId, IReadOnlyList<BankLine> lines, StatementBalances balances,
        bool wasFirstImport)
    {
        var parts = new List<string>();
        var consistent = true;
        var sum = lines.Sum(l => l.AmountCents);
        var opening = balances.Opening ?? (balances.Closing is { } cl ? cl - sum : null);

        if (wasFirstImport && opening is { } open && balances.Start is { } start && OpeningYearFor(start) is { } y)
        {
            SetOpening(y.Id, accountId, open);
            parts.Add($"Solde d'ouverture de l'exercice {y.Label} fixé à {Money.Format(open)} € d'après le relevé.");
        }
        if (balances.Closing is { } closing && balances.End is { } end)
        {
            var ledger = AccountBalanceAt(accountId, end);
            if (ledger is { } value)
            {
                consistent = value == closing;
                parts.Add(consistent
                    ? $"✔ Le solde du compte au {end:dd/MM/yyyy} correspond au relevé : {Money.Format(closing)} €."
                    : $"⚠ Solde du compte au {end:dd/MM/yyyy} : {Money.Format(value)} € dans le logiciel, {Money.Format(closing)} € sur le relevé " +
                      $"(écart {Money.Format(closing - value)} €). Des écritures saisies à la main ou des lignes d'autres relevés sont peut-être manquantes.");
            }
        }
        return new StatementBalanceReport(string.Join("\n", parts), consistent);
    }
}
