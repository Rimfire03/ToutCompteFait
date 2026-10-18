using ToutCompteFait.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core.Data.Migrations;

/// <summary>Étapes de mise à jour du schéma, appliquées par <see cref="DbContextFactory.EnsureDatabaseUpToDate"/>.
/// Même système que FiltresApp : la version atteinte est enregistrée dans la base (SQLite : <c>PRAGMA user_version</c> ;
/// serveurs : table <c>DbInfo</c>) et, au lancement, le poste rédacteur applique uniquement les migrations manquantes
/// après une sauvegarde.</summary>
internal static class DatabaseMigrations
{
    /// <summary>Migrations du schéma, dans l'ordre. Règles : ne jamais modifier ni renuméroter une migration publiée,
    /// toujours en ajouter une nouvelle à la fin, écrire les changements de données en SQL brut (le modèle EF évolue,
    /// pas la migration) et, pour chaque nouvelle migration, ajouter l'équivalent serveur dans
    /// <see cref="ServerMigrations"/>.</summary>
    internal static readonly (int Version, string Description, Action<AppDbContext> Apply)[] All =
    {
        (1, "Schéma initial (exercices, comptes, écritures, ventilations, reports, journal)", _ => { }),
        (2, "Comptes utilisateurs : mot de passe et rôles (administrateur, trésorier, lecteur)", AddUsers),
        (3, "Justificatifs joints aux écritures", AddAttachments),
        (4, "Remboursements reçus liés à la dépense d'origine", AddRefundLink),
        (5, "Identifiants de relevé bancaire sur les comptes (détection à l'import)", AddStatementMatch),
        (6, "Numérotation continue des écritures et contre-passation (annulation sans suppression)", AddEntrySequence),
        (7, "Journal d'audit chaîné par empreintes (inaltérable)", AddAuditChain),
        (8, "Numéros de compte comptable pour l'export FEC", AddAccountNumbers),
        (9, "Entreprise (version Autoentreprise) : profil, types de déclaration URSSAF, déclarations", AddBusinessTables),
        (10, "Immobilisations : registre des biens amortis", AddFixedAssets),
        (11, "Statut Développeur des comptes utilisateurs", AddDeveloperFlag),
        (12, "Charges classées investissement (repère par nom de catégorie)", AddCategoryInvestments),
        (13, "Charges personnelles de l'exploitant (écartées du résultat, repère par nom de catégorie)", AddCategoryPersonal),
        (14, "Mode de règlement et référence de pièce des écritures (livre des recettes, registre des achats)", AddPaymentInfo),
        (15, "Autoentreprise : ventilations en produits remises à zéro (produits désormais libres, rattachés ensuite à un type URSSAF)", ResetProductAllocations),
        (16, "Autoentreprise : ventilations de sens contraire annulées (crédits en charges, débits en produits)", ResetWrongWayAllocations),
        (17, "Autoentreprise : un produit personnel n'a plus de type de déclaration URSSAF", ClearPersonalProductUrssaf),
        (18, "Déclarations URSSAF : montant réellement déclaré (saisi par l'utilisateur)", AddUrssafDeclaredAmount),
    };

    /// <summary>Dernière version de <see cref="All"/> déjà contenue dans le schéma créé directement par EF Core sur un
    /// serveur : les bases serveur naissent au dernier schéma et n'ont jamais besoin des migrations SQLite.</summary>
    internal const int ServerBaselineVersion = 2;

    /// <summary>Mises à jour du schéma des bases serveur au-delà de <see cref="ServerBaselineVersion"/>. RÈGLE : toute
    /// nouvelle migration ajoutée à <see cref="All"/> doit avoir ici une entrée de même numéro (corps vide si sans effet
    /// sur le schéma), écrite en code portable (LINQ / <see cref="ServerSql"/>). Sinon le démarrage sur serveur échoue
    /// explicitement plutôt que de laisser le schéma diverger.</summary>
    internal static readonly (int Version, string Description, Action<AppDbContext> Apply)[] ServerMigrations =
    {
        (3, "Justificatifs joints aux écritures", AddAttachments),
        (4, "Remboursements reçus liés à la dépense d'origine", AddRefundLink),
        (5, "Identifiants de relevé bancaire sur les comptes (détection à l'import)", AddStatementMatch),
        (6, "Numérotation continue des écritures et contre-passation (annulation sans suppression)", AddEntrySequence),
        (7, "Journal d'audit chaîné par empreintes (inaltérable)", AddAuditChain),
        (8, "Numéros de compte comptable pour l'export FEC", AddAccountNumbers),
        (9, "Entreprise (version Autoentreprise) : profil, types de déclaration URSSAF, déclarations", AddBusinessTables),
        (10, "Immobilisations : registre des biens amortis", AddFixedAssets),
        (11, "Statut Développeur des comptes utilisateurs", AddDeveloperFlag),
        (12, "Charges classées investissement (repère par nom de catégorie)", AddCategoryInvestments),
        (13, "Charges personnelles de l'exploitant (écartées du résultat, repère par nom de catégorie)", AddCategoryPersonal),
        (14, "Mode de règlement et référence de pièce des écritures (livre des recettes, registre des achats)", AddPaymentInfo),
        (15, "Autoentreprise : ventilations en produits remises à zéro (produits désormais libres, rattachés ensuite à un type URSSAF)", ResetProductAllocations),
        (16, "Autoentreprise : ventilations de sens contraire annulées (crédits en charges, débits en produits)", ResetWrongWayAllocations),
        (17, "Autoentreprise : un produit personnel n'a plus de type de déclaration URSSAF", ClearPersonalProductUrssaf),
        (18, "Déclarations URSSAF : montant réellement déclaré (saisi par l'utilisateur)", AddUrssafDeclaredAmount),
    };

    /// <summary>Migrations serveur à appliquer depuis <paramref name="fromVersion"/> ; lève si l'une manque.</summary>
    internal static IReadOnlyList<(int Version, string Description, Action<AppDbContext> Apply)> ServerPlan(int fromVersion)
    {
        var plan = new List<(int, string, Action<AppDbContext>)>();
        foreach (var m in All.Where(m => m.Version > Math.Max(fromVersion, ServerBaselineVersion)))
        {
            var server = ServerMigrations.FirstOrDefault(s => s.Version == m.Version);
            if (server.Apply is null)
                throw new InvalidOperationException(
                    $"La mise à jour v{m.Version} de la base n'a pas d'équivalent pour les serveurs : ce logiciel ne peut pas mettre à jour cette base serveur.");
            plan.Add(server);
        }
        return plan;
    }

    // ------------------------------------------------------------------ v3 : justificatifs (tous moteurs)

    /// <summary>Crée la table des justificatifs si elle n'existe pas (SQLite et serveurs : SQL portable, un seul code
    /// pour les migrations SQLite et serveur).</summary>
    private static void AddAttachments(AppDbContext ctx)
    {
        if (ServerSql.TableExists(ctx, "attachments")) return;
        var sql = ctx.Provider switch
        {
            DatabaseProvider.Sqlite => """
                CREATE TABLE "attachments" (
                    "id" INTEGER NOT NULL CONSTRAINT "PK_attachments" PRIMARY KEY AUTOINCREMENT,
                    "entry_id" INTEGER NOT NULL,
                    "file_name" TEXT NOT NULL,
                    "content_type" TEXT NOT NULL,
                    "size_bytes" INTEGER NOT NULL,
                    "data" BLOB NOT NULL,
                    "compression" TEXT NOT NULL DEFAULT 'NONE',
                    "added_at" TEXT NOT NULL,
                    "added_by" TEXT NULL,
                    CONSTRAINT "FK_attachments_entries_entry_id" FOREIGN KEY ("entry_id") REFERENCES "entries" ("id") ON DELETE CASCADE
                );
                """,
            DatabaseProvider.PostgreSql => """
                CREATE TABLE "attachments" (
                    "id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    "entry_id" bigint NOT NULL REFERENCES "entries" ("id") ON DELETE CASCADE,
                    "file_name" varchar(300) NOT NULL,
                    "content_type" varchar(100) NOT NULL,
                    "size_bytes" bigint NOT NULL,
                    "data" bytea NOT NULL,
                    "compression" varchar(10) NOT NULL DEFAULT 'NONE',
                    "added_at" varchar(30) NOT NULL,
                    "added_by" varchar(200) NULL
                );
                """,
            DatabaseProvider.MariaDb => """
                CREATE TABLE `attachments` (
                    `id` bigint NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    `entry_id` bigint NOT NULL,
                    `file_name` varchar(300) NOT NULL,
                    `content_type` varchar(100) NOT NULL,
                    `size_bytes` bigint NOT NULL,
                    `data` longblob NOT NULL,
                    `compression` varchar(10) NOT NULL DEFAULT 'NONE',
                    `added_at` varchar(30) NOT NULL,
                    `added_by` varchar(200) NULL,
                    CONSTRAINT `FK_attachments_entries_entry_id` FOREIGN KEY (`entry_id`) REFERENCES `entries` (`id`) ON DELETE CASCADE
                );
                """,
            DatabaseProvider.SqlServer => """
                CREATE TABLE [attachments] (
                    [id] bigint NOT NULL IDENTITY(1,1) PRIMARY KEY,
                    [entry_id] bigint NOT NULL,
                    [file_name] nvarchar(300) NOT NULL,
                    [content_type] nvarchar(100) NOT NULL,
                    [size_bytes] bigint NOT NULL,
                    [data] varbinary(max) NOT NULL,
                    [compression] nvarchar(10) NOT NULL DEFAULT 'NONE',
                    [added_at] nvarchar(30) NOT NULL,
                    [added_by] nvarchar(200) NULL,
                    CONSTRAINT [FK_attachments_entries_entry_id] FOREIGN KEY ([entry_id]) REFERENCES [entries] ([id]) ON DELETE CASCADE
                );
                """,
            _ => throw new NotSupportedException()
        };
        ctx.Database.ExecuteSqlRaw(sql);
        var q = (string n) => ServerSql.Q(ctx.Provider, n);
        ctx.Database.ExecuteSqlRaw($"CREATE INDEX {q("ix_attach_entry")} ON {q("attachments")} ({q("entry_id")});");
    }

    // ------------------------------------------------------------------ v4 : lien remboursement -> dépense

    private static void AddRefundLink(AppDbContext ctx)
    {
        if (ServerSql.ColumnExists(ctx, "entries", "refund_of_id")) return;
        var sql = ctx.Provider switch
        {
            DatabaseProvider.Sqlite => """ALTER TABLE "entries" ADD COLUMN "refund_of_id" INTEGER NULL REFERENCES "entries" ("id");""",
            DatabaseProvider.PostgreSql => """ALTER TABLE "entries" ADD COLUMN "refund_of_id" bigint NULL REFERENCES "entries" ("id");""",
            DatabaseProvider.MariaDb => "ALTER TABLE `entries` ADD COLUMN `refund_of_id` bigint NULL, ADD CONSTRAINT `FK_entries_entries_refund_of_id` FOREIGN KEY (`refund_of_id`) REFERENCES `entries` (`id`);",
            DatabaseProvider.SqlServer => "ALTER TABLE [entries] ADD [refund_of_id] bigint NULL CONSTRAINT [FK_entries_entries_refund_of_id] FOREIGN KEY REFERENCES [entries] ([id]);",
            _ => throw new NotSupportedException()
        };
        ctx.Database.ExecuteSqlRaw(sql);
        var q = (string n) => ServerSql.Q(ctx.Provider, n);
        ctx.Database.ExecuteSqlRaw($"CREATE INDEX {q("ix_entries_refund")} ON {q("entries")} ({q("refund_of_id")});");
    }

    // ------------------------------------------------------------------ v5 : identifiants de relevé sur les comptes

    private static void AddStatementMatch(AppDbContext ctx)
    {
        if (ServerSql.ColumnExists(ctx, "accounts", "statement_match")) return;
        var sql = ctx.Provider switch
        {
            DatabaseProvider.Sqlite => """ALTER TABLE "accounts" ADD COLUMN "statement_match" TEXT NULL;""",
            DatabaseProvider.PostgreSql => """ALTER TABLE "accounts" ADD COLUMN "statement_match" varchar(500) NULL;""",
            DatabaseProvider.MariaDb => "ALTER TABLE `accounts` ADD COLUMN `statement_match` varchar(500) NULL;",
            DatabaseProvider.SqlServer => "ALTER TABLE [accounts] ADD [statement_match] nvarchar(500) NULL;",
            _ => throw new NotSupportedException()
        };
        ctx.Database.ExecuteSqlRaw(sql);
    }

    // ------------------------------------------------------------------ v6 : numérotation continue + contre-passation

    /// <summary>Ajoute <c>entries.seq</c> (numéro séquentiel dans l'exercice, rempli ici pour l'existant dans l'ordre de
    /// saisie) et <c>entries.reversal_of_id</c> (écriture annulée par une contre-passation). Touche aussi les écritures
    /// des exercices validés : sans effet sur leur empreinte (la formule v1 n'inclut pas ces colonnes, voir
    /// <see cref="IntegritySeal"/>), et les déclencheurs de verrouillage sont retirés avant toute migration.</summary>
    private static void AddEntrySequence(AppDbContext ctx)
    {
        var q = (string n) => ServerSql.Q(ctx.Provider, n);
        if (!ServerSql.ColumnExists(ctx, "entries", "seq"))
        {
            var sql = ctx.Provider switch
            {
                DatabaseProvider.Sqlite => """ALTER TABLE "entries" ADD COLUMN "seq" INTEGER NOT NULL DEFAULT 0;""",
                DatabaseProvider.PostgreSql => """ALTER TABLE "entries" ADD COLUMN "seq" bigint NOT NULL DEFAULT 0;""",
                DatabaseProvider.MariaDb => "ALTER TABLE `entries` ADD COLUMN `seq` bigint NOT NULL DEFAULT 0;",
                DatabaseProvider.SqlServer => "ALTER TABLE [entries] ADD [seq] bigint NOT NULL CONSTRAINT [DF_entries_seq] DEFAULT 0;",
                _ => throw new NotSupportedException()
            };
            ctx.Database.ExecuteSqlRaw(sql);
        }
        if (!ServerSql.ColumnExists(ctx, "entries", "reversal_of_id"))
        {
            var sql = ctx.Provider switch
            {
                DatabaseProvider.Sqlite => """ALTER TABLE "entries" ADD COLUMN "reversal_of_id" INTEGER NULL REFERENCES "entries" ("id");""",
                DatabaseProvider.PostgreSql => """ALTER TABLE "entries" ADD COLUMN "reversal_of_id" bigint NULL REFERENCES "entries" ("id");""",
                DatabaseProvider.MariaDb => "ALTER TABLE `entries` ADD COLUMN `reversal_of_id` bigint NULL, ADD CONSTRAINT `FK_entries_entries_reversal_of_id` FOREIGN KEY (`reversal_of_id`) REFERENCES `entries` (`id`);",
                DatabaseProvider.SqlServer => "ALTER TABLE [entries] ADD [reversal_of_id] bigint NULL CONSTRAINT [FK_entries_entries_reversal_of_id] FOREIGN KEY REFERENCES [entries] ([id]);",
                _ => throw new NotSupportedException()
            };
            ctx.Database.ExecuteSqlRaw(sql);
        }

        // Numéro = rang de saisie (identifiant croissant) dans l'exercice, à partir de 1.
        var t = q("entries");
        var backfill = ctx.Provider == DatabaseProvider.MariaDb
            // MariaDB/MySQL refuse une sous-requête sur la table mise à jour : jointure sur une table dérivée.
            ? $"UPDATE {t} e JOIN (SELECT a.id AS id, COUNT(*) AS rk FROM {t} a JOIN {t} b ON b.year_id = a.year_id AND b.id <= a.id " +
              $"GROUP BY a.id) r ON r.id = e.id SET e.seq = r.rk WHERE e.seq = 0"
            : $"UPDATE {t} SET {q("seq")} = (SELECT COUNT(*) FROM {t} e2 WHERE e2.{q("year_id")} = {t}.{q("year_id")} " +
              $"AND e2.{q("id")} <= {t}.{q("id")}) WHERE {q("seq")} = 0";
        ctx.Database.ExecuteSqlRaw(backfill);

        if (!ServerSql.IndexExists(ctx, "entries", "ux_entries_seq"))
            ctx.Database.ExecuteSqlRaw($"CREATE UNIQUE INDEX {q("ux_entries_seq")} ON {t} ({q("year_id")}, {q("seq")});");
        if (!ServerSql.IndexExists(ctx, "entries", "ix_entries_reversal"))
            ctx.Database.ExecuteSqlRaw($"CREATE INDEX {q("ix_entries_reversal")} ON {t} ({q("reversal_of_id")});");
    }

    // ------------------------------------------------------------------ v7 : journal d'audit chaîné

    /// <summary>Ajoute les colonnes d'empreinte du journal. Les lignes existantes restent sans empreinte : elles sont
    /// scellées par <see cref="AuditChain.SealLegacy"/> à l'ouverture suivante (SHA-256 non disponible en SQL portable).</summary>
    private static void AddAuditChain(AppDbContext ctx)
    {
        foreach (var column in new[] { "prev_hash", "hash" })
        {
            if (ServerSql.ColumnExists(ctx, "audit_log", column)) continue;
            var sql = ctx.Provider switch
            {
                DatabaseProvider.Sqlite => $"ALTER TABLE \"audit_log\" ADD COLUMN \"{column}\" TEXT NULL;",
                DatabaseProvider.PostgreSql => $"ALTER TABLE \"audit_log\" ADD COLUMN \"{column}\" varchar(64) NULL;",
                DatabaseProvider.MariaDb => $"ALTER TABLE `audit_log` ADD COLUMN `{column}` varchar(64) NULL;",
                DatabaseProvider.SqlServer => $"ALTER TABLE [audit_log] ADD [{column}] nvarchar(64) NULL;",
                _ => throw new NotSupportedException()
            };
            ctx.Database.ExecuteSqlRaw(sql);
        }
    }

    // ------------------------------------------------------------------ v8 : numéros de compte (FEC)

    private static void AddAccountNumbers(AppDbContext ctx)
    {
        if (!ServerSql.ColumnExists(ctx, "accounts", "account_number"))
        {
            var sql = ctx.Provider switch
            {
                DatabaseProvider.Sqlite => """ALTER TABLE "accounts" ADD COLUMN "account_number" TEXT NULL;""",
                DatabaseProvider.PostgreSql => """ALTER TABLE "accounts" ADD COLUMN "account_number" varchar(20) NULL;""",
                DatabaseProvider.MariaDb => "ALTER TABLE `accounts` ADD COLUMN `account_number` varchar(20) NULL;",
                DatabaseProvider.SqlServer => "ALTER TABLE [accounts] ADD [account_number] nvarchar(20) NULL;",
                _ => throw new NotSupportedException()
            };
            ctx.Database.ExecuteSqlRaw(sql);
        }
        if (ServerSql.TableExists(ctx, "category_accounts")) return;
        var create = ctx.Provider switch
        {
            DatabaseProvider.Sqlite => """
                CREATE TABLE "category_accounts" (
                    "kind" TEXT NOT NULL,
                    "name" TEXT NOT NULL,
                    "account_number" TEXT NOT NULL,
                    CONSTRAINT "PK_category_accounts" PRIMARY KEY ("kind", "name")
                );
                """,
            DatabaseProvider.PostgreSql => """
                CREATE TABLE "category_accounts" (
                    "kind" varchar(1) NOT NULL,
                    "name" varchar(200) NOT NULL,
                    "account_number" varchar(20) NOT NULL,
                    CONSTRAINT "PK_category_accounts" PRIMARY KEY ("kind", "name")
                );
                """,
            DatabaseProvider.MariaDb => """
                CREATE TABLE `category_accounts` (
                    `kind` varchar(1) NOT NULL,
                    `name` varchar(200) NOT NULL,
                    `account_number` varchar(20) NOT NULL,
                    CONSTRAINT `PK_category_accounts` PRIMARY KEY (`kind`, `name`)
                );
                """,
            DatabaseProvider.SqlServer => """
                CREATE TABLE [category_accounts] (
                    [kind] nvarchar(1) NOT NULL,
                    [name] nvarchar(200) NOT NULL,
                    [account_number] nvarchar(20) NOT NULL,
                    CONSTRAINT [PK_category_accounts] PRIMARY KEY ([kind], [name])
                );
                """,
            _ => throw new NotSupportedException()
        };
        ctx.Database.ExecuteSqlRaw(create);
    }

    // ------------------------------------------------------------------ v9 : entreprise (version Autoentreprise)

    /// <summary>Tables de la version Autoentreprise : réglages de l'entreprise (clé / valeur), type de déclaration URSSAF
    /// des catégories de produits (par nom) et déclarations marquées comme faites. Créées dans toutes les bases (vides et
    /// sans effet pour les autres versions) pour que le schéma soit le même partout.</summary>
    private static void AddBusinessTables(AppDbContext ctx)
    {
        void Create(string table, string sqlite, string pg, string maria, string mssql)
        {
            if (ServerSql.TableExists(ctx, table)) return;
            ctx.Database.ExecuteSqlRaw(ctx.Provider switch
            {
                DatabaseProvider.Sqlite => sqlite,
                DatabaseProvider.PostgreSql => pg,
                DatabaseProvider.MariaDb => maria,
                DatabaseProvider.SqlServer => mssql,
                _ => throw new NotSupportedException()
            });
        }

        Create("business_settings",
            """CREATE TABLE "business_settings" ("key" TEXT NOT NULL CONSTRAINT "PK_business_settings" PRIMARY KEY, "value" TEXT NOT NULL);""",
            """CREATE TABLE "business_settings" ("key" varchar(100) NOT NULL CONSTRAINT "PK_business_settings" PRIMARY KEY, "value" text NOT NULL);""",
            "CREATE TABLE `business_settings` (`key` varchar(100) NOT NULL, `value` longtext NOT NULL, CONSTRAINT `PK_business_settings` PRIMARY KEY (`key`));",
            "CREATE TABLE [business_settings] ([key] nvarchar(100) NOT NULL CONSTRAINT [PK_business_settings] PRIMARY KEY, [value] nvarchar(max) NOT NULL);");

        Create("category_urssaf",
            """CREATE TABLE "category_urssaf" ("name" TEXT NOT NULL CONSTRAINT "PK_category_urssaf" PRIMARY KEY, "kind" TEXT NOT NULL);""",
            """CREATE TABLE "category_urssaf" ("name" varchar(200) NOT NULL CONSTRAINT "PK_category_urssaf" PRIMARY KEY, "kind" varchar(20) NOT NULL);""",
            "CREATE TABLE `category_urssaf` (`name` varchar(200) NOT NULL, `kind` varchar(20) NOT NULL, CONSTRAINT `PK_category_urssaf` PRIMARY KEY (`name`));",
            "CREATE TABLE [category_urssaf] ([name] nvarchar(200) NOT NULL CONSTRAINT [PK_category_urssaf] PRIMARY KEY, [kind] nvarchar(20) NOT NULL);");

        Create("urssaf_declarations",
            """
            CREATE TABLE "urssaf_declarations" (
                "period_key" TEXT NOT NULL CONSTRAINT "PK_urssaf_declarations" PRIMARY KEY,
                "period_start" TEXT NOT NULL, "period_end" TEXT NOT NULL, "declared_at" TEXT NOT NULL,
                "declared_by" TEXT NULL, "turnover_cents" INTEGER NOT NULL, "note" TEXT NULL
            );
            """,
            """
            CREATE TABLE "urssaf_declarations" (
                "period_key" varchar(20) NOT NULL CONSTRAINT "PK_urssaf_declarations" PRIMARY KEY,
                "period_start" varchar(10) NOT NULL, "period_end" varchar(10) NOT NULL, "declared_at" varchar(30) NOT NULL,
                "declared_by" varchar(200) NULL, "turnover_cents" bigint NOT NULL, "note" varchar(500) NULL
            );
            """,
            """
            CREATE TABLE `urssaf_declarations` (
                `period_key` varchar(20) NOT NULL, `period_start` varchar(10) NOT NULL, `period_end` varchar(10) NOT NULL,
                `declared_at` varchar(30) NOT NULL, `declared_by` varchar(200) NULL, `turnover_cents` bigint NOT NULL,
                `note` varchar(500) NULL, CONSTRAINT `PK_urssaf_declarations` PRIMARY KEY (`period_key`)
            );
            """,
            """
            CREATE TABLE [urssaf_declarations] (
                [period_key] nvarchar(20) NOT NULL CONSTRAINT [PK_urssaf_declarations] PRIMARY KEY,
                [period_start] nvarchar(10) NOT NULL, [period_end] nvarchar(10) NOT NULL, [declared_at] nvarchar(30) NOT NULL,
                [declared_by] nvarchar(200) NULL, [turnover_cents] bigint NOT NULL, [note] nvarchar(500) NULL
            );
            """);
    }
    // ------------------------------------------------------------------ v12 : charges classées investissement

    /// <summary>Table <c>category_investments</c> (nom de la charge) : repère « investissement », hors des tables
    /// verrouillées avec les exercices.</summary>
    private static void AddCategoryInvestments(AppDbContext ctx)
    {
        if (ServerSql.TableExists(ctx, "category_investments")) return;
        ctx.Database.ExecuteSqlRaw(ctx.Provider switch
        {
            DatabaseProvider.Sqlite => """CREATE TABLE "category_investments" ("name" TEXT NOT NULL CONSTRAINT "PK_category_investments" PRIMARY KEY);""",
            DatabaseProvider.PostgreSql => """CREATE TABLE "category_investments" ("name" varchar(200) NOT NULL CONSTRAINT "PK_category_investments" PRIMARY KEY);""",
            DatabaseProvider.MariaDb => "CREATE TABLE `category_investments` (`name` varchar(200) NOT NULL, CONSTRAINT `PK_category_investments` PRIMARY KEY (`name`));",
            DatabaseProvider.SqlServer => "CREATE TABLE [category_investments] ([name] nvarchar(200) NOT NULL CONSTRAINT [PK_category_investments] PRIMARY KEY);",
            _ => throw new NotSupportedException()
        });
    }
    // ------------------------------------------------------------------ v13 : charges personnelles

    // ------------------------------------------------------------------ v14 : mode de règlement et référence de pièce

    /// <summary>Colonnes <c>entries.payment_mode</c> et <c>entries.piece_ref</c> (nullables). Ajout de colonnes seulement :
    /// sans effet sur les empreintes v1 et v2 des exercices validés (leurs formules ne les contiennent pas).</summary>
    private static void AddPaymentInfo(AppDbContext ctx)
    {
        foreach (var (column, size) in new[] { ("payment_mode", 20), ("piece_ref", 100) })
        {
            if (ServerSql.ColumnExists(ctx, "entries", column)) continue;
            var sql = ctx.Provider switch
            {
                DatabaseProvider.Sqlite => $"ALTER TABLE \"entries\" ADD COLUMN \"{column}\" TEXT NULL;",
                DatabaseProvider.PostgreSql => $"ALTER TABLE \"entries\" ADD COLUMN \"{column}\" varchar({size}) NULL;",
                DatabaseProvider.MariaDb => $"ALTER TABLE `entries` ADD COLUMN `{column}` varchar({size}) NULL;",
                DatabaseProvider.SqlServer => $"ALTER TABLE [entries] ADD [{column}] nvarchar({size}) NULL;",
                _ => throw new NotSupportedException()
            };
            ctx.Database.ExecuteSqlRaw(sql);
        }
    }

    // ------------------------------------------------------------------ v15 : produits libres (Autoentreprise)

    /// <summary>Version Autoentreprise uniquement : supprime les ventilations sur des produits (recettes) des exercices
    /// NON validés, les écritures redeviennent « à ventiler » (montants, dates et libellés des écritures intacts). Les
    /// exercices validés ne sont jamais touchés : leurs ventilations sont couvertes par l'empreinte. Édition Asso : sans effet.</summary>
    private static void ResetProductAllocations(AppDbContext ctx)
    {
        if (!Editions.Current.IsBusiness) return;
        var q = (string n) => ServerSql.Q(ctx.Provider, n);
        ctx.Database.ExecuteSqlRaw(
            $"DELETE FROM {q("allocations")} WHERE {q("category_id")} IN (" +
            $"SELECT c.{q("id")} FROM {q("categories")} c JOIN {q("fiscal_years")} y ON y.{q("id")} = c.{q("year_id")} " +
            $"WHERE c.{q("kind")} = 'P' AND y.{q("status")} <> 'VALIDATED')");
    }

    // ------------------------------------------------------------------ v18 : montant déclaré à l'URSSAF

    /// <summary>Ajoute le montant réellement déclaré sur le site de l'URSSAF à chaque déclaration conservée (paramétrage
    /// hors empreinte : aucun montant d'écriture n'est touché).</summary>
    private static void AddUrssafDeclaredAmount(AppDbContext ctx)
    {
        if (!ServerSql.TableExists(ctx, "urssaf_declarations") || ServerSql.ColumnExists(ctx, "urssaf_declarations", "declared_cents")) return;
        var sql = ctx.Provider switch
        {
            DatabaseProvider.Sqlite => "ALTER TABLE \"urssaf_declarations\" ADD COLUMN \"declared_cents\" INTEGER NULL;",
            DatabaseProvider.PostgreSql => "ALTER TABLE \"urssaf_declarations\" ADD COLUMN \"declared_cents\" bigint NULL;",
            DatabaseProvider.MariaDb => "ALTER TABLE `urssaf_declarations` ADD COLUMN `declared_cents` bigint NULL;",
            DatabaseProvider.SqlServer => "ALTER TABLE [urssaf_declarations] ADD [declared_cents] bigint NULL;",
            _ => throw new NotSupportedException()
        };
        ctx.Database.ExecuteSqlRaw(sql);
    }
    // ------------------------------------------------------------------ v17 : produit personnel sans type URSSAF

    /// <summary>Un produit marqué « personnel » (apport de l'exploitant) n'est pas du chiffre d'affaires : son type URSSAF
    /// est retiré. Paramétrage seul (table hors empreinte) : aucun montant, aucune ventilation n'est touché.</summary>
    private static void ClearPersonalProductUrssaf(AppDbContext ctx)
    {
        if (!Editions.Current.IsBusiness) return;
        var personal = ctx.CategoryPersonal.AsEnumerable()
            .Where(c => c.Name.StartsWith("P:", StringComparison.Ordinal)).Select(c => c.Name[2..])
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (personal.Count == 0) return;
        var rows = ctx.CategoryUrssaf.AsEnumerable().Where(c => personal.Contains(c.Name)).ToList();
        if (rows.Count == 0) return;
        ctx.CategoryUrssaf.RemoveRange(rows);
        ctx.SaveChanges();
    }

    // ------------------------------------------------------------------ v16 : ventilation selon le sens (Autoentreprise)

    /// <summary>Version Autoentreprise uniquement : annule les ventilations de sens contraire à l'écriture, sur les
    /// exercices NON validés : crédit ventilé sur une charge (hors remboursement lié à une dépense) et débit ventilé sur
    /// un produit. Les écritures redeviennent « à ventiler » ; montants, dates et libellés intacts. Exercices validés
    /// jamais touchés (empreinte). Deux requêtes sans auto-référence (MariaDB refuse de supprimer d'une table lue en sous-requête).</summary>
    private static void ResetWrongWayAllocations(AppDbContext ctx)
    {
        if (!Editions.Current.IsBusiness) return;
        var q = (string n) => ServerSql.Q(ctx.Provider, n);
        string Entries(string direction, string extra) =>
            $"SELECT e.{q("id")} FROM {q("entries")} e JOIN {q("fiscal_years")} y ON y.{q("id")} = e.{q("year_id")} " +
            $"WHERE e.{q("direction")} = '{direction}'{extra} AND y.{q("status")} <> 'VALIDATED'";
        string Categories(string kind) => $"SELECT {q("id")} FROM {q("categories")} WHERE {q("kind")} = '{kind}'";
        // Crédits (hors remboursements liés) ventilés sur des charges.
        ctx.Database.ExecuteSqlRaw(
            $"DELETE FROM {q("allocations")} WHERE {q("category_id")} IN ({Categories("C")}) " +
            $"AND {q("entry_id")} IN ({Entries("IN", $" AND e.{q("refund_of_id")} IS NULL")})");
        // Débits ventilés sur des produits.
        ctx.Database.ExecuteSqlRaw(
            $"DELETE FROM {q("allocations")} WHERE {q("category_id")} IN ({Categories("P")}) " +
            $"AND {q("entry_id")} IN ({Entries("OUT", "")})");
    }

    /// <summary>Table <c>category_personal</c> (nom de la charge) : dépenses privées de l'exploitant, hors des tables
    /// verrouillées avec les exercices.</summary>
    private static void AddCategoryPersonal(AppDbContext ctx)
    {
        if (ServerSql.TableExists(ctx, "category_personal")) return;
        ctx.Database.ExecuteSqlRaw(ctx.Provider switch
        {
            DatabaseProvider.Sqlite => """CREATE TABLE "category_personal" ("name" TEXT NOT NULL CONSTRAINT "PK_category_personal" PRIMARY KEY);""",
            DatabaseProvider.PostgreSql => """CREATE TABLE "category_personal" ("name" varchar(200) NOT NULL CONSTRAINT "PK_category_personal" PRIMARY KEY);""",
            DatabaseProvider.MariaDb => "CREATE TABLE `category_personal` (`name` varchar(200) NOT NULL, CONSTRAINT `PK_category_personal` PRIMARY KEY (`name`));",
            DatabaseProvider.SqlServer => "CREATE TABLE [category_personal] ([name] nvarchar(200) NOT NULL CONSTRAINT [PK_category_personal] PRIMARY KEY);",
            _ => throw new NotSupportedException()
        });
    }
    // ------------------------------------------------------------------ v11 : statut Développeur

    /// <summary>Colonne <c>users.is_developer</c> : statut qui s'ajoute au rôle (aucun compte existant ne l'a).</summary>
    private static void AddDeveloperFlag(AppDbContext ctx)
    {
        if (ServerSql.ColumnExists(ctx, "users", "is_developer")) return;
        ctx.Database.ExecuteSqlRaw(ctx.Provider switch
        {
            DatabaseProvider.Sqlite => """ALTER TABLE "users" ADD COLUMN "is_developer" INTEGER NOT NULL DEFAULT 0;""",
            DatabaseProvider.PostgreSql => """ALTER TABLE "users" ADD COLUMN "is_developer" boolean NOT NULL DEFAULT false;""",
            DatabaseProvider.MariaDb => "ALTER TABLE `users` ADD COLUMN `is_developer` tinyint(1) NOT NULL DEFAULT 0;",
            DatabaseProvider.SqlServer => "ALTER TABLE [users] ADD [is_developer] bit NOT NULL DEFAULT 0;",
            _ => throw new NotSupportedException()
        });
    }

    // ------------------------------------------------------------------ v10 : immobilisations

    /// <summary>Registre des immobilisations (matériel amorti sur sa durée d'utilisation). Table indépendante, hors des
    /// tables verrouillées avec les exercices ; les dotations sont calculées, jamais stockées.</summary>
    private static void AddFixedAssets(AppDbContext ctx)
    {
        if (ServerSql.TableExists(ctx, "fixed_assets")) return;
        ctx.Database.ExecuteSqlRaw(ctx.Provider switch
        {
            DatabaseProvider.Sqlite => """
                CREATE TABLE "fixed_assets" (
                    "id" INTEGER NOT NULL CONSTRAINT "PK_fixed_assets" PRIMARY KEY AUTOINCREMENT,
                    "label" TEXT NOT NULL, "acquired_on" TEXT NOT NULL, "cost_cents" INTEGER NOT NULL,
                    "years" INTEGER NOT NULL, "prorata" TEXT NOT NULL, "origin" TEXT NOT NULL,
                    "category_name" TEXT NULL, "entry_id" INTEGER NULL, "disposed_on" TEXT NULL,
                    "note" TEXT NULL, "created_at" TEXT NOT NULL
                );
                """,
            DatabaseProvider.PostgreSql => """
                CREATE TABLE "fixed_assets" (
                    "id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    "label" varchar(300) NOT NULL, "acquired_on" varchar(10) NOT NULL, "cost_cents" bigint NOT NULL,
                    "years" integer NOT NULL, "prorata" varchar(1) NOT NULL, "origin" varchar(1) NOT NULL,
                    "category_name" varchar(200) NULL, "entry_id" bigint NULL, "disposed_on" varchar(10) NULL,
                    "note" varchar(1000) NULL, "created_at" varchar(30) NOT NULL
                );
                """,
            DatabaseProvider.MariaDb => """
                CREATE TABLE `fixed_assets` (
                    `id` bigint NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    `label` varchar(300) NOT NULL, `acquired_on` varchar(10) NOT NULL, `cost_cents` bigint NOT NULL,
                    `years` int NOT NULL, `prorata` varchar(1) NOT NULL, `origin` varchar(1) NOT NULL,
                    `category_name` varchar(200) NULL, `entry_id` bigint NULL, `disposed_on` varchar(10) NULL,
                    `note` varchar(1000) NULL, `created_at` varchar(30) NOT NULL
                );
                """,
            DatabaseProvider.SqlServer => """
                CREATE TABLE [fixed_assets] (
                    [id] bigint NOT NULL IDENTITY(1,1) PRIMARY KEY,
                    [label] nvarchar(300) NOT NULL, [acquired_on] nvarchar(10) NOT NULL, [cost_cents] bigint NOT NULL,
                    [years] int NOT NULL, [prorata] nvarchar(1) NOT NULL, [origin] nvarchar(1) NOT NULL,
                    [category_name] nvarchar(200) NULL, [entry_id] bigint NULL, [disposed_on] nvarchar(10) NULL,
                    [note] nvarchar(1000) NULL, [created_at] nvarchar(30) NOT NULL
                );
                """,
            _ => throw new NotSupportedException()
        });
    }

    // ------------------------------------------------------------------ v2 : comptes utilisateurs (SQLite)

    private static void AddUsers(AppDbContext ctx)
    {
        ctx.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "users" (
                "id" INTEGER NOT NULL CONSTRAINT "PK_users" PRIMARY KEY AUTOINCREMENT,
                "user_name" TEXT NOT NULL,
                "display_name" TEXT NOT NULL,
                "password_hash" TEXT NOT NULL,
                "role" TEXT NOT NULL,
                "active" INTEGER NOT NULL,
                "must_change_password" INTEGER NOT NULL,
                "created_at" TEXT NOT NULL,
                "created_by" TEXT NULL,
                "last_login_at" TEXT NULL
            );
            """);
        ctx.Database.ExecuteSqlRaw("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_users_user_name" ON "users" ("user_name");""");
    }
}
