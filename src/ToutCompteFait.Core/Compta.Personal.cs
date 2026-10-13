using Microsoft.EntityFrameworkCore;
using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Security;

namespace ToutCompteFait.Core;

public sealed partial class Compta
{
    /// <summary>Noms des charges « personnelles » (sans tenir compte de la casse).</summary>
    public HashSet<string> PersonalCategoryNames()
    {
        using var l = Open();
        return l.Ctx.CategoryPersonal.AsNoTracking().Select(c => c.Name).AsEnumerable()
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>Classe (ou déclasse) une charge en « personnelle » : dépense privée de l'exploitant passée par le compte de
    /// l'activité. Elle reste dans le livre et dans les soldes bancaires, mais sort du résultat ; le bilan la porte au
    /// passif en prélèvements de l'exploitant. Modifiable même sur un exercice validé (donnée de paramétrage).</summary>
    public void SetCategoryPersonal(string categoryName, bool personal)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        using var tx = Begin();
        var ctx = Open().Ctx;
        var existing = ctx.CategoryPersonal.AsEnumerable()
            .FirstOrDefault(c => string.Equals(c.Name, categoryName, StringComparison.CurrentCultureIgnoreCase));
        if (personal && existing is null) ctx.CategoryPersonal.Add(new CategoryPersonalEntity { Name = categoryName });
        else if (!personal && existing is not null) ctx.CategoryPersonal.Remove(existing);
        else { tx.Commit(); return; }
        Save(ctx);
        Log(ctx, "Charge classée en dépense personnelle", $"« {categoryName} » : {(personal ? "personnelle (hors résultat)" : "charge de l'activité")}");
        tx.Commit();
    }

    /// <summary>Un renommage de catégorie emporte son repère « personnelle » (clé = nom).</summary>
    private static void MoveCategoryPersonal(AppDbContext ctx, string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.CurrentCultureIgnoreCase)) return;
        var row = ctx.CategoryPersonal.AsEnumerable()
            .FirstOrDefault(c => string.Equals(c.Name, oldName, StringComparison.CurrentCultureIgnoreCase));
        if (row is null) return;
        ctx.CategoryPersonal.Remove(row);
        if (!ctx.CategoryPersonal.AsEnumerable().Any(c => string.Equals(c.Name, newName, StringComparison.CurrentCultureIgnoreCase)))
            ctx.CategoryPersonal.Add(new CategoryPersonalEntity { Name = newName });
    }
}
