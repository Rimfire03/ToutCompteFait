using Microsoft.EntityFrameworkCore;
using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Security;

namespace ToutCompteFait.Core;

public sealed partial class Compta
{
    /// <summary>Noms des charges classées « investissement » (sans tenir compte de la casse).</summary>
    public HashSet<string> InvestmentCategoryNames()
    {
        using var l = Open();
        return l.Ctx.CategoryInvestments.AsNoTracking().Select(c => c.Name).AsEnumerable()
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>Classe (ou déclasse) une charge en « investissement ». Simple repère : la charge reste dans le résultat ; le
    /// bilan précise la part d''investissement. Modifiable même sur un exercice validé (donnée de paramétrage).</summary>
    public void SetCategoryInvestment(string categoryName, bool investment)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        using var tx = Begin();
        var ctx = Open().Ctx;
        var existing = ctx.CategoryInvestments.AsEnumerable()
            .FirstOrDefault(c => string.Equals(c.Name, categoryName, StringComparison.CurrentCultureIgnoreCase));
        if (investment && existing is null) ctx.CategoryInvestments.Add(new CategoryInvestmentEntity { Name = categoryName });
        else if (!investment && existing is not null) ctx.CategoryInvestments.Remove(existing);
        else { tx.Commit(); return; }
        Save(ctx);
        Log(ctx, "Charge classée en investissement", $"« {categoryName} » : {(investment ? "investissement" : "charge courante")}");
        tx.Commit();
    }

    /// <summary>Un renommage de catégorie emporte son repère « investissement » (clé = nom).</summary>
    private static void MoveCategoryInvestment(AppDbContext ctx, string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.CurrentCultureIgnoreCase)) return;
        var row = ctx.CategoryInvestments.AsEnumerable()
            .FirstOrDefault(c => string.Equals(c.Name, oldName, StringComparison.CurrentCultureIgnoreCase));
        if (row is null) return;
        ctx.CategoryInvestments.Remove(row);
        if (!ctx.CategoryInvestments.AsEnumerable().Any(c => string.Equals(c.Name, newName, StringComparison.CurrentCultureIgnoreCase)))
            ctx.CategoryInvestments.Add(new CategoryInvestmentEntity { Name = newName });
    }
}