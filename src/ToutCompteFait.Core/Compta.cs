using System.Globalization;
using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Security;
using ToutCompteFait.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ToutCompteFait.Core;

/// <summary>Erreur métier : message affichable tel quel à l'utilisateur.</summary>
public sealed class ComptaException : Exception
{
    public ComptaException(string message) : base(message) { }
}

/// <summary>Cœur comptable : toutes les règles (exercices, écritures, régularisations, bilan, utilisateurs) vivent ici,
/// jamais dans l'interface. Montants en centimes entiers. Chaque opération vérifie le rôle de l'utilisateur de la
/// session (<see cref="Session"/>) et l'état de la base (lecture seule).</summary>
public sealed partial class Compta
{
    private readonly DbContextFactory _factory;

    public Session Session { get; }
    public string UserName => Session.UserName;

    public Compta(DbContextFactory factory, Session session)
    {
        _factory = factory;
        Session = session;
    }

    public DbContextFactory Factory => _factory;

    /// <summary>La base est ouverte en lecture seule (un autre poste détient l'écriture).</summary>
    public bool ReadOnly => _factory.ReadOnly;

    // ------------------------------------------------------------------ droits

    public bool Can(Permission p) => Session.Can(p);

    internal void Require(Permission p)
    {
        if (!Session.Can(p))
            throw new ComptaException($"Votre rôle ({RoleInfo.DisplayName(Session.Role)}) ne permet pas cette action.");
    }

    private void RequireWritable()
    {
        if (_factory.ReadOnly)
            throw new ComptaException("Ce poste est en lecture seule : un autre utilisateur détient l'accès en écriture.");
    }

    /// <summary>Droit + base inscriptible + exercice ouvert.</summary>
    internal void RequireOpen(long yearId, Permission p = Permission.WriteLedger)
    {
        Require(p);
        RequireWritable();
        var y = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        if (y.Validated) throw new ComptaException($"L'exercice {y.Label} est validé : aucune modification n'est possible.");
    }

    // ------------------------------------------------------------------ accès aux données et transactions

    private AppDbContext? _ambient;

    private readonly struct Lease : IDisposable
    {
        private readonly bool _own;
        public AppDbContext Ctx { get; }
        public Lease(AppDbContext ctx, bool own) { Ctx = ctx; _own = own; }
        public void Dispose() { if (_own) Ctx.Dispose(); }
    }

    /// <summary>Contexte de la transaction en cours, ou un contexte neuf (libéré à la fin).</summary>
    private Lease Open() => _ambient is not null ? new Lease(_ambient, false) : new Lease(_factory.Create(), true);

    /// <summary>Enregistre et oublie les entités suivies (le contexte peut être partagé dans une transaction).</summary>
    private static void Save(AppDbContext ctx)
    {
        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();
    }

    public interface IDbScope : IDisposable { void Commit(); }

    /// <summary>Transaction « imbriquable » : la première ouverte est réelle, les suivantes se rattachent à elle.
    /// Sans Commit() explicite, tout est annulé à la fermeture.</summary>
    public IDbScope Transaction() => Begin();

    private Scope Begin()
    {
        if (_ambient is not null) return new Scope(this, null, null);
        var ctx = _factory.Create();
        var tx = ctx.Database.BeginTransaction();
        _ambient = ctx;
        return new Scope(this, ctx, tx);
    }

    private sealed class Scope : IDbScope
    {
        private readonly Compta _c;
        private readonly AppDbContext? _ctx;
        private readonly IDbContextTransaction? _tx;
        private bool _done;

        public Scope(Compta c, AppDbContext? ctx, IDbContextTransaction? tx) { _c = c; _ctx = ctx; _tx = tx; }

        public void Commit()
        {
            if (_tx is null || _done) return;
            _tx.Commit();
            _done = true;
        }

        public void Dispose()
        {
            if (_tx is null) return;
            if (!_done) { try { _tx.Rollback(); } catch { /* connexion déjà fermée */ } }
            _tx.Dispose();
            _ctx!.Dispose();
            _c._ambient = null;
        }
    }

    // ------------------------------------------------------------------ utilitaires

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly ParseDate(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Now() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    private static DateTime ParseStamp(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture);

    private void Log(AppDbContext ctx, string action, string? detail = null)
    {
        ctx.Audit.Add(new AuditEntity { At = Now(), UserName = UserName, Action = action, Detail = detail });
        Save(ctx);
    }

    public List<(DateTime At, string User, string Action, string? Detail)> AuditLog(int max = 500)
    {
        using var l = Open();
        return l.Ctx.Audit.AsNoTracking().OrderByDescending(a => a.Id).Take(max).AsEnumerable()
            .Select(a => (ParseStamp(a.At), a.UserName, a.Action, a.Detail)).ToList();
    }

    private static string AccountKindToDb(AccountKind k) => k switch
    {
        AccountKind.Bank => "BANK", AccountKind.Savings => "SAVINGS", AccountKind.Cash => "CASH", _ => "OTHER"
    };

    private static AccountKind AccountKindFromDb(string s) => s switch
    {
        "BANK" => AccountKind.Bank, "SAVINGS" => AccountKind.Savings, "CASH" => AccountKind.Cash, _ => AccountKind.Other
    };

    private static string CatKindToDb(CategoryKind k) => k == CategoryKind.Product ? "P" : "C";
    private static CategoryKind CatKindFromDb(string s) => s == "P" ? CategoryKind.Product : CategoryKind.Charge;

    private static string AdjToDb(AdjustmentType t) => t switch
    {
        AdjustmentType.FromPrev => "FROM_PREV", AdjustmentType.ToNext => "TO_NEXT",
        AdjustmentType.ForPast => "FOR_PAST", _ => "DUE"
    };

    private static AdjustmentType AdjFromDb(string s) => s switch
    {
        "FROM_PREV" => AdjustmentType.FromPrev, "TO_NEXT" => AdjustmentType.ToNext,
        "FOR_PAST" => AdjustmentType.ForPast, _ => AdjustmentType.Due
    };

    private static string EntryKindToDb(EntryKind k) => k switch
    {
        EntryKind.Normal => "NORMAL", EntryKind.Transfer => "TRANSFER", EntryKind.NoCash => "NO_CASH", _ => "MEMBER_REFUND"
    };

    private static EntryKind EntryKindFromDb(string s) => s switch
    {
        "NORMAL" => EntryKind.Normal, "TRANSFER" => EntryKind.Transfer, "NO_CASH" => EntryKind.NoCash, _ => EntryKind.MemberRefund
    };

    private static Account ToAccount(AccountEntity a) => new(a.Id, a.Name, AccountKindFromDb(a.Kind), a.Position, a.Active, a.StatementMatch);

    private static FiscalYear ToYear(YearEntity y) => new(y.Id, y.Label, ParseDate(y.StartDate), ParseDate(y.EndDate),
        y.Status == "VALIDATED", y.ValidatedAt is null ? null : ParseStamp(y.ValidatedAt), y.ValidatedBy);

    private static Operation ToOperation(OperationEntity o) => new(o.Id, o.YearId, o.Name, o.Position);

    private static Category ToCategory(CategoryEntity c) => new(c.Id, c.YearId, CatKindFromDb(c.Kind), c.Name, c.Position, c.OperationId);

    private static Entry ToEntry(EntryEntity e) => new(e.Id, e.YearId, ParseDate(e.EntryDate), e.Label, e.Pointed,
        EntryKindFromDb(e.Kind), e.AccountId, e.Direction == "IN" ? Direction.In : Direction.Out, e.ToAccountId,
        e.AmountCents, e.MemberName, e.Note, e.RefundOfId);

    private static Adjustment ToAdjustment(AdjustmentEntity a) => new(a.Id, a.YearId, AdjFromDb(a.Type), CatKindFromDb(a.Kind),
        a.CategoryId, a.Label, a.AmountCents, a.TargetName, a.SourceId, a.Note);

    private static bool SameName(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);

    // ------------------------------------------------------------------ comptes

    public List<Account> Accounts(bool includeInactive = false)
    {
        using var l = Open();
        IQueryable<AccountEntity> q = l.Ctx.Accounts.AsNoTracking();
        if (!includeInactive) q = q.Where(a => a.Active);
        return q.OrderBy(a => a.Position).ThenBy(a => a.Id).AsEnumerable().Select(ToAccount).ToList();
    }

    public Account? Account(long id)
    {
        using var l = Open();
        var a = l.Ctx.Accounts.AsNoTracking().FirstOrDefault(x => x.Id == id);
        return a is null ? null : ToAccount(a);
    }

    public Account AddAccount(string name, AccountKind kind, string? statementMatch = null)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        name = name.Trim();
        if (name.Length == 0) throw new ComptaException("Le nom du compte est obligatoire.");
        using var l = Open();
        var ctx = l.Ctx;
        if (ctx.Accounts.AsNoTracking().AsEnumerable().Any(a => SameName(a.Name, name)))
            throw new ComptaException($"Un compte « {name} » existe déjà.");
        var pos = (ctx.Accounts.AsNoTracking().Max(a => (int?)a.Position) ?? 0) + 1;
        var e = new AccountEntity { Name = name, Kind = AccountKindToDb(kind), Position = pos, Active = true,
                                    StatementMatch = CleanStatementMatch(statementMatch) };
        ctx.Accounts.Add(e);
        Save(ctx);
        Log(ctx, "Compte ajouté", name);
        return ToAccount(e);
    }

    public void UpdateAccount(long id, string name, AccountKind kind, bool active, string? statementMatch = null)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        name = name.Trim();
        if (name.Length == 0) throw new ComptaException("Le nom du compte est obligatoire.");
        using var l = Open();
        var ctx = l.Ctx;
        if (ctx.Accounts.AsNoTracking().AsEnumerable().Any(a => a.Id != id && SameName(a.Name, name)))
            throw new ComptaException($"Un compte « {name} » existe déjà.");
        var e = ctx.Accounts.FirstOrDefault(a => a.Id == id) ?? throw new ComptaException("Compte introuvable.");
        e.Name = name;
        e.Kind = AccountKindToDb(kind);
        e.Active = active;
        e.StatementMatch = CleanStatementMatch(statementMatch);
        Save(ctx);
        Log(ctx, "Compte modifié", name);
    }

    public void DeleteAccount(long id)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        using var l = Open();
        var ctx = l.Ctx;
        if (ctx.Entries.AsNoTracking().Any(e => e.AccountId == id || e.ToAccountId == id))
            throw new ComptaException("Ce compte est utilisé par des écritures : désactivez-le plutôt que de le supprimer.");
        using var tx = Begin();
        var ctx2 = Open().Ctx;
        var openings = ctx2.YearOpenings.Where(o => o.AccountId == id).ToList();
        if (openings.Any(o => ctx2.Years.AsNoTracking().Any(y => y.Id == o.YearId && y.Status == "VALIDATED")))
            throw new ComptaException("Ce compte porte un solde d'ouverture dans un exercice validé : désactivez-le plutôt que de le supprimer.");
        ctx2.YearOpenings.RemoveRange(openings);
        var acc = ctx2.Accounts.FirstOrDefault(a => a.Id == id);
        if (acc is not null) ctx2.Accounts.Remove(acc);
        Save(ctx2);
        Log(ctx2, "Compte supprimé", id.ToString());
        tx.Commit();
    }

    // ------------------------------------------------------------------ exercices

    public List<FiscalYear> Years()
    {
        using var l = Open();
        return l.Ctx.Years.AsNoTracking().OrderBy(y => y.StartDate).AsEnumerable().Select(ToYear).ToList();
    }

    public FiscalYear? Year(long id)
    {
        using var l = Open();
        var y = l.Ctx.Years.AsNoTracking().FirstOrDefault(x => x.Id == id);
        return y is null ? null : ToYear(y);
    }

    public FiscalYear? PreviousYear(FiscalYear y)
    {
        using var l = Open();
        var s = D(y.Start);
        var p = l.Ctx.Years.AsNoTracking().Where(x => string.Compare(x.StartDate, s) < 0)
            .OrderByDescending(x => x.StartDate).FirstOrDefault();
        return p is null ? null : ToYear(p);
    }

    public FiscalYear? NextYear(FiscalYear y)
    {
        using var l = Open();
        var s = D(y.Start);
        var n = l.Ctx.Years.AsNoTracking().Where(x => string.Compare(x.StartDate, s) > 0)
            .OrderBy(x => x.StartDate).FirstOrDefault();
        return n is null ? null : ToYear(n);
    }

    /// <summary>Libellé par défaut : « 2025-2026 » (ou « 2026 » si l'exercice tient dans l'année civile).</summary>
    public static string DefaultLabel(DateOnly start, DateOnly end) =>
        start.Year == end.Year ? start.Year.ToString(CultureInfo.InvariantCulture) : $"{start.Year}-{end.Year}";

    public FiscalYear CreateYear(DateOnly start, DateOnly end, bool cloneStructure = true, string? label = null)
    {
        Require(Permission.ManageStructure);
        RequireWritable();
        if (end <= start) throw new ComptaException("La date de fin doit être postérieure à la date de début.");
        label = string.IsNullOrWhiteSpace(label) ? DefaultLabel(start, end) : label.Trim();
        var existing = Years();
        if (existing.Any(y => y.Label == label)) throw new ComptaException($"Un exercice « {label} » existe déjà.");
        foreach (var o in existing)
            if (start <= o.End && end >= o.Start)
                throw new ComptaException($"La période chevauche l'exercice {o.Label} ({o.Start:dd/MM/yyyy} - {o.End:dd/MM/yyyy}).");

        FiscalYear created;
        FiscalYear? prev;
        using (var tx = Begin())
        {
            var ctx = Open().Ctx;
            EnsureDefaultAccounts(ctx);
            var provisional = new FiscalYear(0, label, start, end, false, null, null);
            prev = PreviousYear(provisional);
            var ye = new YearEntity { Label = label, StartDate = D(start), EndDate = D(end), Status = "OPEN" };
            ctx.Years.Add(ye);
            Save(ctx);

            if (cloneStructure)
            {
                if (prev is not null) CloneStructure(ctx, prev.Id, ye.Id);
                else SeedDefaultStructure(ctx, ye.Id);
                Save(ctx);
            }
            Log(ctx, "Exercice créé", label);
            created = ToYear(ye);
            tx.Commit();
        }
        if (prev is not null) SyncCarry(prev.Id);
        return created;
    }

    private static void EnsureDefaultAccounts(AppDbContext ctx)
    {
        if (ctx.Accounts.AsNoTracking().Any()) return;
        ctx.Accounts.Add(new AccountEntity { Name = "Banque", Kind = "BANK", Position = 1, Active = true });
        ctx.Accounts.Add(new AccountEntity { Name = "Livret A", Kind = "SAVINGS", Position = 2, Active = true });
        Save(ctx);
    }

    private static readonly string[] DefaultProducts =
        { "Adhésions", "Chocolat", "Loto Octobre", "Loto mars", "Tombola", "Kermesse", "Intérêts", "Subvention", "Locations", "Moulins Geek" };

    private static readonly string[] DefaultCharges =
        { "Investissement", "Garage", "Assurance", "Charges", "Moulins Geek", "Frais divers", "Frais bancaires",
          "Loto Octobre", "Loto mars", "Tombola", "Kermesse", "Subvention exceptionnelle", "Subvention annuelle",
          "Chocolats", "Consommables locations", "Loto n+1" };

    private static void SeedDefaultStructure(AppDbContext ctx, long yearId)
    {
        var i = 0;
        foreach (var n in DefaultProducts)
            ctx.Categories.Add(new CategoryEntity { YearId = yearId, Kind = "P", Name = n, Position = ++i });
        i = 0;
        foreach (var n in DefaultCharges)
            ctx.Categories.Add(new CategoryEntity { YearId = yearId, Kind = "C", Name = n, Position = ++i });
    }

    /// <summary>Recopie les catégories et les opérations d'un exercice vers un autre (sans les écritures).</summary>
    private static void CloneStructure(AppDbContext ctx, long fromYearId, long toYearId)
    {
        var opMap = new Dictionary<long, OperationEntity>();
        foreach (var o in ctx.Operations.AsNoTracking().Where(x => x.YearId == fromYearId).OrderBy(x => x.Position).ThenBy(x => x.Id).ToList())
        {
            var copy = new OperationEntity { YearId = toYearId, Name = o.Name, Position = o.Position };
            ctx.Operations.Add(copy);
            opMap[o.Id] = copy;
        }
        ctx.SaveChanges(); // identifiants des opérations copiées nécessaires aux catégories
        foreach (var c in ctx.Categories.AsNoTracking().Where(x => x.YearId == fromYearId).OrderBy(x => x.Position).ThenBy(x => x.Id).ToList())
            ctx.Categories.Add(new CategoryEntity
            {
                YearId = toYearId, Kind = c.Kind, Name = c.Name, Position = c.Position,
                OperationId = c.OperationId is { } oid && opMap.TryGetValue(oid, out var no) ? no.Id : null
            });
    }

    /// <summary>Modifie les dates d'un exercice ouvert (sans chevauchement avec les autres).</summary>
    public void SetYearPeriod(long yearId, DateOnly start, DateOnly end, string? label = null)
    {
        RequireOpen(yearId, Permission.ManageStructure);
        if (end <= start) throw new ComptaException("La date de fin doit être postérieure à la date de début.");
        foreach (var o in Years())
            if (o.Id != yearId && start <= o.End && end >= o.Start)
                throw new ComptaException($"La période chevauche l'exercice {o.Label}.");
        var current = Year(yearId)!;
        label = string.IsNullOrWhiteSpace(label) ? current.Label : label.Trim();
        if (Years().Any(y => y.Id != yearId && y.Label == label))
            throw new ComptaException($"Un exercice « {label} » existe déjà.");
        using var l = Open();
        var e = l.Ctx.Years.First(y => y.Id == yearId);
        e.StartDate = D(start);
        e.EndDate = D(end);
        e.Label = label;
        Save(l.Ctx);
        Log(l.Ctx, "Période modifiée", $"{label} {start:yyyy-MM-dd} → {end:yyyy-MM-dd}");
    }

    /// <summary>Supprime un exercice ouvert encore vide (annulation d'une création).</summary>
    public void DeleteYear(long yearId)
    {
        RequireOpen(yearId, Permission.ManageStructure);
        var y = Year(yearId)!;
        using var tx = Begin();
        var ctx = Open().Ctx;
        if (ctx.Entries.AsNoTracking().Any(e => e.YearId == yearId))
            throw new ComptaException("Cet exercice contient des écritures : il ne peut pas être supprimé.");
        var adjIds = ctx.Adjustments.AsNoTracking().Where(a => a.YearId == yearId).Select(a => a.Id).ToList();
        // Reports de l'exercice suivant qui en dépendent : supprimés avec leur source.
        var children = ctx.Adjustments.Where(a => a.SourceId != null && adjIds.Contains(a.SourceId.Value)).ToList();
        ctx.Adjustments.RemoveRange(children);
        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();
        var own = ctx.Adjustments.Where(a => a.YearId == yearId).ToList();
        // Les reports portés par cet exercice peuvent se référencer entre eux : on lève d'abord les liens.
        foreach (var a in own) a.SourceId = null;
        ctx.SaveChanges();
        ctx.Adjustments.RemoveRange(own);
        foreach (var c in ctx.Categories.Where(c => c.YearId == yearId && c.OperationId != null).ToList()) c.OperationId = null;
        ctx.SaveChanges();
        ctx.Categories.RemoveRange(ctx.Categories.Where(c => c.YearId == yearId));
        ctx.Operations.RemoveRange(ctx.Operations.Where(o => o.YearId == yearId));
        ctx.YearOpenings.RemoveRange(ctx.YearOpenings.Where(o => o.YearId == yearId));
        ctx.SaveChanges();
        ctx.Years.Remove(ctx.Years.First(x => x.Id == yearId));
        Save(ctx);
        Log(ctx, "Exercice supprimé", y.Label);
        tx.Commit();
    }

    // ------------------------------------------------------------------ soldes d'ouverture

    /// <summary>Solde d'ouverture d'un compte : clôture de l'exercice précédent s'il existe (report automatique),
    /// sinon le solde saisi pour le tout premier exercice.</summary>
    public long Opening(long yearId, long accountId)
    {
        var y = Year(yearId) ?? throw new ComptaException("Exercice introuvable.");
        var prev = PreviousYear(y);
        if (prev is not null) return Closing(prev.Id, accountId);
        using var l = Open();
        return l.Ctx.YearOpenings.AsNoTracking().Where(o => o.YearId == yearId && o.AccountId == accountId)
            .Select(o => (long?)o.Cents).FirstOrDefault() ?? 0;
    }

    public bool OpeningIsAutomatic(long yearId)
    {
        var y = Year(yearId);
        return y is not null && PreviousYear(y) is not null;
    }

    public long Closing(long yearId, long accountId)
    {
        long movement = 0;
        using (var l = Open())
        {
            var rows = l.Ctx.Entries.AsNoTracking().Where(e => e.YearId == yearId)
                .Select(e => new { e.Kind, e.AccountId, e.ToAccountId, e.Direction, e.AmountCents }).ToList();
            foreach (var e in rows)
            {
                if (e.Kind == "TRANSFER")
                {
                    if (e.AccountId == accountId) movement -= e.AmountCents;
                    if (e.ToAccountId == accountId) movement += e.AmountCents;
                }
                else if (e.AccountId == accountId)
                {
                    movement += e.Direction == "IN" ? e.AmountCents : -e.AmountCents;
                }
            }
        }
        return Opening(yearId, accountId) + movement;
    }

    /// <summary>Définit le solde d'ouverture du tout premier exercice (refusé s'il y a un exercice précédent).</summary>
    public void SetOpening(long yearId, long accountId, long cents)
    {
        RequireOpen(yearId, Permission.ManageStructure);
        if (OpeningIsAutomatic(yearId))
            throw new ComptaException("Le solde d'ouverture est repris automatiquement de l'exercice précédent.");
        using var l = Open();
        var ctx = l.Ctx;
        var e = ctx.YearOpenings.FirstOrDefault(o => o.YearId == yearId && o.AccountId == accountId);
        if (e is null) ctx.YearOpenings.Add(new YearOpeningEntity { YearId = yearId, AccountId = accountId, Cents = cents });
        else e.Cents = cents;
        Save(ctx);
        Log(ctx, "Solde d'ouverture saisi", $"{yearId}/{accountId} = {Money.Format(cents)}");
    }

    // ------------------------------------------------------------------ opérations

    public List<Operation> Operations(long yearId)
    {
        using var l = Open();
        return l.Ctx.Operations.AsNoTracking().Where(o => o.YearId == yearId)
            .OrderBy(o => o.Position).ThenBy(o => o.Id).AsEnumerable().Select(ToOperation).ToList();
    }

    private OperationEntity OperationEntityById(AppDbContext ctx, long id) =>
        ctx.Operations.FirstOrDefault(o => o.Id == id) ?? throw new ComptaException("Opération introuvable.");

    public Operation AddOperation(long yearId, string name)
    {
        RequireOpen(yearId, Permission.ManageStructure);
        name = name.Trim();
        if (name.Length == 0) throw new ComptaException("Le nom de l'opération est obligatoire.");
        using var l = Open();
        var ctx = l.Ctx;
        if (ctx.Operations.AsNoTracking().Where(o => o.YearId == yearId).AsEnumerable().Any(o => SameName(o.Name, name)))
            throw new ComptaException($"Une opération « {name} » existe déjà.");
        var pos = (ctx.Operations.AsNoTracking().Where(o => o.YearId == yearId).Max(o => (int?)o.Position) ?? 0) + 1;
        var e = new OperationEntity { YearId = yearId, Name = name, Position = pos };
        ctx.Operations.Add(e);
        Save(ctx);
        return ToOperation(e);
    }

    public void RenameOperation(long operationId, string name)
    {
        using var l = Open();
        var ctx = l.Ctx;
        var o = OperationEntityById(ctx, operationId);
        RequireOpen(o.YearId, Permission.ManageStructure);
        name = name.Trim();
        if (name.Length == 0) throw new ComptaException("Le nom de l'opération est obligatoire.");
        if (ctx.Operations.AsNoTracking().Where(x => x.YearId == o.YearId && x.Id != operationId).AsEnumerable().Any(x => SameName(x.Name, name)))
            throw new ComptaException($"Une opération « {name} » existe déjà.");
        o.Name = name;
        Save(ctx);
    }

    public void DeleteOperation(long operationId)
    {
        using var tx = Begin();
        var ctx = Open().Ctx;
        var o = OperationEntityById(ctx, operationId);
        RequireOpen(o.YearId, Permission.ManageStructure);
        foreach (var c in ctx.Categories.Where(c => c.OperationId == operationId).ToList()) c.OperationId = null;
        ctx.SaveChanges();
        ctx.Operations.Remove(o);
        Save(ctx);
        tx.Commit();
    }

    // ------------------------------------------------------------------ catégories (produits / charges)

    public List<Category> Categories(long yearId)
    {
        using var l = Open();
        // « P » avant « C » comme dans le classeur (produits puis charges), puis ordre choisi.
        return l.Ctx.Categories.AsNoTracking().Where(c => c.YearId == yearId)
            .OrderByDescending(c => c.Kind).ThenBy(c => c.Position).ThenBy(c => c.Id)
            .AsEnumerable().Select(ToCategory).ToList();
    }

    private static CategoryEntity CategoryEntityById(AppDbContext ctx, long id) =>
        ctx.Categories.FirstOrDefault(c => c.Id == id) ?? throw new ComptaException("Catégorie introuvable.");

    public Category AddCategory(long yearId, CategoryKind kind, string name)
    {
        RequireOpen(yearId, Permission.ManageStructure);
        name = name.Trim();
        if (name.Length == 0) throw new ComptaException("Le nom est obligatoire.");
        using var l = Open();
        var ctx = l.Ctx;
        var k = CatKindToDb(kind);
        if (ctx.Categories.AsNoTracking().Where(c => c.YearId == yearId && c.Kind == k).AsEnumerable().Any(c => SameName(c.Name, name)))
            throw new ComptaException($"« {name} » existe déjà dans cette liste.");
        var pos = (ctx.Categories.AsNoTracking().Where(c => c.YearId == yearId && c.Kind == k).Max(c => (int?)c.Position) ?? 0) + 1;
        var e = new CategoryEntity { YearId = yearId, Kind = k, Name = name, Position = pos };
        ctx.Categories.Add(e);
        Save(ctx);
        // Un produit et une charge de même nom forment automatiquement une opération du même nom.
        if (AutoLinkSameName(ctx, yearId, e.Id) > 0) Save(ctx);
        return ToCategory(e);
    }

    /// <summary>Rattache automatiquement les produits et les charges de même nom à une opération portant ce nom (créée
    /// si besoin). Une catégorie déjà rattachée à une opération n'est jamais déplacée : si une seule des deux l'est,
    /// l'autre rejoint la même opération. <paramref name="onlyCategoryId"/> limite le contrôle à une catégorie.
    /// Renvoie le nombre de rattachements faits (à enregistrer par l'appelant).</summary>
    private static int AutoLinkSameName(AppDbContext ctx, long yearId, long? onlyCategoryId = null)
    {
        var cats = ctx.Categories.Where(c => c.YearId == yearId).ToList();
        var ops = ctx.Operations.Where(o => o.YearId == yearId).ToList();
        var product = CatKindToDb(CategoryKind.Product);
        var targets = onlyCategoryId is { } id ? cats.Where(c => c.Id == id).ToList() : cats.Where(c => c.Kind == product).ToList();
        var linked = 0;
        foreach (var c in targets)
        {
            var pair = cats.FirstOrDefault(x => x.Kind != c.Kind && SameName(x.Name, c.Name));
            if (pair is null) continue;
            var p = c.Kind == product ? c : pair;
            var ch = c.Kind == product ? pair : c;
            if (p.OperationId is not null && ch.OperationId is not null) continue;   // déjà rattachées : choix conservé
            long opId;
            if (p.OperationId is { } a) opId = a;
            else if (ch.OperationId is { } b) opId = b;
            else
            {
                var op = ops.FirstOrDefault(o => SameName(o.Name, c.Name));
                if (op is null)
                {
                    op = new OperationEntity
                    {
                        YearId = yearId, Name = c.Name.Trim(), Position = (ops.Max(o => (int?)o.Position) ?? 0) + 1
                    };
                    ctx.Operations.Add(op);
                    ctx.SaveChanges();
                    ops.Add(op);
                }
                opId = op.Id;
            }
            if (p.OperationId is null) { p.OperationId = opId; linked++; }
            if (ch.OperationId is null) { ch.OperationId = opId; linked++; }
        }
        return linked;
    }

    /// <summary>Associe tous les produits et charges de même nom de l'exercice à une opération du même nom.</summary>
    public int AutoLinkOperations(long yearId)
    {
        RequireOpen(yearId, Permission.ManageStructure);
        using var tx = Begin();
        var ctx = Open().Ctx;
        var n = AutoLinkSameName(ctx, yearId);
        if (n > 0)
        {
            Log(ctx, "Opérations associées automatiquement", $"{n} rattachement(s) produit ↔ charge de même nom");
            Save(ctx);
        }
        tx.Commit();
        return n;
    }

    public void RenameCategory(long categoryId, string name)
    {
        using var l = Open();
        var ctx = l.Ctx;
        var c = CategoryEntityById(ctx, categoryId);
        RequireOpen(c.YearId, Permission.ManageStructure);
        name = name.Trim();
        if (name.Length == 0) throw new ComptaException("Le nom est obligatoire.");
        if (ctx.Categories.AsNoTracking().Where(x => x.YearId == c.YearId && x.Kind == c.Kind && x.Id != categoryId)
                .AsEnumerable().Any(x => SameName(x.Name, name)))
            throw new ComptaException($"« {name} » existe déjà dans cette liste.");
        c.Name = name;
        Save(ctx);
        if (AutoLinkSameName(ctx, c.YearId, c.Id) > 0) Save(ctx);
    }

    /// <summary>Monte (delta &lt; 0) ou descend (delta &gt; 0) une catégorie dans sa liste.</summary>
    public void MoveCategory(long categoryId, int delta)
    {
        using var tx = Begin();
        var ctx = Open().Ctx;
        var c = CategoryEntityById(ctx, categoryId);
        RequireOpen(c.YearId, Permission.ManageStructure);
        var list = ctx.Categories.Where(x => x.YearId == c.YearId && x.Kind == c.Kind)
            .OrderBy(x => x.Position).ThenBy(x => x.Id).ToList();
        var idx = list.FindIndex(x => x.Id == categoryId);
        var to = idx + delta;
        if (idx < 0 || to < 0 || to >= list.Count) return;
        var item = list[idx];
        list.RemoveAt(idx);
        list.Insert(to, item);
        for (var i = 0; i < list.Count; i++) list[i].Position = i + 1;
        Save(ctx);
        tx.Commit();
    }

    public void DeleteCategory(long categoryId)
    {
        using var l = Open();
        var ctx = l.Ctx;
        var c = CategoryEntityById(ctx, categoryId);
        RequireOpen(c.YearId, Permission.ManageStructure);
        var n = ctx.Allocations.AsNoTracking().Count(a => a.CategoryId == categoryId)
              + ctx.Adjustments.AsNoTracking().Count(a => a.CategoryId == categoryId);
        if (n > 0)
            throw new ComptaException($"« {c.Name} » est utilisée par {n} ligne(s) : elle ne peut pas être supprimée. Vous pouvez la renommer.");
        ctx.Categories.Remove(c);
        Save(ctx);
    }

    /// <summary>Rattache une catégorie à une opération (null = aucune). Produits et charges d'une même
    /// opération sont ainsi liés : leur rentabilité apparaît ensemble dans le bilan.</summary>
    public void SetCategoryOperation(long categoryId, long? operationId)
    {
        using var l = Open();
        var ctx = l.Ctx;
        var c = CategoryEntityById(ctx, categoryId);
        RequireOpen(c.YearId, Permission.ManageStructure);
        if (operationId is { } oid)
        {
            var o = OperationEntityById(ctx, oid);
            if (o.YearId != c.YearId) throw new ComptaException("Cette opération appartient à un autre exercice.");
        }
        c.OperationId = operationId;
        Save(ctx);
    }
}
