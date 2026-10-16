using Microsoft.EntityFrameworkCore;
using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Security;

namespace ToutCompteFait.Core;

public sealed partial class Compta
{
    /// <summary>Préfixe du repère « personnelle » d'un produit (recette) dans la table <c>category_personal</c>, dont les
    /// lignes sans préfixe sont les charges : un produit et une charge de même nom restent distincts, sans migration.</summary>
    private const string ProductKeyPrefix = "P:";

    private static string PersonalKey(CategoryKind kind, string name) => kind == CategoryKind.Product ? ProductKeyPrefix + name : name;

    /// <summary>Noms des charges « personnelles » (sans tenir compte de la casse).</summary>
    public HashSet<string> PersonalCategoryNames() => PersonalNames(CategoryKind.Charge);

    /// <summary>Noms des produits « personnels » (apports de l'exploitant, sans tenir compte de la casse).</summary>
    public HashSet<string> PersonalProductNames() => PersonalNames(CategoryKind.Product);

    private HashSet<string> PersonalNames(CategoryKind kind)
    {
        using var l = Open();
        var all = l.Ctx.CategoryPersonal.AsNoTracking().Select(c => c.Name).AsEnumerable();
        var names = kind == CategoryKind.Product
            ? all.Where(n => n.StartsWith(ProductKeyPrefix, StringComparison.Ordinal)).Select(n => n[ProductKeyPrefix.Length..])
            : all.Where(n => !n.StartsWith(ProductKeyPrefix, StringComparison.Ordinal));
        return names.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>Classe (ou déclasse) une charge en « personnelle » : dépense privée de l'exploitant passée par le compte de
    /// l'activité. Elle reste dans le livre et dans les soldes bancaires, mais sort du résultat ; le bilan la porte au
    /// passif en prélèvements de l'exploitant. Modifiable même sur un exercice validé (donnée de paramétrage).</summary>
    public void SetCategoryPersonal(string categoryName, bool personal) => SetCategoryPersonal(categoryName, personal, CategoryKind.Charge);

    /// <summary>Même repère pour un produit : somme personnelle de l'exploitant encaissée sur le compte de l'activité
    /// (apport). Hors résultat, hors chiffre d'affaires et hors livre des recettes ; portée au passif en apports.</summary>
    public void SetCategoryPersonal(string categoryName, bool personal, CategoryKind kind)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        var key = PersonalKey(kind, categoryName);
        using var tx = Begin();
        var ctx = Open().Ctx;
        var existing = ctx.CategoryPersonal.AsEnumerable()
            .FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.CurrentCultureIgnoreCase));
        if (personal && existing is null) ctx.CategoryPersonal.Add(new CategoryPersonalEntity { Name = key });
        else if (!personal && existing is not null) ctx.CategoryPersonal.Remove(existing);
        else { tx.Commit(); return; }
        // Un apport personnel n'est pas du chiffre d'affaires : il n'a pas de type de déclaration URSSAF.
        if (personal && kind == CategoryKind.Product)
        {
            var urssaf = ctx.CategoryUrssaf.AsEnumerable()
                .Where(c => string.Equals(c.Name, categoryName, StringComparison.CurrentCultureIgnoreCase)).ToList();
            ctx.CategoryUrssaf.RemoveRange(urssaf);
        }
        Save(ctx);
        if (kind == CategoryKind.Product)
            Log(ctx, "Produit classé en apport personnel", $"« {categoryName} » : {(personal ? "personnel (hors résultat)" : "recette de l'activité")}");
        else
            Log(ctx, "Charge classée en dépense personnelle", $"« {categoryName} » : {(personal ? "personnelle (hors résultat)" : "charge de l'activité")}");
        tx.Commit();
    }

    /// <summary>Un renommage de catégorie emporte son repère « personnelle » (clé = nom).</summary>
    private static void MoveCategoryPersonal(AppDbContext ctx, string oldName, string newName, CategoryKind kind = CategoryKind.Charge)
    {
        if (string.Equals(oldName, newName, StringComparison.CurrentCultureIgnoreCase)) return;
        var oldKey = PersonalKey(kind, oldName);
        var newKey = PersonalKey(kind, newName);
        var row = ctx.CategoryPersonal.AsEnumerable()
            .FirstOrDefault(c => string.Equals(c.Name, oldKey, StringComparison.CurrentCultureIgnoreCase));
        if (row is null) return;
        ctx.CategoryPersonal.Remove(row);
        if (!ctx.CategoryPersonal.AsEnumerable().Any(c => string.Equals(c.Name, newKey, StringComparison.CurrentCultureIgnoreCase)))
            ctx.CategoryPersonal.Add(new CategoryPersonalEntity { Name = newKey });
    }
}
