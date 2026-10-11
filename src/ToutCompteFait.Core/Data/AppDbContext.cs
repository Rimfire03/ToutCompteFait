using ToutCompteFait.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core.Data;

/// <summary>Contexte EF Core de l'application (même architecture que FiltresDbContext : une cible, un mode
/// lecture seule, quatre moteurs).</summary>
public class AppDbContext : DbContext
{
    private readonly DbTarget _target;
    private readonly bool _readOnly;

    // Version du serveur MariaDB/MySQL détectée une fois par serveur (la détection ouvre une connexion).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ServerVersion> MySqlVersions = new();

    public AppDbContext(DbTarget target, bool readOnly = false)
    {
        _target = target;
        _readOnly = readOnly;
    }

    public DatabaseProvider Provider => _target.Provider;

    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    public DbSet<YearEntity> Years => Set<YearEntity>();
    public DbSet<YearOpeningEntity> YearOpenings => Set<YearOpeningEntity>();
    public DbSet<OperationEntity> Operations => Set<OperationEntity>();
    public DbSet<CategoryEntity> Categories => Set<CategoryEntity>();
    public DbSet<EntryEntity> Entries => Set<EntryEntity>();
    public DbSet<AllocationEntity> Allocations => Set<AllocationEntity>();
    public DbSet<AdjustmentEntity> Adjustments => Set<AdjustmentEntity>();
    public DbSet<AttachmentEntity> Attachments => Set<AttachmentEntity>();
    public DbSet<AuditEntity> Audit => Set<AuditEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Journal SQLite par défaut (DELETE) volontairement conservé : le mode WAL ne fonctionne pas sur un disque
        // réseau. Le délai d'attente couvre les lectures qui tombent pendant une écriture.
        switch (_target.Provider)
        {
            case DatabaseProvider.Sqlite:
                var cs = new SqliteConnectionStringBuilder
                {
                    DataSource = _target.FilePath,
                    Mode = _readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                    DefaultTimeout = 30,
                    ForeignKeys = true
                }.ToString();
                optionsBuilder.UseSqlite(cs);
                break;

            case DatabaseProvider.PostgreSql:
                optionsBuilder.UseNpgsql(_target.Server!.BuildConnectionString());
                break;

            case DatabaseProvider.MariaDb:
                var mysql = _target.Server!.BuildConnectionString();
                optionsBuilder.UseMySql(mysql, MySqlVersions.GetOrAdd(mysql, ServerVersion.AutoDetect));
                break;

            case DatabaseProvider.SqlServer:
                optionsBuilder.UseSqlServer(_target.Server!.BuildConnectionString());
                break;
        }

        // Lecture seule : sans suivi, chaque requête relit la base et voit les modifications enregistrées entre-temps
        // par le poste rédacteur.
        if (_readOnly) optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        // Serveur : pas de mode lecture seule natif comme SQLite, donc toute écriture est refusée par un intercepteur.
        if (_readOnly && _target.IsServer) optionsBuilder.AddInterceptors(ReadOnlyGuard.Instance);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (_readOnly && _target.IsServer) throw ReadOnlyGuard.Error();
        YearLockGuard.Enforce(this);

        var changed = Provider != DatabaseProvider.Sqlite && ChangeTracker.HasChanges()
                      && ChangeTracker.Entries().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                                                          && e.Entity is not AuditEntity and not UserEntity);
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        if (changed && result > 0) DatabaseWriteTracking.StampServer(this);
        return result;
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AccountEntity>(e =>
        {
            e.ToTable("accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(20).IsRequired();
            e.Property(x => x.Position).HasColumnName("position");
            e.Property(x => x.Active).HasColumnName("active");
            e.Property(x => x.StatementMatch).HasColumnName("statement_match").HasMaxLength(500);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<YearEntity>(e =>
        {
            e.ToTable("fiscal_years");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Label).HasColumnName("label").HasMaxLength(100).IsRequired();
            e.Property(x => x.StartDate).HasColumnName("start_date").HasMaxLength(10).IsRequired();
            e.Property(x => x.EndDate).HasColumnName("end_date").HasMaxLength(10).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
            e.Property(x => x.ValidatedAt).HasColumnName("validated_at").HasMaxLength(30);
            e.Property(x => x.ValidatedBy).HasColumnName("validated_by").HasMaxLength(200);
            e.Property(x => x.Checksum).HasColumnName("checksum").HasMaxLength(100);
            e.Property(x => x.ClosingJson).HasColumnName("closing_json");
            e.HasIndex(x => x.Label).IsUnique();
        });

        b.Entity<YearOpeningEntity>(e =>
        {
            e.ToTable("year_openings");
            e.HasKey(x => new { x.YearId, x.AccountId });
            e.Property(x => x.YearId).HasColumnName("year_id");
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.Cents).HasColumnName("cents");
            e.HasOne<YearEntity>().WithMany().HasForeignKey(x => x.YearId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<OperationEntity>(e =>
        {
            e.ToTable("operations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.YearId).HasColumnName("year_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Position).HasColumnName("position");
            e.HasIndex(x => new { x.YearId, x.Name }).IsUnique();
            e.HasOne<YearEntity>().WithMany().HasForeignKey(x => x.YearId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CategoryEntity>(e =>
        {
            e.ToTable("categories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.YearId).HasColumnName("year_id");
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(1).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Position).HasColumnName("position");
            e.Property(x => x.OperationId).HasColumnName("operation_id");
            e.HasIndex(x => new { x.YearId, x.Kind, x.Name }).IsUnique();
            e.HasOne<YearEntity>().WithMany().HasForeignKey(x => x.YearId).OnDelete(DeleteBehavior.Cascade);
            // Pas de SET NULL côté base (SQL Server refuse les chemins en cascade multiples) : le service délie d'abord.
            e.HasOne<OperationEntity>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<EntryEntity>(e =>
        {
            e.ToTable("entries", t => t.HasCheckConstraint("ck_entries_amount", "amount_cents > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.YearId).HasColumnName("year_id");
            e.Property(x => x.EntryDate).HasColumnName("entry_date").HasMaxLength(10).IsRequired();
            e.Property(x => x.Label).HasColumnName("label").HasMaxLength(500).IsRequired();
            e.Property(x => x.Pointed).HasColumnName("pointed");
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(20).IsRequired();
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.Direction).HasColumnName("direction").HasMaxLength(3).IsRequired();
            e.Property(x => x.ToAccountId).HasColumnName("to_account_id");
            e.Property(x => x.AmountCents).HasColumnName("amount_cents");
            e.Property(x => x.MemberName).HasColumnName("member_name").HasMaxLength(200);
            e.Property(x => x.Note).HasColumnName("note").HasMaxLength(1000);
            e.Property(x => x.ImportRef).HasColumnName("import_ref").HasMaxLength(300);
            e.Property(x => x.RefundOfId).HasColumnName("refund_of_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasMaxLength(30).IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasMaxLength(30).IsRequired();
            e.HasIndex(x => new { x.YearId, x.EntryDate, x.Id }).HasDatabaseName("ix_entries_year");
            e.HasOne<YearEntity>().WithMany().HasForeignKey(x => x.YearId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AccountEntity>().WithMany().HasForeignKey(x => x.ToAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<EntryEntity>().WithMany().HasForeignKey(x => x.RefundOfId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AllocationEntity>(e =>
        {
            e.ToTable("allocations", t => t.HasCheckConstraint("ck_allocations_amount", "amount_cents <> 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntryId).HasColumnName("entry_id");
            e.Property(x => x.CategoryId).HasColumnName("category_id");
            e.Property(x => x.AmountCents).HasColumnName("amount_cents");
            e.HasIndex(x => x.EntryId).HasDatabaseName("ix_alloc_entry");
            e.HasIndex(x => x.CategoryId).HasDatabaseName("ix_alloc_cat");
            e.HasOne<EntryEntity>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CategoryEntity>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AttachmentEntity>(e =>
        {
            e.ToTable("attachments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntryId).HasColumnName("entry_id");
            e.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(300).IsRequired();
            e.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(100).IsRequired();
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.Data).HasColumnName("data").IsRequired();
            e.Property(x => x.Compression).HasColumnName("compression").HasMaxLength(10).IsRequired();
            e.Property(x => x.AddedAt).HasColumnName("added_at").HasMaxLength(30).IsRequired();
            e.Property(x => x.AddedBy).HasColumnName("added_by").HasMaxLength(200);
            e.HasIndex(x => x.EntryId).HasDatabaseName("ix_attach_entry");
            e.HasOne<EntryEntity>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AdjustmentEntity>(e =>
        {
            e.ToTable("adjustments", t => t.HasCheckConstraint("ck_adjustments_amount", "amount_cents > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.YearId).HasColumnName("year_id");
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(20).IsRequired();
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(1).IsRequired();
            e.Property(x => x.CategoryId).HasColumnName("category_id");
            e.Property(x => x.Label).HasColumnName("label").HasMaxLength(500).IsRequired();
            e.Property(x => x.AmountCents).HasColumnName("amount_cents");
            e.Property(x => x.TargetName).HasColumnName("target_name").HasMaxLength(200);
            e.Property(x => x.SourceId).HasColumnName("source_id");
            e.Property(x => x.Note).HasColumnName("note").HasMaxLength(1000);
            e.HasOne<YearEntity>().WithMany().HasForeignKey(x => x.YearId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CategoryEntity>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AdjustmentEntity>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuditEntity>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.At).HasColumnName("at").HasMaxLength(30).IsRequired();
            e.Property(x => x.UserName).HasColumnName("user_name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Action).HasColumnName("action").HasMaxLength(200).IsRequired();
            e.Property(x => x.Detail).HasColumnName("detail");
        });

        b.Entity<UserEntity>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserName).HasColumnName("user_name").HasMaxLength(100).IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
            e.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(300).IsRequired();
            e.Property(x => x.Role).HasColumnName("role").HasMaxLength(20).IsRequired();
            e.Property(x => x.Active).HasColumnName("active");
            e.Property(x => x.MustChangePassword).HasColumnName("must_change_password");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasMaxLength(30).IsRequired();
            e.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(200);
            e.Property(x => x.LastLoginAt).HasColumnName("last_login_at").HasMaxLength(30);
            e.HasIndex(x => x.UserName).IsUnique();
        });
    }
}

/// <summary>Refuse toute commande d'écriture sur un poste en lecture seule connecté à un serveur de base de données
/// (voir <see cref="ServerWriteLock"/>).</summary>
internal sealed class ReadOnlyGuard : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    public static readonly ReadOnlyGuard Instance = new();

    public static ComptaException Error() =>
        new("Ce poste est en lecture seule : un autre poste a l'accès en écriture à la base. Aucune modification n'a été enregistrée.");

    public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> NonQueryExecuting(
        System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result) => throw Error();

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
        System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw Error();
}

/// <summary>Verrou des exercices validés, côté application et valable pour les quatre moteurs : toute tentative
/// d'ajout, de modification ou de suppression d'une donnée rattachée à un exercice VALIDATED est refusée avant
/// d'atteindre la base. (SQLite a en plus des déclencheurs, voir <see cref="SqliteTriggers"/>.)</summary>
internal static class YearLockGuard
{
    public static void Enforce(AppDbContext ctx)
    {
        var years = new HashSet<long>();
        var allocationEntryIds = new HashSet<long>();

        foreach (var e in ctx.ChangeTracker.Entries())
        {
            if (e.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            long? Orig(string prop) => e.State == EntityState.Added ? null : e.OriginalValues.GetValue<long>(prop);
            long? Curr(string prop) => e.State == EntityState.Deleted ? null : e.CurrentValues.GetValue<long>(prop);

            switch (e.Entity)
            {
                case YearEntity y:
                    // Seule la transition d'état (validation / réouverture) peut toucher un exercice validé.
                    if (e.State == EntityState.Deleted && e.OriginalValues.GetValue<string>("Status") == "VALIDATED")
                        throw new ComptaException("Exercice validé : suppression impossible.");
                    if (e.State == EntityState.Modified
                        && e.OriginalValues.GetValue<string>("Status") == "VALIDATED" && y.Status == "VALIDATED")
                        throw new ComptaException("Exercice validé : modification impossible.");
                    break;

                case EntryEntity or CategoryEntity or OperationEntity or AdjustmentEntity or YearOpeningEntity:
                    foreach (var id in new[] { Orig("YearId"), Curr("YearId") })
                        if (id is { } v) years.Add(v);
                    break;

                case AllocationEntity or AttachmentEntity:
                    foreach (var id in new[] { Orig("EntryId"), Curr("EntryId") })
                        if (id is { } v) allocationEntryIds.Add(v);
                    break;
            }
        }

        if (allocationEntryIds.Count > 0)
        {
            var ids = allocationEntryIds.ToList();
            // Entrées créées dans ce même lot : pas encore en base, donc rattachées à un exercice déjà vérifié ci-dessus.
            foreach (var y in ctx.Set<EntryEntity>().AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.YearId).Distinct().ToList())
                years.Add(y);
        }
        if (years.Count == 0) return;

        var list = years.ToList();
        var locked = ctx.Set<YearEntity>().AsNoTracking()
            .Where(y => list.Contains(y.Id) && y.Status == "VALIDATED")
            .Select(y => y.Label).ToList();
        if (locked.Count > 0)
            throw new ComptaException($"L'exercice {locked[0]} est validé : aucune modification n'est possible.");
    }
}

