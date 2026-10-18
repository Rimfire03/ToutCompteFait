using ToutCompteFait.Core.Business;
using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Security;

namespace ToutCompteFait.Core.Services;

/// <summary>Dossier de démonstration : une base SQLite SÉPARÉE, remplie de données entièrement fictives (aucun nom, numéro
/// de compte ni montant réel), pour découvrir ou présenter le logiciel sans toucher aux vraies données. Jamais mélangée
/// aux données de l'utilisateur.</summary>
public static class DemoDatabase
{
    /// <summary>Identifiants du compte administrateur de démonstration (données fictives : aucun secret).</summary>
    public const string UserName = "demo";
    public const string Password = "Demo2026-test";

    /// <summary>Crée (ou recrée) la base de démonstration à <paramref name="path"/>. Le fichier existant est remplacé.</summary>
    public static void Create(string path, bool business, string appVersion, DateOnly today)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(f)) File.Delete(f);

        var factory = new DbContextFactory(path);
        factory.EnsureDatabaseUpToDate(appVersion);
        new AuthService(factory).CreateFirstAdmin(UserName, "Démonstration", Password);
        var c = new Compta(factory, Session.System);

        // Autoentreprise : année civile en cours. Association : exercice septembre → août dont la plus grande partie est déjà
        // écoulée (celui qui contenait la date d'il y a huit mois), pour que la démonstration soit bien remplie.
        var reference = today.AddMonths(-8);
        var start = business ? new DateOnly(today.Year, 1, 1) : new DateOnly(reference.Month >= 9 ? reference.Year : reference.Year - 1, 9, 1);
        var year = c.CreateYear(start, start.AddYears(1).AddDays(-1));
        var bank = c.Accounts().First(a => a.Name == "Banque");
        c.SetOpening(year.Id, bank.Id, business ? 150000 : 420000);

        if (business) FillBusiness(c, year, bank, today);
        else FillAssociation(c, year, bank, today);
    }

    private static DateOnly Clamp(DateOnly d, DateOnly max) => d > max ? max : d;

    private static void FillAssociation(Compta c, FiscalYear year, Account bank, DateOnly today)
    {
        var cats = c.Categories(year.Id);
        Category P(string n) => cats.First(k => k.Kind == CategoryKind.Product && k.Name == n);
        Category C(string n) => cats.First(k => k.Kind == CategoryKind.Charge && k.Name == n);
        void Add(DateOnly d, string label, long cents, Direction dir, Category? cat)
        {
            if (d > today) return;
            c.SaveEntry(new Entry(0, year.Id, d, label, true, EntryKind.Normal, bank.Id, dir, null, cents, null, null),
                cat is null ? Array.Empty<Allocation>() : new[] { new Allocation(cat.Id, cents) }, strict: cat is not null);
        }
        var s = year.Start;
        Add(s.AddDays(12), "Cotisations adhérents — rentrée", 86000, Direction.In, P("Adhésions"));
        Add(s.AddDays(40), "Loto d'automne — recettes", 214500, Direction.In, P("Loto Octobre"));
        Add(s.AddDays(41), "Loto d'automne — lots et salle", 98000, Direction.Out, C("Loto Octobre"));
        Add(s.AddDays(75), "Assurance association", 18400, Direction.Out, C("Assurance"));
        Add(s.AddDays(110), "Subvention communale", 150000, Direction.In, P("Subvention"));
        Add(s.AddDays(140), "Tombola de Noël — recettes", 63000, Direction.In, P("Tombola"));
        Add(s.AddDays(160), "Frais bancaires du trimestre", 1250, Direction.Out, C("Frais bancaires"));
        Add(s.AddDays(190), "Loto de mars — recettes", 187000, Direction.In, P("Loto mars"));
        Add(s.AddDays(191), "Loto de mars — lots et salle", 91500, Direction.Out, C("Loto mars"));
        Add(s.AddDays(225), "Achat de matériel de sonorisation", 42000, Direction.Out, C("Charges"));
        Add(s.AddDays(260), "Kermesse — recettes", 154000, Direction.In, P("Kermesse"));
        Add(s.AddDays(262), "Kermesse — achats", 38700, Direction.Out, C("Kermesse"));
        // Deux écritures laissées « à ventiler » pour montrer la saisie rapide.
        Add(s.AddDays(280), "VIR RECU DUPONT 0412", 12000, Direction.In, null);
        Add(s.AddDays(290), "CARTE 12/06 PAPETERIE", 3490, Direction.Out, null);
    }

    private static void FillBusiness(Compta c, FiscalYear year, Account bank, DateOnly today)
    {
        c.SaveBusinessProfile(new BusinessProfile
        {
            Name = "Atelier Démo (fictif)", Siren = "000000000", Mode = DeclarationMode.Quarterly, CreationDate = year.Start
        });
        c.ApplyCategoryTemplate(year.Id, CategoryTemplates.Find("prestation")!.Items);
        c.CompleteBusinessOnboarding(year.Id, null, DeclarationMode.Quarterly, useAssets: false);
        var cats = c.Categories(year.Id);
        Category P(string n) => cats.First(k => k.Kind == CategoryKind.Product && k.Name == n);
        Category C(string n) => cats.First(k => k.Kind == CategoryKind.Charge && k.Name == n);
        void Add(DateOnly d, string label, long cents, Direction dir, Category? cat)
        {
            if (d > today) return;
            c.SaveEntry(new Entry(0, year.Id, d, label, true, EntryKind.Normal, bank.Id, dir, null, cents, null, null),
                cat is null ? Array.Empty<Allocation>() : new[] { new Allocation(cat.Id, cents) }, strict: cat is not null);
        }
        var sales = P("Prestations de services");
        var s = year.Start;
        for (var m = 0; m < 12; m++)
        {
            var d = s.AddMonths(m);
            Add(d.AddDays(4), "VIR CLIENT ALPHA — mission " + (m + 1), 120000 + (m % 4) * 15000, Direction.In, sales);
            Add(d.AddDays(14), "VIR CLIENT BETA — facture " + (m + 1), 65000 + (m % 3) * 9000, Direction.In, sales);
            Add(d.AddDays(7), "ABONNEMENT LOGICIELS", 4900, Direction.Out, C("Logiciels et abonnements"));
            Add(d.AddDays(9), "TELEPHONE ET INTERNET", 3990, Direction.Out, C("Téléphone et internet"));
            if (m % 3 == 0) Add(d.AddDays(20), "ASSURANCE PROFESSIONNELLE", 21000, Direction.Out, C("Assurance professionnelle"));
            if (m % 2 == 1) Add(d.AddDays(18), "DEPLACEMENT CLIENT", 8600, Direction.Out, C("Déplacements"));
        }
        // Une écriture laissée « à ventiler » pour montrer la saisie rapide.
        Add(today.AddDays(-3), "CARTE PAPETERIE CENTRE", 2790, Direction.Out, null);

        // Premier trimestre déclaré, avec un petit écart entre le calculé et le déclaré.
        var q1 = UrssafPeriod.Quarterly(year.Start.Year, 1);
        if (today > q1.End)
            c.MarkUrssafDeclared(q1, "Déclaration de démonstration", c.ComputeUrssafReport(q1).TotalTurnoverCents);
    }
}
