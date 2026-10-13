using ToutCompteFait.Core.Business;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Import;
using TomLine.Licensing;
using ToutCompteFait.Core.Licensing;
using ToutCompteFait.Core.Security;
using ToutCompteFait.Core.Services;
using Microsoft.EntityFrameworkCore;

// Utilitaire : « adduser <fichier.db> <identifiant> <mot de passe> » crée le premier administrateur (base de démonstration).
if (args.Length >= 4 && args[0] == "adduser")
{
    var af = new DbContextFactory(args[1]);
    var au = new AuthService(af).CreateFirstAdmin(args[2], args[2], args[3]);
    Console.WriteLine($"Administrateur « {au.UserName} » créé.");
    return 0;
}

// Utilitaire : « demo <fichier.db> <identifiant> <mot de passe> » crée une base de démonstration FICTIVE (administrateur,
// deux exercices dont un validé, écritures dont une annulée) pour les captures d'écran de diagnostic de l'application.
if (args.Length >= 4 && args[0] == "demo")
{
    var df = new DbContextFactory(args[1]);
    df.EnsureDatabaseUpToDate("demo");
    new AuthService(df).CreateFirstAdmin(args[2], args[2], args[3]);
    var dc = new Compta(df, Session.System);
    var y1 = dc.CreateYear(new DateOnly(2025, 9, 1), new DateOnly(2026, 8, 31));
    var bq = dc.Accounts().First(a => a.Name == "Banque");
    var cats1 = dc.Categories(y1.Id);
    var p1 = cats1.First(k => k.Kind == CategoryKind.Product);
    var c1 = cats1.First(k => k.Kind == CategoryKind.Charge);
    dc.SetOpening(y1.Id, bq.Id, 100000);
    dc.SaveEntry(new Entry(0, y1.Id, new DateOnly(2025, 10, 4), "Cotisations", true, EntryKind.Normal, bq.Id, Direction.In, null, 45000, null, null),
        new[] { new Allocation(p1.Id, 45000) });
    dc.SaveEntry(new Entry(0, y1.Id, new DateOnly(2025, 11, 12), "Assurance", true, EntryKind.Normal, bq.Id, Direction.Out, null, 18000, null, null),
        new[] { new Allocation(c1.Id, 18000) });
    dc.ValidateYear(y1.Id);
    var y2 = dc.CreateYear(new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31));
    var cats2 = dc.Categories(y2.Id);
    var p2 = cats2.First(k => k.Kind == CategoryKind.Product);
    var c2 = cats2.First(k => k.Kind == CategoryKind.Charge);
    dc.SaveEntry(new Entry(0, y2.Id, new DateOnly(2026, 9, 20), "Cotisations", false, EntryKind.Normal, bq.Id, Direction.In, null, 30000, null, null),
        new[] { new Allocation(p2.Id, 30000) });
    var wrong = dc.SaveEntry(new Entry(0, y2.Id, new DateOnly(2026, 10, 2), "Achat saisi deux fois", false, EntryKind.Normal, bq.Id, Direction.Out, null, 5000, null, null),
        new[] { new Allocation(c2.Id, 5000) });
    dc.CancelEntry(wrong, "doublon");
    dc.SaveEntry(new Entry(0, y2.Id, new DateOnly(2026, 10, 5), "Fournitures", false, EntryKind.Normal, bq.Id, Direction.Out, null, 7500, null, null),
        new[] { new Allocation(c2.Id, 7500) });
    Console.WriteLine($"Base de démonstration créée : {args[1]}");
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    return 0;
}

// Utilitaire : « pgsql <source.db> <sortie.sql> » : génère un script PostgreSQL autonome (schéma + données) à partir d'une
// base SQLite, sans se connecter à aucun serveur. La source n'est pas modifiée (migration sur une copie temporaire).
if (args.Length >= 3 && args[0] == "pgsql")
{
    var tmpDb = Path.Combine(Path.GetTempPath(), "tcf-pgsql-" + Guid.NewGuid().ToString("N") + ".db");
    File.Copy(args[1], tmpDb);
    var sqliteFactory = new DbContextFactory(tmpDb);
    sqliteFactory.EnsureDatabaseUpToDate("export-sql");
    var version = DbContextFactory.LatestVersion;

    var sbSql = new System.Text.StringBuilder();
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    sbSql.AppendLine("-- Tout Compte Fait : script PostgreSQL (schéma + données) généré le " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", inv));
    sbSql.AppendLine("-- À exécuter depuis le logiciel (Paramètres → Base de données → Exécuter un script SQL), ou avec psql -f.");
    sbSql.AppendLine("-- Tout est dans une seule transaction : en cas d'erreur, rien n'est appliqué.");
    sbSql.AppendLine("SET client_encoding = 'UTF8';");
    sbSql.AppendLine("BEGIN;");
    sbSql.AppendLine();
    sbSql.AppendLine("-- 1. Schéma de l'application (les tables existantes de l'application sont d'abord supprimées :");
    sbSql.AppendLine("--    le script fonctionne aussi sur une base déjà initialisée par le logiciel)");
    using (var scriptCtx = new ServerScriptContext(DatabaseProvider.PostgreSql))
    {
        foreach (var t in scriptCtx.Model.GetEntityTypes().Select(e => e.GetTableName()).Where(n => n is not null).Distinct())
            sbSql.AppendLine($"DROP TABLE IF EXISTS \"{t}\" CASCADE;");
        sbSql.AppendLine("DROP TABLE IF EXISTS \"DbInfo\" CASCADE;");
        sbSql.AppendLine(scriptCtx.Database.GenerateCreateScript().Trim());
    }
    sbSql.AppendLine();
    sbSql.AppendLine("-- 2. Table technique (version du schéma, verrou d'écriture)");
    sbSql.AppendLine("CREATE TABLE IF NOT EXISTS \"DbInfo\" (\"Key\" varchar(100) NOT NULL PRIMARY KEY, \"Value\" text NOT NULL);");
    sbSql.AppendLine($"INSERT INTO \"DbInfo\" (\"Key\", \"Value\") VALUES ('SchemaVersion', '{version}'), ('AppVersion', 'export-sql')");
    sbSql.AppendLine("  ON CONFLICT (\"Key\") DO UPDATE SET \"Value\" = EXCLUDED.\"Value\";");
    sbSql.AppendLine();
    sbSql.AppendLine("-- 3. Données");

    string Lit(object? v, Type clr)
    {
        if (v is null || v is DBNull) return "NULL";
        if (v is byte[] blob) return "decode('" + Convert.ToHexString(blob) + "', 'hex')";
        var t = Nullable.GetUnderlyingType(clr) ?? clr;
        if (t == typeof(bool)) return Convert.ToInt64(v, inv) != 0 ? "TRUE" : "FALSE";
        if (t == typeof(string)) return "'" + Convert.ToString(v, inv)!.Replace("'", "''") + "'";
        if (t == typeof(decimal) || t == typeof(double) || t == typeof(float)) return Convert.ToString(v, inv)!;
        if (t.IsEnum || t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte))
            return Convert.ToInt64(v, inv).ToString(inv);
        return "'" + Convert.ToString(v, inv)!.Replace("'", "''") + "'";
    }

    using (var src = sqliteFactory.Create())
    {
        var types = src.Model.GetEntityTypes().Where(e => !e.IsOwned() && e.GetTableName() is not null).ToList();
        // tri topologique par clés étrangères
        var ordered = new List<Microsoft.EntityFrameworkCore.Metadata.IEntityType>();
        var pending = new List<Microsoft.EntityFrameworkCore.Metadata.IEntityType>(types);
        while (pending.Count > 0)
        {
            var ready = pending.Where(e => e.GetForeignKeys().All(fk =>
                fk.PrincipalEntityType == e || ordered.Contains(fk.PrincipalEntityType) || !pending.Contains(fk.PrincipalEntityType))).ToList();
            if (ready.Count == 0) ready = pending.Take(1).ToList();
            foreach (var e in ready) { ordered.Add(e); pending.Remove(e); }
        }

        var conn = src.Database.GetDbConnection();
        conn.Open();
        var total = 0;
        foreach (var e in ordered)
        {
            var table = e.GetTableName()!;
            var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, e.GetSchema());
            var props = e.GetProperties().Select(p => (Name: p.GetColumnName(store)!, Clr: p.ClrType)).ToList();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT " + string.Join(", ", props.Select(p => "\"" + p.Name + "\"")) + " FROM \"" + table + "\"" +
                              (table == "entries" ? " ORDER BY (\"refund_of_id\" IS NOT NULL OR \"reversal_of_id\" IS NOT NULL), \"id\"" : "");
            var count = 0;
            sbSql.AppendLine($"-- {table}");
            using (var rd = cmd.ExecuteReader())
                while (rd.Read())
                {
                    var vals = props.Select((p, i) => Lit(rd.IsDBNull(i) ? null : rd.GetValue(i), p.Clr));
                    sbSql.AppendLine($"INSERT INTO \"{table}\" ({string.Join(", ", props.Select(p => "\"" + p.Name + "\""))}) VALUES ({string.Join(", ", vals)});");
                    count++;
                }
            total += count;
            sbSql.AppendLine($"-- {count} ligne(s)");
            var key = e.FindPrimaryKey();
            if (key is { Properties.Count: 1 } && key.Properties[0].ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd)
            {
                var col = key.Properties[0].GetColumnName(store)!;
                sbSql.AppendLine($"SELECT setval(pg_get_serial_sequence('\"{table}\"', '{col}'), COALESCE((SELECT MAX(\"{col}\") FROM \"{table}\"), 0) + 1, false);");
            }
            sbSql.AppendLine();
        }
        sbSql.AppendLine("COMMIT;");
        Console.WriteLine($"{ordered.Count} table(s), {total} ligne(s) de données.");
    }
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    File.WriteAllText(args[2], sbSql.ToString(), new System.Text.UTF8Encoding(false));
    Console.WriteLine("Script écrit : " + args[2] + " (" + new FileInfo(args[2]).Length / 1024 + " Ko)");
    return 0;
}

// Utilitaire : « pgcheck <hôte> <port> <base> <utilisateur> » (mot de passe dans TCF_DB_PASSWORD) : liste les tables
// existantes du serveur PostgreSQL, sans rien modifier.
if (args.Length >= 5 && args[0] == "pgcheck")
{
    var info = new DbConnectionInfo
    {
        Provider = DatabaseProvider.PostgreSql, Host = args[1], Port = int.Parse(args[2]), Database = args[3], UserName = args[4], UseSsl = true,
        Password = Environment.GetEnvironmentVariable("TCF_DB_PASSWORD") ?? ""
    };
    using var c = new Npgsql.NpgsqlConnection(info.BuildConnectionString());
    c.Open();
    Console.WriteLine("Serveur : PostgreSQL " + c.ServerVersion + " ; base " + info.Database + " ; utilisateur " + info.UserName);
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT table_schema, table_name FROM information_schema.tables " +
                      "WHERE table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1,2";
    var tables = new List<(string S, string T)>();
    using (var rd = cmd.ExecuteReader())
        while (rd.Read()) tables.Add((rd.GetString(0), rd.GetString(1)));
    Console.WriteLine($"Tables existantes : {tables.Count}");
    foreach (var (s, tn) in tables)
    {
        using var cnt = c.CreateCommand();
        cnt.CommandText = $"SELECT count(*) FROM \"{s}\".\"{tn}\"";
        Console.WriteLine($"  {s}.{tn} : {cnt.ExecuteScalar()} ligne(s)");
    }
    using var sc = c.CreateCommand();
    sc.CommandText = "SELECT rolcreatedb, rolsuper FROM pg_roles WHERE rolname = current_user";
    using (var rd = sc.ExecuteReader()) if (rd.Read()) Console.WriteLine($"Droits : createdb={rd.GetBoolean(0)} super={rd.GetBoolean(1)}");
    return 0;
}

// Utilitaire : « pgfill <source.db> <hôte> <port> <base> <utilisateur> » (mot de passe dans TCF_DB_PASSWORD) : copie la
// base SQLite (migrée sur une COPIE temporaire, la source n'est pas touchée) vers le serveur PostgreSQL VIDE.
if (args.Length >= 6 && args[0] == "pgfill")
{
    var srcPath = args[1];
    var tmp = Path.Combine(Path.GetTempPath(), "tcf-pgfill-" + Guid.NewGuid().ToString("N") + ".db");
    File.Copy(srcPath, tmp);
    var sf = new DbContextFactory(tmp);
    sf.EnsureDatabaseUpToDate("copie-serveur");
    var dest = new DbConnectionInfo
    {
        Provider = DatabaseProvider.PostgreSql, Host = args[2], Port = int.Parse(args[3]), Database = args[4], UserName = args[5], UseSsl = true,
        Password = Environment.GetEnvironmentVariable("TCF_DB_PASSWORD") ?? ""
    };
    var progress = new Progress<string>(s => Console.WriteLine("  " + s));
    var report = DatabaseCopier.Copy(DbTarget.ForFile(tmp), DbTarget.ForServer(dest), "copie-serveur", replaceDestination: false, progress);
    Console.WriteLine($"Copie terminée : {report.TotalRows} ligne(s) ; complète = {report.IsComplete}");
    foreach (var (table, s, d) in report.Tables) Console.WriteLine($"  {table,-28} source {s,5}  destination {d,5}");
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    return report.IsComplete ? 0 : 1;
}

// Mini exécuteur de tests (sans dépendance externe) : code de sortie 0 = tout est vrai.
var failures = 0;
var checks = 0;

void Check(bool ok, string name, string? detail = null)
{
    checks++;
    if (ok) { Console.WriteLine($"  ok    {name}"); return; }
    failures++;
    Console.WriteLine($"  ECHEC {name}{(detail is null ? "" : " -> " + detail)}");
}

void Eq<T>(T expected, T actual, string name) =>
    Check(EqualityComparer<T>.Default.Equals(expected, actual), name, $"attendu {expected}, obtenu {actual}");

void Throws(Action a, string name, string? contains = null)
{
    try { a(); Check(false, name, "aucune exception"); }
    catch (Exception ex) { Check(contains is null || ex.ToString().Contains(contains, StringComparison.OrdinalIgnoreCase), name, ex.Message); }
}

string TempDb() => Path.Combine(Path.GetTempPath(), "compta-test-" + Guid.NewGuid().ToString("N") + ".db");

void Cleanup(string p)
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    foreach (var f in Directory.GetFiles(Path.GetDirectoryName(p)!, Path.GetFileName(p) + "*"))
        try { File.Delete(f); } catch { /* temporaire */ }
}

DbContextFactory NewFactory(string path)
{
    var f = new DbContextFactory(path);
    f.EnsureDatabaseUpToDate("test");
    return f;
}

Compta NewCompta(DbContextFactory f, Session? s = null) => new(f, s ?? Session.System);

void Sql(DbContextFactory f, string sql, params object[] p)
{
    using var ctx = f.Create();
    ctx.Database.ExecuteSqlRaw(sql, p);
}

DateOnly Dt(int y, int m, int d) => new(y, m, d);

// ------------------------------------------------------------------ montants
Console.WriteLine("Montants");
{
    Check(Money.TryParse("1 234,56", out var c1) && c1 == 123456, "parse « 1 234,56 »");
    Check(Money.TryParse("1234.56", out var c2) && c2 == 123456, "parse « 1234.56 »");
    Check(Money.TryParse("1.234,56", out var c3) && c3 == 123456, "parse « 1.234,56 »");
    Check(Money.TryParse("-12,5", out var c4) && c4 == -1250, "parse négatif");
    Check(Money.TryParse("12 €", out var c5) && c5 == 1200, "parse « 12 € »");
    Check(!Money.TryParse("12,345", out _), "refuse 3 décimales");
    Check(!Money.TryParse("abc", out _), "refuse texte");
    Check(!Money.TryParse("1,2,3", out _), "refuse virgules multiples");
    Eq(4965L, Money.FromDouble(49.65), "FromDouble 49.65");
    Eq(1999L, Money.FromDouble(19.99), "FromDouble 19.99 (pas de dérive flottante)");
    Eq(11L, Money.FromDouble(0.1 + 0.01), "FromDouble 0.1+0.01");
}

// ------------------------------------------------------------------ moteur
Console.WriteLine("Moteur : écritures, ventilation, bilan, verrouillage");
{
    var path = TempDb();
    {
        var f = NewFactory(path);
        var c = NewCompta(f);
        var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
        var banque = c.Accounts().First(a => a.Name == "Banque");
        var livret = c.Accounts().First(a => a.Name == "Livret A");
        c.SetOpening(y.Id, banque.Id, 100000);
        c.SetOpening(y.Id, livret.Id, 50000);
        var cats = c.Categories(y.Id);
        var adh = cats.First(k => k.Kind == CategoryKind.Product && k.Name == "Adhésions");
        var loto = cats.First(k => k.Kind == CategoryKind.Product && k.Name == "Loto Octobre");
        var lotoC = cats.First(k => k.Kind == CategoryKind.Charge && k.Name == "Loto Octobre");
        var assur = cats.First(k => k.Kind == CategoryKind.Charge && k.Name == "Assurance");

        c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 3), "Adhésions + loto", false, EntryKind.Normal, banque.Id, Direction.In, null, 30000, null, null),
            new[] { new Allocation(adh.Id, 10000), new Allocation(loto.Id, 20000) });
        c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 5), "Lots loto", true, EntryKind.Normal, banque.Id, Direction.Out, null, 7550, null, null),
            new[] { new Allocation(lotoC.Id, 7550) });
        c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 6), "Virement livret", true, EntryKind.Transfer, banque.Id, Direction.Out, livret.Id, 20000, null, null),
            Array.Empty<Allocation>());
        c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 7), "Remb. achat", false, EntryKind.MemberRefund, banque.Id, Direction.Out, null, 1200, "Julien", null),
            new[] { new Allocation(assur.Id, 1200) });
        c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 9), "Avoir fournisseur", false, EntryKind.Normal, banque.Id, Direction.In, null, 500, null, null),
            new[] { new Allocation(lotoC.Id, -500) });

        var b = c.ComputeBilan(y.Id);
        var bank = b.Accounts.First(a => a.Account.Id == banque.Id);
        var liv = b.Accounts.First(a => a.Account.Id == livret.Id);
        Eq(100000L + 30000 - 7550 - 20000 - 1200 + 500, bank.Closing, "solde banque");
        Eq(50000L + 20000, liv.Closing, "solde livret (virement)");
        Eq(30000L, b.ProductsTotal, "total produits");
        Eq(7550L - 500 + 1200, b.ChargesTotal, "total charges (avoir déduit)");
        Eq(0L, b.UnallocatedGap, "aucun écart de ventilation");
        Eq(0L, b.BalanceDifference, "bilan équilibré");
        Eq(b.Result, b.ClosingTotal - b.OpeningTotal, "résultat = variation de trésorerie");

        var op = c.AddOperation(y.Id, "Loto Octobre");
        c.SetCategoryOperation(loto.Id, op.Id);
        c.SetCategoryOperation(lotoC.Id, op.Id);
        var opr = c.ComputeBilan(y.Id).Operations.Single();
        Eq(20000L, opr.ProductsTotal, "opération : produits liés");
        Eq(7050L, opr.ChargesTotal, "opération : charges liées");
        Eq(12950L, opr.Result, "opération : rentabilité");

        Throws(() => c.SaveEntry(new Entry(0, y.Id, Dt(2026, 11, 1), "x", false, EntryKind.Normal, banque.Id, Direction.Out, null, 1000, null, null),
            new[] { new Allocation(assur.Id, 900) }), "refuse ventilation ≠ montant", "ventiler");
        Throws(() => c.SaveEntry(new Entry(0, y.Id, Dt(2027, 9, 1), "x", false, EntryKind.Normal, banque.Id, Direction.Out, null, 1000, null, null),
            new[] { new Allocation(assur.Id, 1000) }), "refuse date hors exercice", "hors de l'exercice");
        Throws(() => c.SaveEntry(new Entry(0, y.Id, Dt(2026, 11, 1), "x", false, EntryKind.Transfer, banque.Id, Direction.Out, banque.Id, 1000, null, null),
            Array.Empty<Allocation>()), "refuse virement vers le même compte");
        Throws(() => c.SaveEntry(new Entry(0, y.Id, Dt(2026, 11, 1), "x", false, EntryKind.MemberRefund, banque.Id, Direction.Out, null, 1000, "", null),
            new[] { new Allocation(assur.Id, 1000) }), "refuse remboursement sans membre", "membre");
        Throws(() => c.SaveEntry(new Entry(0, y.Id, Dt(2026, 11, 1), "x", false, EntryKind.Normal, banque.Id, Direction.Out, null, 0, null, null),
            Array.Empty<Allocation>()), "refuse montant nul");
        Throws(() => c.DeleteCategory(assur.Id), "refuse suppression d'une catégorie utilisée", "utilisée");

        // écriture non ventilée : visible, bloque la validation
        var nvId = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 11, 2), "non ventilé", false, EntryKind.Normal, banque.Id, Direction.Out, null, 1000, null, null),
            Array.Empty<Allocation>(), strict: false);
        var bad = c.ComputeBilan(y.Id);
        Eq(-1000L, bad.UnallocatedGap, "écart de ventilation visible");
        Eq(1, bad.EntriesToFill, "écriture à ventiler comptée");
        Eq(0L, bad.BalanceDifference, "bilan équilibré malgré l'écart (affiché au passif)");
        Throws(() => c.ValidateYear(y.Id), "validation refusée avec écart", "classer");
        c.CancelEntry(nvId, "saisie erronée");

        c.ValidateYear(y.Id);
        Check(c.Year(y.Id)!.Validated, "exercice validé");
        Check(c.VerifyIntegrity(y.Id), "empreinte valide");
        var anyEntry = c.Entries(y.Id).First().Entry;
        Throws(() => c.SaveEntry(new Entry(0, y.Id, Dt(2026, 11, 3), "après validation", false, EntryKind.Normal, banque.Id, Direction.Out, null, 100, null, null),
            new[] { new Allocation(assur.Id, 100) }), "ajout refusé (service)", "validé");
        Throws(() => c.CancelEntry(anyEntry.Id), "annulation refusée (service)", "validé");
        Throws(() => c.AddCategory(y.Id, CategoryKind.Charge, "Nouvelle"), "catégorie refusée (service)", "validé");
        Throws(() => c.SetPointed(anyEntry.Id, false), "pointage refusé (service)", "validé");

        // Défense en profondeur : couche EF (garde SaveChanges) puis déclencheurs SQLite.
        using (var ctx = f.Create())
        {
            var e = ctx.Entries.First();
            e.AmountCents += 1;
            Throws(() => ctx.SaveChanges(), "modification refusée (garde EF sur SaveChanges)", "validé");
        }
        Throws(() => Sql(f, "DELETE FROM entries WHERE year_id = {0}", y.Id), "suppression refusée (trigger SQL)");
        Throws(() => Sql(f, "UPDATE entries SET amount_cents = 1 WHERE year_id = {0}", y.Id), "modification refusée (trigger SQL)", "validé");
        Throws(() => Sql(f, "INSERT INTO allocations(entry_id,category_id,amount_cents) SELECT id,{1},5 FROM entries WHERE year_id={0} LIMIT 1", y.Id, assur.Id),
            "ajout de ventilation refusé (trigger SQL)", "validé");
        Throws(() => Sql(f, "UPDATE allocations SET amount_cents = amount_cents + 1"), "modification de ventilation refusée (trigger SQL)", "validé");
        Throws(() => Sql(f, "DELETE FROM categories WHERE year_id = {0}", y.Id), "suppression catégories refusée (trigger SQL)", "validé");
        Throws(() => Sql(f, "DELETE FROM fiscal_years WHERE id = {0}", y.Id), "suppression exercice refusée (trigger SQL)", "validé");

        // Altération hors logiciel (déclencheur retiré) : l'empreinte la détecte.
        Sql(f, "DROP TRIGGER lock_entries_upd");
        Sql(f, "UPDATE entries SET amount_cents = amount_cents + 1 WHERE id = {0}", anyEntry.Id);
        Check(!c.VerifyIntegrity(y.Id), "empreinte : altération détectée");

        // La mise à jour de la base recrée les déclencheurs manquants.
        f.EnsureDatabaseUpToDate("test");
        Throws(() => Sql(f, "UPDATE entries SET amount_cents = 1 WHERE year_id = {0}", y.Id), "déclencheurs recréés à l'ouverture", "validé");
    }
    Cleanup(path);
}

// ------------------------------------------------------------------ reports entre exercices
Console.WriteLine("Moteur : report automatique d'un exercice à l'autre");
{
    var path = TempDb();
    {
        var f = NewFactory(path);
        var c = NewCompta(f);
        var y1 = c.CreateYear(Dt(2025, 9, 1), Dt(2026, 8, 31));
        var banque = c.Accounts().First(a => a.Name == "Banque");
        c.SetOpening(y1.Id, banque.Id, 100000);
        var cats1 = c.Categories(y1.Id);
        var lotoN1 = cats1.First(k => k.Kind == CategoryKind.Charge && k.Name == "Loto n+1");
        var adh = cats1.First(k => k.Kind == CategoryKind.Product && k.Name == "Adhésions");

        c.SaveEntry(new Entry(0, y1.Id, Dt(2026, 6, 1), "Lots loto octobre 2026", false, EntryKind.Normal, banque.Id, Direction.Out, null, 25499, null, null),
            new[] { new Allocation(lotoN1.Id, 25499) });
        c.SaveEntry(new Entry(0, y1.Id, Dt(2026, 6, 2), "Adhésion", false, EntryKind.Normal, banque.Id, Direction.In, null, 4000, null, null),
            new[] { new Allocation(adh.Id, 4000) });

        c.AddAdjustment(y1.Id, AdjustmentType.ToNext, CategoryKind.Charge, lotoN1.Id, "Lots loto", 25499, targetName: "Loto Octobre");
        var b1 = c.ComputeBilan(y1.Id);
        Eq(0L, b1.Charges.First(x => x.Name == "Loto n+1").Total, "charge « Loto n+1 » neutralisée par le report");
        Eq(4000L, b1.Result, "résultat N : le lot ne pèse pas sur N");
        Eq(0L, b1.BalanceDifference, "bilan N équilibré");
        Eq(-25499L, b1.Report, "report au passif = charges payées d'avance (en moins)");

        var y2 = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
        var adj2 = c.Adjustments(y2.Id);
        Eq(1, adj2.Count, "report créé automatiquement dans l'exercice suivant");
        Eq(AdjustmentType.FromPrev, adj2[0].Type, "type = réglé l'an dernier");
        var lotoOct2 = c.Categories(y2.Id).First(k => k.Kind == CategoryKind.Charge && k.Name == "Loto Octobre");
        Eq(lotoOct2.Id, adj2[0].CategoryId ?? -1, "affecté automatiquement à « Loto Octobre »");
        Eq(25499L, adj2[0].AmountCents, "montant reporté");
        Eq(100000L + 4000 - 25499, c.Opening(y2.Id, banque.Id), "ouverture N+1 = clôture N (automatique)");
        Throws(() => c.SetOpening(y2.Id, banque.Id, 5), "ouverture N+1 non modifiable à la main", "automatiquement");
        Throws(() => c.DeleteAdjustment(adj2[0].Id), "ligne reportée non supprimable ici", "source");
        var b2 = c.ComputeBilan(y2.Id);
        Eq(25499L, b2.Charges.First(x => x.Name == "Loto Octobre").Total, "charge « Loto Octobre » N+1 = lots payés l'an dernier");
        Eq(0L, b2.BalanceDifference, "bilan N+1 équilibré");

        var srcAdj = c.Adjustments(y1.Id).First();
        c.UpdateAdjustment(srcAdj.Id, srcAdj.CategoryId, srcAdj.Label, 20000, "Loto Octobre", null);
        Eq(20000L, c.Adjustments(y2.Id)[0].AmountCents, "modification propagée");
        var tomb2 = c.Categories(y2.Id).First(k => k.Kind == CategoryKind.Charge && k.Name == "Tombola");
        c.UpdateAdjustment(c.Adjustments(y2.Id)[0].Id, tomb2.Id, "", 0, null, null);
        c.SyncAllCarries();
        Eq(tomb2.Id, c.Adjustments(y2.Id)[0].CategoryId ?? -1, "choix manuel conservé après resynchronisation");
        c.UpdateAdjustment(c.Adjustments(y2.Id)[0].Id, lotoOct2.Id, "", 0, null, null);

        Throws(() => c.ValidateYear(y2.Id), "N+1 ne peut pas être validé avant N", "précédent");
        c.ValidateYear(y1.Id);
        c.SyncCarry(y1.Id);
        Eq(20000L, c.Adjustments(y2.Id)[0].AmountCents, "report toujours là après validation de N");
        Throws(() => c.UpdateAdjustment(srcAdj.Id, srcAdj.CategoryId, "x", 1, null, null), "régularisation de N verrouillée", "validé");
        Throws(() => c.ReopenYear(y1.Id, ""), "réouverture exige un motif", "motif");
        c.ReopenYear(y1.Id, "erreur de saisie");
        Check(c.Year(y1.Id)!.Open, "exercice rouvert");
        Check(c.AuditLog().Any(l => l.Action.Contains("rouvert")), "réouverture tracée");

        // Suppression de la source : la copie reportée disparaît aussi (clé étrangère sans cascade côté base).
        c.DeleteAdjustment(srcAdj.Id);
        Eq(0, c.Adjustments(y2.Id).Count, "suppression de la source : report retiré de l'exercice suivant");
    }
    Cleanup(path);
}

// ------------------------------------------------------------------ comparaison entre exercices
Console.WriteLine("Comparaison entre exercices");
{
    var path = TempDb();
    {
        var f = NewFactory(path);
        var c = NewCompta(f);
        var y1 = c.CreateYear(Dt(2025, 9, 1), Dt(2026, 8, 31));
        var banque = c.Accounts().First(a => a.Name == "Banque");
        var adh1 = c.Categories(y1.Id).First(k => k.Kind == CategoryKind.Product && k.Name == "Adhésions");
        var lot1 = c.Categories(y1.Id).First(k => k.Kind == CategoryKind.Charge && k.Name == "Tombola");
        c.SaveEntry(new Entry(0, y1.Id, Dt(2026, 1, 5), "Adhésion", false, EntryKind.Normal, banque.Id, Direction.In, null, 4000, null, null),
            new[] { new Allocation(adh1.Id, 4000) });
        c.SaveEntry(new Entry(0, y1.Id, Dt(2026, 2, 5), "Lots", false, EntryKind.Normal, banque.Id, Direction.Out, null, 1500, null, null),
            new[] { new Allocation(lot1.Id, 1500) });
        var y2 = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
        var adh2 = c.Categories(y2.Id).First(k => k.Kind == CategoryKind.Product && k.Name == "Adhésions");
        c.SaveEntry(new Entry(0, y2.Id, Dt(2027, 1, 5), "Adhésion", false, EntryKind.Normal, banque.Id, Direction.In, null, 6000, null, null),
            new[] { new Allocation(adh2.Id, 6000) });

        var one = c.CompareYears(y2.Id, 1);
        Eq(1, one.Years.Count, "comparaison sur 1 exercice");
        var two = c.CompareYears(y2.Id, 2);
        Eq(2, two.Years.Count, "comparaison sur 2 exercices");
        Eq(y1.Label, two.Years[0].Label, "ordre chronologique : le plus ancien d'abord");
        var cmp = c.CompareYears(y2.Id, 5);
        Eq(2, cmp.Years.Count, "plus d'exercices demandés que disponibles : tous ceux qui existent");
        var adhLine = cmp.Products.First(l => l.Name == "Adhésions");
        Eq(4000L, adhLine.Values[0], "adhésions N-1");
        Eq(6000L, adhLine.Values[1], "adhésions N");
        Eq(2000L, adhLine.Delta, "écart N / N-1");
        Eq(50.0m, adhLine.Percent ?? -1, "écart en %");
        var res = cmp.Summary.First(l => l.Name == YearComparison.ResultName);
        Eq(2500L, res.Values[0], "résultat N-1");
        Eq(6000L, res.Values[1], "résultat N");
        Check(cmp.Charges.Any(l => l.Name == "Tombola" && l.Values[0] == 1500 && l.Values[1] == 0), "charge présente seulement en N-1");
        Check(cmp.Charges.All(l => !l.IsZero), "lignes entièrement nulles masquées");
        Eq(c.ComputeBilan(y2.Id).Result, res.Values[1], "synthèse = résultat du bilan");
    }
    Cleanup(path);
}

// ------------------------------------------------------------------ comptes utilisateurs et rôles
Console.WriteLine("Comptes utilisateurs, mots de passe et rôles");
{
    var path = TempDb();
    {
        var f = NewFactory(path);
        var auth = new AuthService(f);
        Check(!auth.HasUsers(), "base neuve : aucun compte");
        Throws(() => auth.CreateFirstAdmin("ab", "X", "motdepasse1"), "identifiant trop court refusé", "identifiant");
        Throws(() => auth.CreateFirstAdmin("admin", "X", "court1"), "mot de passe trop court refusé", "8 caractères");
        Throws(() => auth.CreateFirstAdmin("admin", "X", "motdepassesanschiffre"), "mot de passe sans chiffre refusé", "chiffre");
        var admin = auth.CreateFirstAdmin("admin", "Administrateur", "Secret2026!");
        Eq(Role.Admin, admin.Role, "premier compte = administrateur");
        Throws(() => auth.CreateFirstAdmin("autre", "Y", "Secret2026!"), "création du premier compte impossible s'il en existe", "existent déjà");

        // Le mot de passe n'est jamais en clair en base.
        using (var ctx = f.Create())
        {
            var stored = ctx.Users.AsNoTracking().First().PasswordHash;
            Check(stored.StartsWith("PBKDF2-SHA256$") && !stored.Contains("Secret2026!"), "mot de passe haché (PBKDF2), jamais en clair");
        }

        Check(auth.TryLogin("admin", "Secret2026!", out _) is { } u && u.Role == Role.Admin, "connexion correcte");
        Check(auth.TryLogin("ADMIN", "Secret2026!", out _) is not null, "identifiant insensible à la casse");
        Check(auth.TryLogin("admin", "mauvais", out var err) is null && err!.Contains("incorrect"), "mauvais mot de passe refusé");
        Check(auth.TryLogin("inconnu", "Secret2026!", out var err2) is null && err2!.Contains("incorrect"), "identifiant inconnu : même message");

        // Blocage après 5 échecs consécutifs.
        for (var i = 0; i < 5; i++) auth.TryLogin("admin", "faux" + i, out _);
        Check(auth.TryLogin("admin", "Secret2026!", out var err3) is null && err3!.Contains("Trop de tentatives"), "blocage après 5 échecs");

        var adminSession = new Session(admin.Id, admin.UserName, admin.DisplayName, admin.Role);
        var ca = NewCompta(f, adminSession);
        var tre = ca.CreateUser("tresorier", "Le trésorier", Role.Treasurer, "Tresor2026");
        var lec = ca.CreateUser("lecteur", "Le lecteur", Role.Reader, "Lecteur2026");
        Check(tre.MustChangePassword && lec.MustChangePassword, "mot de passe provisoire à changer");
        Throws(() => ca.CreateUser("Tresorier", "Doublon", Role.Reader, "Doublon2026"), "identifiant déjà pris (casse ignorée)", "existe déjà");
        Eq(3, ca.Users().Count, "3 comptes");

        // Dernier administrateur protégé.
        Throws(() => ca.UpdateUser(admin.Id, "Admin", Role.Treasurer, true), "dernier admin non rétrogradable", "dernier administrateur");
        Throws(() => ca.UpdateUser(admin.Id, "Admin", Role.Admin, false), "dernier admin non désactivable", "dernier administrateur");

        // Matrice des droits sur de vraies opérations.
        var ct = NewCompta(f, new Session(tre.Id, tre.UserName, tre.DisplayName, tre.Role));
        var cl = NewCompta(f, new Session(lec.Id, lec.UserName, lec.DisplayName, lec.Role));
        var y = ct.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
        var banque = ct.Accounts().First();
        var cat = ct.Categories(y.Id).First(k => k.Kind == CategoryKind.Charge);
        var id = ct.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 1), "Achat", false, EntryKind.Normal, banque.Id, Direction.Out, null, 1000, null, null),
            new[] { new Allocation(cat.Id, 1000) });
        Check(id > 0, "trésorier : saisie autorisée");
        Throws(() => ct.CreateUser("intrus", "X", Role.Admin, "Intrus2026"), "trésorier : création de compte refusée", "ne permet pas");
        Throws(() => ct.Users(), "trésorier : liste des comptes refusée", "ne permet pas");
        Throws(() => ct.UpdateUser(lec.Id, "L", Role.Admin, true), "trésorier : changement de rôle refusé", "ne permet pas");
        Throws(() => ct.ResetPassword(lec.Id, "Nouveau2026"), "trésorier : réinitialisation refusée", "ne permet pas");
        Throws(() => cl.CreateUser("intrus", "X", Role.Admin, "Intrus2026"), "lecteur : création de compte refusée", "ne permet pas");
        Throws(() => cl.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 2), "x", false, EntryKind.Normal, banque.Id, Direction.Out, null, 100, null, null),
            new[] { new Allocation(cat.Id, 100) }), "lecteur : saisie refusée", "ne permet pas");
        Throws(() => cl.CancelEntry(id), "lecteur : annulation refusée", "ne permet pas");
        Throws(() => cl.AddCategory(y.Id, CategoryKind.Charge, "Nouvelle"), "lecteur : catégorie refusée", "ne permet pas");
        Throws(() => cl.CreateYear(Dt(2028, 9, 1), Dt(2029, 8, 31)), "lecteur : exercice refusé", "ne permet pas");
        Eq(1, cl.Entries(y.Id).Count, "lecteur : consultation autorisée");
        Check(cl.ComputeBilan(y.Id).ChargesTotal == 1000, "lecteur : bilan consultable");

        // Validation : trésorier oui ; réouverture : administrateur seulement.
        ct.ValidateYear(y.Id);
        Throws(() => ct.ReopenYear(y.Id, "oups"), "trésorier : réouverture refusée", "ne permet pas");
        ca.ReopenYear(y.Id, "contrôle");
        Check(ca.Year(y.Id)!.Open, "administrateur : réouverture autorisée");

        // Mot de passe : réinitialisation puis changement par l'intéressé.
        ca.ResetPassword(lec.Id, "Provisoire2026");
        Check(auth.TryLogin("lecteur", "Provisoire2026", out _) is { MustChangePassword: true }, "mot de passe réinitialisé : changement exigé");
        Throws(() => cl.ChangeOwnPassword("faux", "Definitif2026"), "changement : ancien mot de passe vérifié", "incorrect");
        cl.ChangeOwnPassword("Provisoire2026", "Definitif2026");
        Check(auth.TryLogin("lecteur", "Definitif2026", out _) is { MustChangePassword: false }, "nouveau mot de passe accepté");
        Check(auth.TryLogin("lecteur", "Provisoire2026", out _) is null, "ancien mot de passe refusé");

        // Désactivation.
        ca.UpdateUser(lec.Id, "Le lecteur", Role.Reader, false);
        Check(auth.TryLogin("lecteur", "Definitif2026", out var err4) is null && err4!.Contains("désactivé"), "compte désactivé : connexion refusée");
    }
    Cleanup(path);
}

// ------------------------------------------------------------------ lecture seule
Console.WriteLine("Lecture seule");
{
    var path = TempDb();
    {
        var f = NewFactory(path);
        var c = NewCompta(f);
        c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
        new AuthService(f).CreateFirstAdmin("admro", "Admin", "Secret2026!");
    }
    {
        var ro = new DbContextFactory(path, readOnly: true);
        var c = new Compta(ro, Session.System);
        Eq(1, c.Years().Count, "lecture possible");
        Throws(() => c.AddCategory(c.Years()[0].Id, CategoryKind.Charge, "x"), "écriture refusée en lecture seule", "lecture seule");
        Throws(() => Sql(ro, "DELETE FROM accounts"), "écriture SQL refusée en lecture seule");
        Check(new AuthService(ro).TryLogin("admro", "Secret2026!", out _) is not null, "connexion possible sur une base en lecture seule");
        Throws(() => new AuthService(ro).CreateFirstAdmin("autre", "X", "Secret2026!"), "création de compte impossible en lecture seule", "lecture seule");
    }
    Cleanup(path);
}

// ------------------------------------------------------------------ import du relevé bancaire
Console.WriteLine("Import du relevé bancaire");
{
    // Analyse des fichiers
    var csv = "Compte;FR76 1234;;;\nDate;Libellé;Débit;Crédit;Solde\n05/01/2026;CB AMAZON FACT 030126;139,99;;1 000,00\n" +
              "07/01/2026;VIR STRIPE ADHESION;;1 021,90;2 021,90\n\"08/01/2026\";\"LOYER \"\"GARAGE\"\"\";60,00;;1 961,90\nTotal;;199,99;1 021,90;\n";
    var table = BankStatementParser.ReadCsv(csv);
    Eq(';', table.Delimiter, "séparateur « ; » détecté");
    var map = BankStatementParser.DetectMapping(table);
    Check(map is { DebitColumn: 2, CreditColumn: 3, DateColumn: 0, LabelColumn: 1 }, "colonnes débit/crédit reconnues par les titres");
    var warns = new List<string>();
    var parsed = BankStatementParser.ParseCsv(table, map!, warns);
    Eq(3, parsed.Count, "3 opérations lues (pied de fichier ignoré)");
    Eq(-13999L, parsed[0].AmountCents, "débit = sortie négative");
    Eq(102190L, parsed[1].AmountCents, "crédit = entrée positive");
    Eq("LOYER \"GARAGE\"", parsed[2].Label, "guillemets échappés");
    Eq(Dt(2026, 1, 5), parsed[0].Date, "date dd/MM/yyyy");

    var csv2 = "Date,Description,Amount\n2026-02-01,Carte ASSO,-12.50\n2026-02-02,Remboursement,30.00\n";
    var t2 = BankStatementParser.ReadCsv(csv2);
    var m2 = BankStatementParser.DetectMapping(t2);
    var p2 = BankStatementParser.ParseCsv(t2, m2!, new List<string>());
    Check(p2.Count == 2 && p2[0].AmountCents == -1250 && p2[1].AmountCents == 3000, "CSV international (virgule, montant signé)");

    var ofx = "<OFX><BANKTRANLIST><STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260110120000<TRNAMT>-45.20<FITID>A1<NAME>CB LECLERC<MEMO>FACT 090126</STMTTRN>" +
              "<STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260111<TRNAMT>100.00<FITID>A2<NAME>VIR LOTO</STMTTRN></BANKTRANLIST></OFX>";
    var po = BankStatementParser.ParseOfx(ofx);
    Check(po.Count == 2 && po[0].AmountCents == -4520 && po[0].ExternalId == "A1" && po[0].Label.Contains("LECLERC"), "OFX lu (montant, identifiant, libellé)");

    // Rapprochement
    var path = TempDb();
    {
        var f = NewFactory(path);
        var c = NewCompta(f);
        var y = c.CreateYear(Dt(2026, 1, 1), Dt(2026, 12, 31));
        var y0 = c.CreateYear(Dt(2025, 1, 1), Dt(2025, 12, 31));
        var banque = c.Accounts().First(a => a.Name == "Banque");
        var cats = c.Categories(y.Id);
        var charge = cats.First(k => k.Kind == CategoryKind.Charge && k.Name == "Investissement");
        var produit = cats.First(k => k.Kind == CategoryKind.Product && k.Name == "Adhésions");

        // Écriture déjà saisie à la main avec un libellé « maison », ventilée.
        var existingId = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 1, 5), "achat amazon", false, EntryKind.Normal, banque.Id, Direction.Out, null, 13999, null, null),
            new[] { new Allocation(charge.Id, 13999) });

        var lines = new List<BankLine>
        {
            new(Dt(2026, 1, 5), "CB AMAZON FACT 030126", -13999),     // existe : intitulé mis à jour
            new(Dt(2026, 1, 7), "VIR STRIPE ADHESION", 102190),       // nouvelle entrée
            new(Dt(2026, 1, 8), "LOYER GARAGE", -6000),               // nouvelle sortie
            new(Dt(2027, 3, 1), "HORS EXERCICE", -100),               // aucun exercice
        };
        var plan = c.PlanBankImport(banque.Id, lines);
        Eq(BankLineStatus.LabelUpdated, plan.Lines[0].Status, "ligne existante : intitulé à mettre à jour");
        Eq(BankLineStatus.New, plan.Lines[1].Status, "ligne inconnue : nouvelle");
        Eq(BankLineStatus.NoYear, plan.Lines[3].Status, "date hors de tout exercice : ignorée");
        Eq(0, c.Entries(y.Id).Count(r => r.Entry.Label == "VIR STRIPE ADHESION"), "le plan ne modifie rien");

        var res = c.ApplyBankImport(plan, markPointed: true);
        Eq(2, res.Created, "2 lignes créées");
        Eq(1, res.LabelsUpdated, "1 intitulé mis à jour");
        Eq(1, res.Skipped, "1 ligne ignorée");
        var rows = c.Entries(y.Id);
        var upd = rows.First(r => r.Entry.Id == existingId);
        Eq("CB AMAZON FACT 030126", upd.Entry.Label, "intitulé remplacé par celui de la banque");
        Eq(1, upd.Allocations.Count, "ventilation existante conservée");
        Check(upd.Entry.Pointed, "écriture retrouvée pointée");
        var created = rows.Where(r => r.Entry.Label is "VIR STRIPE ADHESION" or "LOYER GARAGE").ToList();
        Check(created.All(r => r.Allocations.Count == 0 && Compta.NeedsAllocation(r) && r.Entry.Pointed), "nouvelles lignes sans produit ni charge, à ventiler");
        Eq(Direction.In, created.First(r => r.Entry.Label == "VIR STRIPE ADHESION").Entry.Direction, "crédit = entrée");
        Eq(2, c.ComputeBilan(y.Id).EntriesToFill, "bilan : 2 écritures à ventiler");
        Check(c.ValidationIssues(y.Id).Any(i => i.Blocking && i.Message.Contains("classer")), "validation bloquée tant qu'elles ne sont pas classées");

        // Deuxième import du même relevé : tout est reconnu, rien n'est créé.
        var again = c.PlanBankImport(banque.Id, lines.Take(3).ToList());
        Check(again.Lines.All(l => l.Status == BankLineStatus.Unchanged), "ré-import : tout est déjà à jour");
        var res2 = c.ApplyBankImport(again, true);
        Eq(0, res2.Created, "ré-import : aucune création");
        Eq(rows.Count, c.Entries(y.Id).Count, "ré-import : nombre d'écritures inchangé");

        // Affectation directe depuis la liste
        var stripe = created.First(r => r.Entry.Label == "VIR STRIPE ADHESION").Entry;
        var loyer = created.First(r => r.Entry.Label == "LOYER GARAGE").Entry;
        c.AssignCategory(stripe.Id, produit.Id);
        c.AssignCategory(loyer.Id, charge.Id);
        var after = c.Entries(y.Id);
        Eq(102190L, after.First(r => r.Entry.Id == stripe.Id).Allocations.Single().AmountCents, "entrée classée en produit : montant positif");
        Eq(6000L, after.First(r => r.Entry.Id == loyer.Id).Allocations.Single().AmountCents, "sortie classée en charge : montant positif");
        Eq(0L, c.ComputeBilan(y.Id).UnallocatedGap, "tout est ventilé après affectation");
        Eq(0, c.ComputeBilan(y.Id).EntriesToFill, "plus rien à ventiler");
        Throws(() => c.AssignCategory(stripe.Id, produit.Id), "affectation rapide refusée si déjà ventilée", "déjà ventilée");

        // Une entrée d'argent classée en charge devient un avoir (montant négatif).
        var avoirPlan = c.PlanBankImport(banque.Id, new[] { new BankLine(Dt(2026, 2, 1), "REMBOURSEMENT AMAZON", 2500) });
        c.ApplyBankImport(avoirPlan, true);
        var av = c.Entries(y.Id).First(r => r.Entry.Label == "REMBOURSEMENT AMAZON").Entry;
        c.AssignCategory(av.Id, charge.Id);
        Eq(-2500L, c.Entries(y.Id).First(r => r.Entry.Id == av.Id).Allocations.Single().AmountCents, "entrée classée en charge = avoir (négatif)");
        Eq(0L, c.ComputeBilan(y.Id).UnallocatedGap, "avoir cohérent avec la trésorerie");

        // Écriture importée de la banque : le montant fait foi et ne peut plus être modifié.
        var avRow = c.Entries(y.Id).First(r => r.Entry.Id == av.Id);
        Check(avRow.Entry.FromBank, "écriture importée : marquée « banque »");
        Throws(() => c.SaveEntry(avRow.Entry with { AmountCents = 2600 }, avRow.Allocations), "montant d'une écriture bancaire verrouillé", "relevé fait foi");
        c.SaveEntry(avRow.Entry with { Label = "REMBOURSEMENT AMAZON (note)" }, avRow.Allocations);
        Eq("REMBOURSEMENT AMAZON (note)", c.Entries(y.Id).First(r => r.Entry.Id == av.Id).Entry.Label, "le libellé reste modifiable");

        // Exercice validé : plus d'import dessus
        c.SaveEntry(new Entry(0, y0.Id, Dt(2025, 3, 1), "vieux", false, EntryKind.Normal, banque.Id, Direction.Out, null, 500, null, null),
            new[] { new Allocation(c.Categories(y0.Id).First(k => k.Kind == CategoryKind.Charge).Id, 500) });
        c.ValidateYear(y0.Id);
        var pv = c.PlanBankImport(banque.Id, new[] { new BankLine(Dt(2025, 6, 1), "ANCIEN", -500) });
        Eq(BankLineStatus.YearValidated, pv.Lines[0].Status, "exercice validé : ligne non importée");
        Eq(1, c.ApplyBankImport(pv, true).Skipped, "exercice validé : ignorée à l'application");

        // Droits : un lecteur ne peut pas importer
        var reader = NewCompta(f, new Session(9, "lecteur", "Lecteur", Role.Reader));
        Throws(() => reader.PlanBankImport(banque.Id, lines), "lecteur : import du relevé refusé", "ne permet pas");
    }
    Cleanup(path);
}

// ------------------------------------------------------------------ import du relevé : dates approximatives
Console.WriteLine("Import du relevé bancaire : date légèrement différente (validation par l'utilisateur)");
{
    var path = TempDb();
    {
        var f = NewFactory(path);
        var c = NewCompta(f);
        var y = c.CreateYear(Dt(2026, 1, 1), Dt(2026, 12, 31));
        var banque = c.Accounts().First(a => a.Name == "Banque");
        var charge = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Charge && k.Name == "Investissement");
        long Add(DateOnly d, string label, long cents) => c.SaveEntry(
            new Entry(0, y.Id, d, label, false, EntryKind.Normal, banque.Id, Direction.Out, null, cents, null, null),
            new[] { new Allocation(charge.Id, cents) });

        var near = Add(Dt(2026, 3, 10), "achat matériel", 4500);       // saisi le 10, débité le 12
        var exact = Add(Dt(2026, 3, 20), "facture exacte", 1000);       // même date côté banque
        var other = Add(Dt(2026, 3, 22), "autre facture", 1000);        // même montant, 2 jours plus tard
        var far = Add(Dt(2026, 6, 1), "trop loin", 7700);               // 40 jours d'écart

        var lines = new List<BankLine>
        {
            new(Dt(2026, 3, 12), "CB MATERIEL PRO FACT 100326", -4500),  // douteux : 2 jours d'écart
            new(Dt(2026, 3, 20), "PRLV FACTURE", -1000),                 // date exacte : sûr, ne doit pas voler « autre facture »
            new(Dt(2026, 7, 10), "VIREMENT LOIN", -7700),                // 40 jours : trop loin -> nouvelle
        };
        var plan = c.PlanBankImport(banque.Id, lines);
        Eq(BankLineStatus.ToConfirm, plan.Lines[0].Status, "date à 2 jours : correspondance à valider");
        Eq(near, plan.Lines[0].EntryId ?? -1, "l'écriture proche est proposée");
        Check(plan.Lines[0].Candidates is { Count: 1 } && plan.Lines[0].Candidates![0].DaysApart == 2, "écart de jours indiqué");
        Eq(BankLineStatus.LabelUpdated, plan.Lines[1].Status, "date exacte : rapprochement sûr (pas de question)");
        Eq(exact, plan.Lines[1].EntryId ?? -1, "la date exacte l'emporte sur l'écriture à 2 jours");
        Eq(BankLineStatus.New, plan.Lines[2].Status, "au-delà de la tolérance : nouvelle écriture");
        Eq(1, plan.Count(BankLineStatus.ToConfirm), "une seule ligne à valider");

        // Sans décision : refus, rien n'est écrit.
        Throws(() => c.ApplyBankImport(plan, true), "import refusé tant que le doute n'est pas tranché", "incertaine");
        Eq("achat matériel", c.EntryById(near)!.Entry.Label, "rien n'a été modifié");

        // Décision invalide : une écriture qui n'est pas proposée.
        Throws(() => c.ApplyBankImport(plan, true, new Dictionary<int, BankDecision> { [0] = new(BankDecisionKind.Match, far) }),
            "rapprochement avec une écriture non proposée refusé", "proposées");

        // Rapprochement confirmé, date de saisie conservée.
        var r1 = c.ApplyBankImport(plan, true, new Dictionary<int, BankDecision> { [0] = new(BankDecisionKind.Match, near) });
        var e1 = c.EntryById(near)!;
        Eq("CB MATERIEL PRO FACT 100326", e1.Entry.Label, "intitulé de la banque appliqué après confirmation");
        Eq(Dt(2026, 3, 10), e1.Entry.Date, "date de saisie conservée par défaut");
        Eq(1, e1.Allocations.Count, "ventilation conservée");
        Eq(1, r1.Created, "la ligne trop éloignée est créée");
    }
    {
        // Les autres choix : créer une nouvelle écriture, ignorer, aligner la date.
        var f = NewFactory(TempDb());
        var c = NewCompta(f);
        var y = c.CreateYear(Dt(2026, 1, 1), Dt(2026, 12, 31));
        var banque = c.Accounts().First(a => a.Name == "Banque");
        var charge = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Charge && k.Name == "Investissement");
        long Add(DateOnly d, string label, long cents) => c.SaveEntry(
            new Entry(0, y.Id, d, label, false, EntryKind.Normal, banque.Id, Direction.Out, null, cents, null, null),
            new[] { new Allocation(charge.Id, cents) });
        var a = Add(Dt(2026, 4, 3), "achat A", 2000);
        var b = Add(Dt(2026, 4, 3), "achat B", 3000);
        var lines = new List<BankLine>
        {
            new(Dt(2026, 4, 5), "BANQUE A", -2000),
            new(Dt(2026, 4, 6), "BANQUE B", -3000),
        };
        var plan = c.PlanBankImport(banque.Id, lines);
        Eq(2, plan.Count(BankLineStatus.ToConfirm), "deux lignes à valider");
        var res = c.ApplyBankImport(plan, true, new Dictionary<int, BankDecision>
        {
            [0] = new(BankDecisionKind.Match, a),
            [1] = new(BankDecisionKind.CreateNew),
        }, alignDates: true);
        Eq(Dt(2026, 4, 5), c.EntryById(a)!.Entry.Date, "date alignée sur celle de la banque (option)");
        Eq(1, res.DatesAligned, "une date alignée");
        Eq(1, res.Created, "« créer une nouvelle écriture » crée la ligne");
        Eq(Dt(2026, 4, 3), c.EntryById(b)!.Entry.Date, "l'écriture non rapprochée n'est pas touchée");
        Eq("achat B", c.EntryById(b)!.Entry.Label, "intitulé de l'écriture non rapprochée inchangé");

        var plan2 = c.PlanBankImport(banque.Id, new[] { new BankLine(Dt(2026, 4, 7), "BANQUE B BIS", -3000) });
        Eq(BankLineStatus.ToConfirm, plan2.Lines[0].Status, "nouvelle ligne douteuse");
        var res2 = c.ApplyBankImport(plan2, true, new Dictionary<int, BankDecision> { [0] = new(BankDecisionKind.Skip) });
        Eq(1, res2.Skipped, "« ignorer » n'écrit rien");
        Eq(1, c.Entries(y.Id).Count(r => r.Entry.Label == "BANQUE B"), "aucune écriture ajoutée par l'ignorance");

        // Deux lignes du relevé ne peuvent pas se rapprocher de la même écriture.
        var plan3 = c.PlanBankImport(banque.Id, new[]
        {
            new BankLine(Dt(2026, 4, 8), "DOUBLON 1", -3000), new BankLine(Dt(2026, 4, 9), "DOUBLON 2", -3000)
        });
        Throws(() => c.ApplyBankImport(plan3, true, new Dictionary<int, BankDecision>
        {
            [0] = new(BankDecisionKind.Match, b), [1] = new(BankDecisionKind.Match, b)
        }), "même écriture rapprochée deux fois : refusé", "deux lignes");
    }
    Cleanup(path);
}
// ------------------------------------------------------------------ base existante (schéma de la première version)
Console.WriteLine("Compatibilité avec une base créée par la première version");
{
    // La base de référence est fabriquée ici en SQL brut, exactement comme le faisait la première version.
    var path = TempDb();
    using (var cn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
    {
        cn.Open();
        void Run(string sql) { using var cmd = cn.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        Run("CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        Run("INSERT INTO meta VALUES('schema_version','1')");
        Run("CREATE TABLE accounts (id INTEGER PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE UNIQUE, kind TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0, active INTEGER NOT NULL DEFAULT 1)");
        Run("CREATE TABLE fiscal_years (id INTEGER PRIMARY KEY, label TEXT NOT NULL UNIQUE, start_date TEXT NOT NULL, end_date TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'OPEN', validated_at TEXT, validated_by TEXT, checksum TEXT, closing_json TEXT)");
        Run("CREATE TABLE year_openings (year_id INTEGER NOT NULL, account_id INTEGER NOT NULL, cents INTEGER NOT NULL, PRIMARY KEY (year_id, account_id))");
        Run("CREATE TABLE operations (id INTEGER PRIMARY KEY, year_id INTEGER NOT NULL, name TEXT NOT NULL COLLATE NOCASE, position INTEGER NOT NULL DEFAULT 0)");
        Run("CREATE TABLE categories (id INTEGER PRIMARY KEY, year_id INTEGER NOT NULL, kind TEXT NOT NULL, name TEXT NOT NULL COLLATE NOCASE, position INTEGER NOT NULL DEFAULT 0, operation_id INTEGER)");
        Run("CREATE TABLE entries (id INTEGER PRIMARY KEY, year_id INTEGER NOT NULL, entry_date TEXT NOT NULL, label TEXT NOT NULL, pointed INTEGER NOT NULL DEFAULT 0, kind TEXT NOT NULL, account_id INTEGER, direction TEXT NOT NULL, to_account_id INTEGER, amount_cents INTEGER NOT NULL, member_name TEXT, note TEXT, import_ref TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL)");
        Run("CREATE TABLE allocations (id INTEGER PRIMARY KEY, entry_id INTEGER NOT NULL, category_id INTEGER NOT NULL, amount_cents INTEGER NOT NULL)");
        Run("CREATE TABLE adjustments (id INTEGER PRIMARY KEY, year_id INTEGER NOT NULL, type TEXT NOT NULL, kind TEXT NOT NULL, category_id INTEGER, label TEXT NOT NULL, amount_cents INTEGER NOT NULL, target_name TEXT, source_id INTEGER, note TEXT)");
        Run("CREATE TABLE audit_log (id INTEGER PRIMARY KEY, at TEXT NOT NULL, user_name TEXT NOT NULL, action TEXT NOT NULL, detail TEXT)");
        Run("INSERT INTO accounts VALUES (1,'Banque','BANK',1,1),(2,'Livret A','SAVINGS',2,1)");
        Run("INSERT INTO fiscal_years(id,label,start_date,end_date,status) VALUES (1,'2025-2026','2025-09-01','2026-08-31','OPEN')");
        Run("INSERT INTO year_openings VALUES (1,1,100000),(1,2,50000)");
        Run("INSERT INTO categories VALUES (1,1,'P','Adhésions',1,NULL),(2,1,'C','Garage',1,NULL)");
        Run("INSERT INTO entries VALUES (1,1,'2025-10-01','Cotisation',1,'NORMAL',1,'IN',NULL,2500,NULL,NULL,NULL,'2025-10-01T10:00:00','2025-10-01T10:00:00')");
        Run("INSERT INTO allocations VALUES (1,1,1,2500)");
    }
    {
        var f = new DbContextFactory(path);
        Eq(1, f.GetDatabaseVersion(), "base de la première version : version 1 reconnue");
        f.EnsureDatabaseUpToDate("test");
        Eq(DbContextFactory.LatestVersion, f.GetDatabaseVersion(), "mise à jour vers la dernière version");
        Check(Directory.GetFiles(DbContextFactory.BackupDirectory, "*avant-maj-v1*").Length > 0, "sauvegarde faite avant la mise à jour");
        var c = NewCompta(f);
        var y = c.Years().Single();
        var b = c.ComputeBilan(y.Id);
        Eq(2500L, b.ProductsTotal, "données conservées : produits");
        Eq(152500L, b.ClosingTotal, "données conservées : trésorerie");
        Eq(0L, b.BalanceDifference, "bilan équilibré");
        Check(!new AuthService(f).HasUsers(), "aucun compte : le premier administrateur sera à créer");
        new AuthService(f).CreateFirstAdmin("admin", "Admin", "Secret2026!");
        c.ValidateYear(y.Id);
        Throws(() => Sql(f, "UPDATE entries SET amount_cents = 1"), "exercice validé verrouillé sur la base migrée", "validé");
        f.EnsureDatabaseUpToDate("test");
        Eq(DbContextFactory.LatestVersion, f.GetDatabaseVersion(), "relance : rien à refaire");
    }
    Cleanup(path);
}

// ------------------------------------------------------------------ modèle pour les serveurs
Console.WriteLine("Modèle de données pour PostgreSQL, MariaDB et SQL Server (génération du schéma, sans serveur)");
{
    string Script(DatabaseProvider p)
    {
        using var ctx = new ServerScriptContext(p);
        return ctx.Database.GenerateCreateScript();
    }
    var tables = new[] { "accounts", "fiscal_years", "year_openings", "operations", "categories", "entries", "allocations", "adjustments", "audit_log", "users" };
    foreach (var p in new[] { DatabaseProvider.PostgreSql, DatabaseProvider.MariaDb, DatabaseProvider.SqlServer })
    {
        string s;
        try { s = Script(p); }
        catch (Exception ex) { Check(false, $"{DatabaseProviderInfo.DisplayName(p)} : génération du schéma", ex.Message); continue; }
        Check(tables.All(t => s.Contains(t, StringComparison.OrdinalIgnoreCase)), $"{DatabaseProviderInfo.DisplayName(p)} : toutes les tables générées");
        Check(s.Contains("password_hash", StringComparison.OrdinalIgnoreCase), $"{DatabaseProviderInfo.DisplayName(p)} : table des comptes utilisateurs");
    }
}

Console.WriteLine();
// ---------------------------------------------------------------------------------------------------------------------
// Lecture de relevés (PDF numérique / OCR) : pages synthétiques « comme un OCR les rendrait », réponse connue.
// ---------------------------------------------------------------------------------------------------------------------
Console.WriteLine("Lecture de relevés (OCR)");
{
    var d = (int day, int month) => new DateOnly(2026, month, day);
    var ops = new List<SampleOp>
    {
        new(d(1, 9), "VIR SEPA DUPONT COTISATION", 2500),
        new(d(2, 9), "PRLV EDF FACTURE SEPTEMBRE", -12345, "REF 987654 CONTRAT 12"),
        new(d(3, 9), "CB LECLERC 03/09", -4580),
        new(d(4, 9), "REMISE CHEQUES 0123456", 123456),
        new(d(7, 9), "VIR LOTO RECETTES", 1234567),
        new(d(8, 9), "FRAIS TENUE DE COMPTE", -1290),
        new(d(9, 9), "CHEQUE 0001234", -30000),
        new(d(10, 9), "VIR SEPA MARTIN LOCATION SALLE", 15000, "SAMEDI 12 SEPTEMBRE"),
        new(d(14, 9), "CB AMAZON", -2999),
        new(d(15, 9), "PRLV ASSURANCE MAIF", -9876),
        new(d(16, 9), "VIR PAYPAL", 4550),
        new(d(21, 9), "COTISATION 45,00 ADHESION", 4500),
        new(d(22, 9), "RETRAIT DAB", -6000),
        new(d(25, 9), "VIR SUBVENTION MAIRIE", 100000),
        new(d(28, 9), "CB BOULANGERIE", -1850),
        new(d(30, 9), "AGIOS", -123)
    };
    const long open = 150075;

    void SameRows(StatementReadResult r, string name)
    {
        Eq(ops.Count, r.Rows.Count, name + " : nombre d'opérations");
        for (var i = 0; i < Math.Min(ops.Count, r.Rows.Count); i++)
        {
            var row = r.Rows[i];
            var op = ops[i];
            var okDate = row.Date == op.Date;
            var okAmt = row.AmountCents == op.Cents;
            var expectLabel = op.Continuation is null ? op.Label : op.Label + " " + op.Continuation;
            var okLabel = row.Label == expectLabel;
            if (!(okDate && okAmt && okLabel))
            {
                Check(false, $"{name} : ligne {i + 1}", $"lu {row.Date} « {row.Label} » {row.AmountCents} ; attendu {op.Date} « {expectLabel} » {op.Cents}");
                return;
            }
        }
        Check(true, name + " : dates, libellés (multi-lignes recollés) et montants exacts");
    }

    // 1) débit / crédit, deux dates, solde courant, totaux, 2 pages, bruit de pied de page
    var p1 = StatementSample.Build(ops, open, new SampleOptions { RunningBalance = true, RowsPerPage = 9 });
    Eq(2, p1.Count, "relevé d'exemple sur 2 pages");
    var r1 = StatementLayoutParser.Parse(p1);
    SameRows(r1, "débit/crédit 2 pages");
    Eq(open, r1.OpeningCents ?? -1, "ancien solde lu");
    Eq(open + ops.Sum(x => x.Cents), r1.ClosingCents ?? -1, "nouveau solde lu");
    var c1 = StatementChecker.Evaluate(r1);
    Check(c1.State == StatementCheckState.Verified && c1.CanImport, "contrôle : relevé exact → vérifié", string.Join(" | ", c1.Details));
    Eq(-ops.Where(x => x.Cents < 0).Sum(x => x.Cents), r1.TotalDebitsCents ?? -1, "total des débits lu");
    Eq(ops.Where(x => x.Cents > 0).Sum(x => x.Cents), r1.TotalCreditsCents ?? -1, "total des crédits lu");
    Check(r1.Rows.All(r => !r.Repaired && r.Notes.Count == 0), "aucune correction silencieuse sur un relevé propre");
    Check(r1.PeriodStart is { } ps && ps <= new DateOnly(2026, 9, 1) && r1.PeriodEnd == new DateOnly(2026, 9, 30), "période détectée", PeriodText(r1));

    string PeriodText(StatementReadResult r) => $"{r.PeriodStart:dd/MM/yyyy}-{r.PeriodEnd:dd/MM/yyyy}";

    // 2) colonne Montant signée, dates sans année (année déduite de la période), une seule date
    var p2 = StatementSample.Build(ops, open, new SampleOptions { Layout = SampleAmountLayout.SignedAmount, ShortDates = true, ValueDate = false, RowsPerPage = 20 });
    var r2 = StatementLayoutParser.Parse(p2);
    SameRows(r2, "montant signé, dates sans année");
    var c2 = StatementChecker.Evaluate(r2);
    Check(c2.State == StatementCheckState.Verified, "contrôle : montant signé vérifié", string.Join(" | ", c2.Details));
    Check(!r2.Messages.Any(m => m.Contains("sans signe")), "colonne signée : pas d'alerte « sans signe »");

    // 3) année introuvable → dates incomplètes (jamais devinées) ; année fournie → complétées
    var p3 = StatementSample.Build(ops, open, new SampleOptions { ShortDates = true, BalanceLines = true });
    var noPeriod = p3.Select(pg => new StatementPage(pg.Number, pg.Width, pg.Height,
        pg.Words.Where(w => !(w.Text.StartsWith("du", StringComparison.OrdinalIgnoreCase) && w.Y < 130) && !w.Text.Contains("/2026")).ToList())).ToList();
    var r3 = StatementLayoutParser.Parse(noPeriod);
    Check(r3.Rows.Count > 0 && r3.Rows.All(r => r.Date is null), "année absente : dates laissées vides, rien n'est deviné");
    Check(StatementChecker.Evaluate(r3).IncompleteRows == r3.Rows.Count && !StatementChecker.Evaluate(r3).CanImport, "année absente : import bloqué");
    var r3b = StatementLayoutParser.Parse(noPeriod, null, 2026);
    SameRows(r3b, "année fournie par l'utilisateur");

    // 4) erreurs de lecture OCR
    List<StatementPage> Replace(List<StatementPage> pages, string from, string to)
    {
        var done = false;
        return pages.Select(pg => new StatementPage(pg.Number, pg.Width, pg.Height,
            pg.Words.Select(w => { if (!done && w.Text == from) { done = true; return w with { Text = to }; } return w; }).ToList())).ToList();
    }
    // 4a) lettre à la place d'un chiffre : corrigée mais signalée, résultat exact
    var p4a = Replace(p1, "45,80", "4S,80");
    var r4a = StatementLayoutParser.Parse(p4a);
    SameRows(r4a, "« 4S,80 » corrigé en 45,80");
    Check(r4a.Rows.Count(r => r.Repaired) == 1, "ligne corrigée par le lecteur marquée à vérifier");
    // 4b) chiffre faux : le contrôle détecte et localise
    var p4b = Replace(p1, "45,80", "46,80");
    var r4b = StatementLayoutParser.Parse(p4b);
    var c4b = StatementChecker.Evaluate(r4b);
    Check(c4b.State == StatementCheckState.Mismatch && !c4b.CanImport, "chiffre mal lu : contrôle en échec, import bloqué");
    Check(c4b.Details.Any(x => x.Contains("CB LECLERC")), "le solde courant désigne la ligne fautive", string.Join(" | ", c4b.Details));
    // correction manuelle → vérifié
    r4b.Rows.First(r => r.Label.StartsWith("CB LECLERC", StringComparison.Ordinal)).AmountCents = -4580;
    Check(StatementChecker.Evaluate(r4b).CanImport, "après correction de la ligne : import autorisé");
    // 4c) virgule décimale perdue : montant non reconnu → ligne incomplète, jamais prise pour 4580 euros
    var p4c = Replace(p1, "45,80", "4580");
    var r4c = StatementLayoutParser.Parse(p4c);
    Check(!StatementChecker.Evaluate(r4c).CanImport, "montant sans décimales : import bloqué");
    // 4d) ligne parasite décochée
    var withNoise = r1.Rows.ToList();
    var noise = new StatementRow { Date = new DateOnly(2026, 9, 5), Label = "BRUIT", AmountCents = 100, Page = 1 };
    withNoise.Add(noise);
    Check(StatementChecker.Evaluate(withNoise, r1.OpeningCents, r1.ClosingCents, r1.TotalDebitsCents, r1.TotalCreditsCents).State == StatementCheckState.Mismatch,
        "ligne parasite : contrôle en échec");
    noise.Include = false;
    Check(StatementChecker.Evaluate(withNoise, r1.OpeningCents, r1.ClosingCents, r1.TotalDebitsCents, r1.TotalCreditsCents).CanImport,
        "ligne parasite décochée : relevé de nouveau exact");

    // 5) en-têtes illisibles : colonnes déduites de la position des montants, signalé
    var p5 = p1.Select(pg => new StatementPage(pg.Number, pg.Width, pg.Height,
        pg.Words.Where(w => w.Text is not ("Débit" or "Crédit" or "Solde" or "Libellé")).ToList())).ToList();
    var r5 = StatementLayoutParser.Parse(p5);
    Check(r5.Messages.Any(m => m.Contains("déduites")), "sans en-têtes : colonnes déduites, signalé");
    Check(r5.Rows.Count == ops.Count && StatementChecker.Evaluate(r5).State == StatementCheckState.Verified,
        "sans en-têtes : lecture vérifiée par les soldes", string.Join(" | ", StatementChecker.Evaluate(r5).Details));

    // 6) ni soldes ni totaux : non vérifiable → contrôle manuel exigé
    var p6 = StatementSample.Build(ops, open, new SampleOptions { BalanceLines = false, Totals = false });
    var r6 = StatementLayoutParser.Parse(p6);
    var c6 = StatementChecker.Evaluate(r6);
    Check(c6.State == StatementCheckState.Unverifiable && !c6.CanImport && c6.CanImportAfterManualCheck, "sans soldes : non vérifiable, import sous condition");
    // soldes saisis à la main → vérifiable
    var c6b = StatementChecker.Evaluate(r6.Rows, open, open + ops.Sum(x => x.Cents), null, null);
    Check(c6b.CanImport, "soldes saisis à la main → vérifié");
    var c6c = StatementChecker.Evaluate(r6.Rows, open, open + ops.Sum(x => x.Cents) + 1, null, null);
    Check(c6c.State == StatementCheckState.Mismatch, "solde saisi faux (1 centime) → écart détecté");

    // 7) montant + libellé multi-lignes + profil
    var p7 = StatementLayoutParser.Parse(StatementSample.Build(ops, open), StatementProfiles.CaisseEpargne());
    SameRows(p7, "profil Caisse d'Épargne");
    var json = StatementProfiles.Generic().ToJson();
    var back = StatementProfile.FromJson(json);
    Check(back is not null && back.DebitHeaders.SequenceEqual(StatementProfiles.Generic().DebitHeaders), "profil : aller-retour JSON");

    // 8) import : lignes au format du moteur
    var bank = StatementChecker.ToBankLines(r1.Rows);
    Eq(ops.Count, bank.Count, "lignes prêtes pour l'import");
    Eq(ops.Sum(x => x.Cents), bank.Sum(b => b.AmountCents), "somme des lignes importées");

    // 9) saisie manuelle de dates / montants
    Check(StatementChecker.TryParseUserDate("05/09", 2026, out var ud) && ud == new DateOnly(2026, 9, 5), "date saisie sans année");
    Check(StatementChecker.TryParseUserAmount("1 234,56", out var ua) && ua == 123456, "montant saisi 1 234,56");
    Check(!StatementChecker.TryParseUserDate("31/02", 2026, out _), "date impossible refusée");

    // 10) document sans texte
    var empty = StatementLayoutParser.Parse(new[] { new StatementPage(1, 1000, 1400, Array.Empty<StatementWord>()) });
    Check(empty.Rows.Count == 0 && empty.Messages.Count > 0, "page vide : message, aucune opération");
}

// ---------------------------------------------------------------------------------------------------------------------
// Mise en page d'un vrai relevé de la Caisse d'Épargne (PDF numérique) reconstituée en pièces anonymes :
// colonne « Montant » unique, « + » / « - » imprimés comme des mots à part (parfois décalés), en-tête sur 3 lignes,
// milliers coupés (« 1 » « 405,00 »), libellés sur 2 lignes, bruit de marge, deux comptes dans le même PDF.
// ---------------------------------------------------------------------------------------------------------------------
Console.WriteLine("Relevé multi-comptes (mise en page Caisse d'Épargne)");
{
    var words = new List<StatementWord>();
    StatementWord W(string t, double x, double y) => new(t, x, y, t.Length * 4.2, 8);
    void L(double y, params (double x, string t)[] ws) { foreach (var (x, t) in ws) words.Add(W(t, x, y)); }

    // résumé avant le tableau : ne doit pas fournir le solde de début
    L(100, (44, "COMPTE"), (85, "COURANT"), (242, "N°"), (255, "12345"), (282, "67890"), (310, "12345678901"), (367, "30"),
        (384, "Solde"), (409, "au"), (422, "31/10/2025"), (506, "+"), (513, "17"), (531, "691,26"));
    L(120, (43, "TOTAL"), (75, "DES"), (96, "OPERATIONS"), (158, "EN"), (173, "CREDIT"), (504, "+"), (512, "1"), (518, "081,90"));
    L(130, (43, "TOTAL"), (75, "DES"), (96, "OPERATIONS"), (158, "EN"), (173, "DEBIT"), (517, "1"), (523, "412,98"));
    L(132, (512, "-"));
    // compte courant
    L(280, (43, "COMPTE"), (84, "COURANT"), (131, "N°"), (144, "12345678901"), (201, "30"));
    L(300, (66, "DATE"), (121, "DATE"), (145, "DE"), (512, "MONTANT"));
    L(310, (184, "DETAIL"), (215, "DES"), (233, "OPERATIONS"));
    L(320, (49, "D'OPERATION"), (122, "VALEUR"), (521, "EN"), (535, "EUR"));
    L(340, (45, "SOLDE"), (75, "CREDITEUR"), (124, "AU"), (137, "30/09/2025"), (507, "+"), (514, "18"), (525, "022,34"));
    L(360, (56, "01/10/2025"), (120, "01/10/2025"), (184, "CB"), (197, "Action"), (222, "4175"), (251, "FACT"), (274, "290925"), (540, "7,98"));
    L(365, (532, "-"));
    L(375, (184, "N.0000000"), (225, "MLLE"), (248, "ALICE"), (287, "MARTIN"));
    L(400, (10, "4"), (56, "02/10/2025"), (120, "02/10/2025"), (184, "VIR"), (199, "SEPA"), (223, "JEAN"), (250, "DUPONT"), (527, "+"), (534, "60,00"));
    L(408, (10, "0"));
    L(420, (56, "09/10/2025"), (120, "09/10/2025"), (184, "RETRAIT"), (220, "ESPECES"), (278, "RET"), (521, "1"), (525, "405,00"));
    L(424, (514, "-"));
    L(440, (56, "09/10/2025"), (120, "09/10/2025"), (184, "VIR"), (199, "SEPA"), (223, "Stripe"), (512, "+"), (518, "1"), (525, "021,90"));
    L(450, (184, "HelloAsso"), (222, "W1S5M6"));
    L(480, (46, "SOLDE"), (75, "CREDITEUR"), (124, "AU"), (138, "31/10/2025"), (507, "+"), (514, "17"), (531, "691,26"));
    // livret du même PDF
    L(540, (43, "LIVRET"), (77, "A"), (86, "ASSOCIATIONS"), (157, "EURO"), (186, "N°"), (198, "98765432109"), (256, "23"));
    L(560, (66, "DATE"), (121, "DATE"), (145, "DE"), (512, "MONTANT"));
    L(570, (184, "DETAIL"), (215, "DES"), (233, "OPERATIONS"));
    L(580, (49, "D'OPERATION"), (122, "VALEUR"), (522, "EN"), (535, "EUR"));
    L(600, (45, "SOLDE"), (75, "CREDITEUR"), (124, "AU"), (137, "31/07/2025"), (518, "+"), (525, "114,31"));
    L(620, (56, "15/10/2025"), (184, "VIREMENT"), (227, "PAR"), (246, "INTERNET"), (512, "+"), (518, "1"), (525, "021,90"));
    L(630, (184, "De"), (196, "12345"), (221, "67890"), (245, "12345678901"), (296, "30"));
    L(660, (46, "SOLDE"), (75, "CREDITEUR"), (124, "AU"), (138, "31/10/2025"), (512, "+"), (518, "1"), (525, "136,21"));
    L(800, (28, "Caisse"), (48, "d'Epargne"), (77, "et"), (84, "de"), (92, "Prévoyance"));
    var page = new StatementPage(1, 595, 842, words);

    var secs = StatementLayoutParser.ParseSections(new[] { page });
    Eq(2, secs.Count, "deux comptes détectés dans le même PDF");
    var cc = secs[0];
    var lv = secs[^1];
    Eq("12345678901", cc.AccountNumber ?? "", "numéro du compte courant");
    Eq("98765432109", lv.AccountNumber ?? "", "numéro du livret");
    Eq(4, cc.Rows.Count, "compte courant : 4 opérations (bruit de marge ignoré)");
    Eq(1802234L, cc.OpeningCents ?? -1, "solde de début pris dans le tableau (pas dans le résumé)");
    Eq(1769126L, cc.ClosingCents ?? -1, "solde de fin du compte courant");
    Eq(-798L, cc.Rows[0].AmountCents ?? 0, "« - » décalé rattaché au montant : débit 7,98");
    Eq("CB Action 4175 FACT 290925 N.0000000 MLLE ALICE MARTIN", cc.Rows[0].Label, "libellé sur deux lignes recollé");
    Eq(6000L, cc.Rows[1].AmountCents ?? 0, "« + » séparé : crédit 60,00");
    Eq(-140500L, cc.Rows[2].AmountCents ?? 0, "milliers coupés avec signe : -1 405,00");
    Eq(102190L, cc.Rows[3].AmountCents ?? 0, "+1 021,90");
    Eq(new DateOnly(2025, 10, 9), cc.Rows[3].Date ?? default, "date d'opération (deux dates sur la ligne)");
    Eq(141298L, cc.TotalDebitsCents ?? -1, "total des débits du résumé rattaché au compte courant");
    Eq(108190L, cc.TotalCreditsCents ?? -1, "total des crédits du résumé rattaché au compte courant");
    var ck = StatementChecker.Evaluate(cc);
    Check(ck.State == StatementCheckState.Verified && ck.CanImport, "compte courant : vérifié (soldes + totaux)", string.Join(" | ", ck.Details));
    Eq(1, lv.Rows.Count, "livret : 1 opération");
    Eq(11431L, lv.OpeningCents ?? -1, "livret : solde de début");
    Eq(113621L, lv.ClosingCents ?? -1, "livret : solde de fin");
    Check(StatementChecker.Evaluate(lv).CanImport, "livret : vérifié");
    // un chiffre mal lu sur le compte courant n'affecte pas le livret
    var broken = new StatementPage(1, 595, 842, words.Select(w => w.Text == "60,00" ? w with { Text = "80,00" } : w).ToList());
    var bs = StatementLayoutParser.ParseSections(new[] { broken });
    Check(!StatementChecker.Evaluate(bs[0]).CanImport && StatementChecker.Evaluate(bs[^1]).CanImport,
        "chiffre faux : seul le compte concerné est bloqué");
}

// ---- Date de clôture saisie : les écritures postérieures passent à l'exercice suivant
Console.WriteLine("Clôture à une date choisie");
{
    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var banque = c.Accounts().First(a => a.Name == "Banque");
    c.SetOpening(y.Id, banque.Id, 100000);
    var adh = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Product && k.Name == "Adhésions");
    void Add(DateOnly d, string label, long cents) =>
        c.SaveEntry(new Entry(0, y.Id, d, label, false, EntryKind.Normal, banque.Id, Direction.In, null, cents, null, null),
            new[] { new Allocation(adh.Id, cents) });
    Add(Dt(2027, 6, 30), "Avant clôture", 10000);
    Add(Dt(2027, 7, 1), "Jour de clôture", 300);
    Add(Dt(2027, 7, 10), "Après clôture", 2000);
    Add(Dt(2027, 8, 20), "Bien après", 500);

    Throws(() => c.ValidateYear(y.Id, Dt(2026, 8, 31)), "clôture avant le début refusée", "précéder");
    c.ValidateYear(y.Id, Dt(2027, 7, 1));

    var vy = c.Year(y.Id)!;
    Check(vy.Validated, "exercice validé");
    Eq(Dt(2027, 7, 1), vy.End, "fin de l'exercice = date de clôture");
    var ny = c.NextYear(vy);
    Check(ny is not null, "exercice suivant créé");
    Eq(Dt(2027, 7, 2), ny!.Start, "l'exercice suivant commence le lendemain");
    Eq(2, c.Entries(y.Id).Count, "écritures jusqu'au jour de clôture inclus restent dans l'exercice clôturé");
    Eq(2, c.Entries(ny.Id).Count, "deux écritures déplacées dans l'exercice suivant");
    var nAdh = c.Categories(ny.Id).First(k => k.Kind == CategoryKind.Product && k.Name == "Adhésions");
    Check(c.Entries(ny.Id).All(r => r.Allocations.All(a => a.CategoryId == nAdh.Id)), "ventilation rattachée aux catégories de l'exercice suivant");
    Eq(10300L, c.ComputeBilan(y.Id).ProductsTotal, "produits de l'exercice clôturé (jour de clôture inclus)");
    Eq(2500L, c.ComputeBilan(ny.Id).ProductsTotal, "produits de l'exercice suivant");
    Eq(110300L, c.Opening(ny.Id, banque.Id), "solde d'ouverture de l'exercice suivant");
    Check(c.VerifyIntegrity(y.Id), "empreinte conforme après déplacement");
}

// ---- Script SQL exécuté depuis l'application
{
    var sqlDb = Path.Combine(Path.GetTempPath(), "tcf-sqlscript-" + Guid.NewGuid().ToString("N") + ".db");
    var sqlFactory = new DbContextFactory(sqlDb);
    sqlFactory.EnsureDatabaseUpToDate("test");
    SqlScriptRunner.Run(sqlFactory, "CREATE TABLE zz_test (a INTEGER, b TEXT);\nINSERT INTO zz_test VALUES (1, 'a;b {x}');\nINSERT INTO zz_test VALUES (2, 'l''apostrophe');");
    using (var c1 = sqlFactory.Create())
        Eq(2, c1.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM zz_test").AsEnumerable().First(), "script SQL : 2 lignes insérées");
    var failed = false;
    try { SqlScriptRunner.Run(sqlFactory, "INSERT INTO zz_test VALUES (3, 'x');\nINSERT INTO table_inconnue VALUES (1);"); }
    catch { failed = true; }
    using (var c2 = sqlFactory.Create())
        Check(failed && c2.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM zz_test").AsEnumerable().First() == 2,
            "script SQL en erreur : tout est annulé");
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { File.Delete(sqlDb); } catch { }
}

// ---- Clôture forcée par un administrateur
Console.WriteLine("Clôture forcée");
{
    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var banque = c.Accounts().First(a => a.Name == "Banque");
    c.SaveEntry(new Entry(0, y.Id, Dt(2027, 1, 10), "Non ventilée", false, EntryKind.Normal, banque.Id, Direction.In, null, 5000, null, null),
        Array.Empty<Allocation>(), strict: false);
    Throws(() => c.ValidateYear(y.Id), "écriture non ventilée : clôture normale refusée", "Validation impossible");
    Check(!c.Year(y.Id)!.Validated, "exercice toujours ouvert après refus");
    c.ValidateYear(y.Id, null, force: true);
    Check(c.Year(y.Id)!.Validated, "clôture forcée par l'administrateur : exercice validé");
    Check(c.VerifyIntegrity(y.Id), "clôture forcée : empreinte conforme");
}
{
    // Le forçage ne doit jamais échouer : exercice précédent non validé, date de clôture en conflit.
    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y1 = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var y2 = c.CreateYear(Dt(2027, 9, 1), Dt(2028, 8, 31));
    var banque = c.Accounts().First(a => a.Name == "Banque");
    c.SaveEntry(new Entry(0, y2.Id, Dt(2027, 10, 5), "Non ventilée", false, EntryKind.Normal, banque.Id, Direction.Out, null, 1234, null, null),
        Array.Empty<Allocation>(), strict: false);
    Throws(() => c.ValidateYear(y2.Id), "exercice précédent non validé : clôture normale refusée", "précédent");
    c.ValidateYear(y2.Id, Dt(2028, 8, 31), force: true);
    Check(c.Year(y2.Id)!.Validated, "clôture forcée malgré l'exercice précédent non validé");
    Check(!c.Year(y1.Id)!.Validated, "l'exercice précédent reste ouvert");
    // Conflit de dates avec un exercice suivant validé : ramenée à la fin prévue, sans échec.
    var y3 = c.CreateYear(Dt(2028, 9, 1), Dt(2029, 8, 31));
    c.ValidateYear(y3.Id, Dt(2029, 8, 31), force: true);
    Check(c.Year(y3.Id)!.Validated, "troisième exercice clôturé de force");
}
// ---- Justificatifs joints aux écritures
Console.WriteLine("Justificatifs");
{
    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var banque = c.Accounts().First(a => a.Name == "Banque");
    var ch = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Charge);
    var id = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 3), "Achat fournitures", false, EntryKind.Normal, banque.Id, Direction.Out, null, 4500, null, null),
        new[] { new Allocation(ch.Id, 4500) });
    var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0xFF, 0x10 };
    var aid = c.AddAttachment(id, @"C:\temp\facture 12.pdf", bytes);
    var list = c.Attachments(id);
    Eq(1, list.Count, "un justificatif joint");
    Eq("facture 12.pdf", list[0].FileName, "nom de fichier sans chemin");
    Eq("application/pdf", list[0].ContentType, "type déduit de l'extension");
    Check(c.AttachmentData(aid).SequenceEqual(bytes), "contenu relu à l'identique");
    Eq(1, c.AttachmentCounts(y.Id)[id], "compteur par écriture");
    // Compression sans perte : un texte répétitif est stocké compressé et relu à l'identique.
    var text = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Facture papeterie ligne 1234,56 EUR\n", 2000)));
    var tid = c.AddAttachment(id, "releve.txt", text);
    Check(c.AttachmentData(tid).SequenceEqual(text), "contenu compressé relu à l'identique");
    using (var cx = f.Create())
    {
        var stored = cx.Attachments.AsNoTracking().Where(a => a.Id == tid).Select(a => a.Data.Length).First();
        Check(stored < text.Length / 10, "texte répétitif : taille stockée très réduite");
        Eq(text.Length, (int)c.Attachments(id).First(a => a.Id == tid).SizeBytes, "taille d'origine conservée");
    }
    c.RemoveAttachment(tid);
    Throws(() => c.AddAttachment(id, "virus.exe", bytes), "exécutable refusé", "pas accepté");
    Throws(() => c.AddAttachment(id, "gros.pdf", new byte[Compta.MaxAttachmentBytes + 1]), "fichier trop gros refusé", "trop gros");
    var before1 = c.ComputeChecksum(y.Id);
    c.RemoveAttachment(aid);
    Eq(0, c.Attachments(id).Count, "justificatif retiré");
    Check(before1 != c.ComputeChecksum(y.Id), "l'empreinte tient compte des justificatifs");
    var aid2 = c.AddAttachment(id, "ticket.png", bytes);
    c.CancelEntry(id, "doublon");
    Eq(1, c.AttachmentCounts(y.Id)[id], "écriture annulée : justificatif conservé (pièce à garder 10 ans)");
    var id2 = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 4), "Autre achat", false, EntryKind.Normal, banque.Id, Direction.Out, null, 100, null, null),
        new[] { new Allocation(ch.Id, 100) });
    c.AddAttachment(id2, "ticket2.jpg", bytes);
    c.ValidateYear(y.Id);
    Throws(() => c.AddAttachment(id2, "autre.pdf", bytes), "exercice validé : ajout refusé", "validé");
    Throws(() => c.RemoveAttachment(c.Attachments(id2)[0].Id), "exercice validé : retrait refusé", "validé");
    Check(c.VerifyIntegrity(y.Id), "empreinte conforme avec justificatif");
}
// ---- Remboursements partiels liés à une dépense
Console.WriteLine("Remboursements partiels");
{
    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var banque = c.Accounts().First(a => a.Name == "Banque");
    var ch = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Charge);
    var dep = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 3), "Achat matériel", false, EntryKind.Normal, banque.Id, Direction.Out, null, 10000, null, null),
        new[] { new Allocation(ch.Id, 10000) });
    long Refund(long cents, long? of, string label = "Remboursement assurance") =>
        c.SaveEntry(new Entry(0, y.Id, Dt(2026, 11, 3), label, false, EntryKind.Normal, banque.Id, Direction.In, null, cents, null, null, of),
            new[] { new Allocation(ch.Id, -cents) });
    var r1 = Refund(3000, dep);
    Eq(3000L, c.RefundedAmount(dep), "premier remboursement partiel : 30 € sur 100 €");
    var r2 = Refund(2500, dep);
    Eq(5500L, c.RefundedAmount(dep), "plusieurs remboursements partiels cumulés");
    Eq(5500L, c.RefundedAmounts(y.Id)[dep], "total remboursé par dépense");
    Throws(() => Refund(5000, dep), "dépassement refusé", "dépasse");
    Refund(4500, dep);
    Eq(10000L, c.RefundedAmount(dep), "remboursement total atteint");
    Throws(() => Refund(1, dep), "plus rien à rembourser", "dépasse");
    Throws(() => c.CancelEntry(dep), "dépense remboursée non annulable", "remboursements liés");
    // Modifier un remboursement : son propre montant n'est pas compté deux fois.
    c.SaveEntry(new Entry(r1, y.Id, Dt(2026, 11, 3), "Remb corrigé", false, EntryKind.Normal, banque.Id, Direction.In, null, 3000, null, null, dep),
        new[] { new Allocation(ch.Id, -3000) });
    Eq(dep, c.EntryById(r1)!.Entry.RefundOfId, "lien conservé après modification");
    Check(c.RefundCandidates().All(x => x.Entry.Id != dep), "dépense soldée non proposée");
    var autre = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 12, 1), "Autre", false, EntryKind.Normal, banque.Id, Direction.Out, null, 500, null, null),
        new[] { new Allocation(ch.Id, 500) });
    Check(c.RefundCandidates().Any(x => x.Entry.Id == autre && x.Remaining == 500), "dépense non remboursée proposée");
    Throws(() => c.SaveEntry(new Entry(0, y.Id, Dt(2026, 12, 2), "Recette", false, EntryKind.Normal, banque.Id, Direction.Out, null, 100, null, null, dep),
        new[] { new Allocation(ch.Id, 100) }), "seule une entrée d'argent peut être un remboursement", "entrée d");
    Check(c.RefundOrigins(y.Id).ContainsKey(r2), "origine du remboursement retrouvée");
}
// ---- Copie de base avec exercice validé (sauvegarde d'une base serveur, export)
Console.WriteLine("Copie avec exercice validé");
{
    var srcPath = TempDb();
    var f = NewFactory(srcPath);
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var banque = c.Accounts().First(a => a.Name == "Banque");
    var ch = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Charge);
    var dep = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 3), "Achat", false, EntryKind.Normal, banque.Id, Direction.Out, null, 4000, null, null),
        new[] { new Allocation(ch.Id, 4000) });
    c.AddAttachment(dep, "ticket.txt", System.Text.Encoding.UTF8.GetBytes("ticket"));
    c.SaveEntry(new Entry(0, y.Id, Dt(2026, 11, 3), "Remb", false, EntryKind.Normal, banque.Id, Direction.In, null, 1000, null, null, dep),
        new[] { new Allocation(ch.Id, -1000) });
    c.ValidateYear(y.Id);
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    var dstPath = Path.Combine(Path.GetTempPath(), "tcf-copy-" + Guid.NewGuid().ToString("N") + ".db");
    var rep = DatabaseCopier.Copy(DbTarget.ForFile(srcPath), DbTarget.ForFile(dstPath), "copie-test", replaceDestination: false);
    Check(rep.IsComplete, "copie complète malgré l'exercice validé");
    Check(!File.Exists(dstPath + "-wal") && new FileInfo(dstPath).Length > 20000, "copie autonome : journal fusionné dans le fichier");
    var c2 = NewCompta(NewFactory(dstPath));
    Check(c2.Year(y.Id)!.Validated, "exercice toujours validé dans la copie");
    Eq(2, c2.Entries(y.Id).Count, "écritures copiées");
    Check(c2.VerifyIntegrity(y.Id), "empreinte conforme dans la copie");
    Eq(1, c2.Attachments(dep).Count, "justificatif copié");
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    var rep2 = DatabaseCopier.Copy(DbTarget.ForFile(srcPath), DbTarget.ForFile(dstPath), "copie-test", replaceDestination: true);
    Check(rep2.IsComplete, "copie par remplacement d'une base contenant un exercice validé");
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { File.Delete(dstPath); } catch { }
}
// ---- Réinitialisation de la ventilation
Console.WriteLine("Réinitialisation de la ventilation");
{
    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var banque = c.Accounts().First(a => a.Name == "Banque");
    var ch = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Charge);
    var id = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 3), "Mal classé", false, EntryKind.Normal, banque.Id, Direction.Out, null, 4000, null, null),
        new[] { new Allocation(ch.Id, 4000) });
    c.ClearAllocations(id);
    var row = c.EntryById(id)!;
    Eq(0, row.Allocations.Count, "ventilation réinitialisée");
    Check(Compta.NeedsAllocation(row), "l'écriture redevient à ventiler");
    Eq(4000L, row.Entry.AmountCents, "montant conservé");
    c.ClearAllocations(id);   // sans effet
    c.ValidateYear(y.Id, null, force: true);
    Throws(() => c.ClearAllocations(id), "exercice validé : réinitialisation refusée", "validé");
}

// ---- Détection du compte d'un relevé (identifiants saisis sur les comptes)
Console.WriteLine("Détection du compte du relevé");
{
    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var courant = c.Accounts().First(a => a.Name == "Banque");
    var livret = c.Accounts().First(a => a.Name == "Livret A");
    c.UpdateAccount(courant.Id, courant.Name, courant.Kind, true, "12345 67890 11122233344 ; compte courant");
    c.UpdateAccount(livret.Id, livret.Name, livret.Kind, true, "99887766554");
    Eq("12345 67890 11122233344 ; compte courant", c.Account(courant.Id)!.StatementMatch, "identifiants enregistrés");
    Throws(() => c.UpdateAccount(livret.Id, livret.Name, livret.Kind, true, "ab"), "identifiant trop court refusé", "trop court");

    const string fichierCourant = "Code de la banque : 00000;Code de l'agence : 00111\nNuméro de compte : 11122233344;Nom du compte : COMPTE COURANT;Devise : EUR\n\n" +
        "Date comptable;Libelle;Debit;Credit\n06/10/2026;VIREMENT PAR INTERNET De 00000 00111 99887766554 23;;+649,45\n";
    var m = c.MatchStatementAccounts(fichierCourant);
    Check(m.Count == 1 && m[0].Account.Id == courant.Id, "compte courant reconnu (numéro dans l''en-tête)");
    // Le numéro de l'autre compte apparaît dans une opération : ne doit pas compter.
    const string fichierLivret = "Numéro de compte : 99 887 766 554;Nom du compte : LIVRET A\nDate comptable;Libelle;Debit;Credit\n" +
        "06/10/2026;VIREMENT Vers 11122233344;-649,45;\n";
    var m2 = c.MatchStatementAccounts(fichierLivret);
    Check(m2.Count == 1 && m2[0].Account.Id == livret.Id, "livret reconnu malgré le numéro de l''autre compte dans une opération");
    Eq(0, c.MatchStatementAccounts("Numéro de compte : 55555555555\n01/10/2026;X;-1,00;").Count, "compte inconnu : aucune détection");
    c.UpdateAccount(livret.Id, livret.Name, livret.Kind, true, "11122233344");
    Eq(2, c.MatchStatementAccounts(fichierCourant).Count, "identifiants en double : ambigu");
}
// ---- Vidage de la base (utilisateurs conservés)
Console.WriteLine("Vidage de la base");
{
    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var banque = c.Accounts().First(a => a.Name == "Banque");
    var ch = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Charge);
    var dep = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 3), "Achat", false, EntryKind.Normal, banque.Id, Direction.Out, null, 4000, null, null),
        new[] { new Allocation(ch.Id, 4000) });
    c.AddAttachment(dep, "t.txt", new byte[] { 1, 2, 3 });
    c.SaveEntry(new Entry(0, y.Id, Dt(2026, 11, 3), "Remb", false, EntryKind.Normal, banque.Id, Direction.In, null, 1000, null, null, dep),
        new[] { new Allocation(ch.Id, -1000) });
    c.ValidateYear(y.Id, null, force: true);
    var users = new AuthService(f).HasUsers();
    // Exercice validé : vidage refusé, sauf phrase de confirmation ET archive exportée au préalable.
    Throws(() => c.ResetDatabaseData(), "vidage refusé : exercice validé", "validé");
    Throws(() => c.ResetDatabaseData("autre phrase", null), "vidage refusé : mauvaise phrase", "saisissez exactement");
    Throws(() => c.ResetDatabaseData(Compta.ResetValidatedPhrase, null), "vidage refusé : pas d'archive", "Archive");
    var archive = Path.Combine(Path.GetTempPath(), "tcf-archive-" + Guid.NewGuid().ToString("N"));
    Eq(1, c.ArchiveValidatedYears(archive), "archive des exercices validés exportée");
    Check(Compta.ArchiveFilesMissing(archive, c.Years().Where(v => v.Validated)).Count == 0, "archive complète (livre CSV + FEC)");
    Eq(1, c.Years().Count, "refus : l'exercice est toujours là");
    c.ResetDatabaseData("supprimer les exercices valides", archive);
    try { Directory.Delete(archive, true); } catch { /* temporaire */ }
    Eq(0, c.Years().Count, "plus aucun exercice");
    Eq(0, c.Accounts(includeInactive: true).Count, "plus aucun compte bancaire");
    Eq(users, new AuthService(f).HasUsers(), "comptes utilisateurs conservés");
    Check(c.AuditLog(20).Any(a => a.Action == "Base de données vidée"), "opération consignée au journal");
    var y2 = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    Check(c.Accounts().Count > 0 && y2.Id > 0, "la base vidée reste utilisable");
}

// ---- Contrôle du solde du relevé et solde d'ouverture au premier import
Console.WriteLine("Soldes du relevé");
{
    const string csv = "Code de la banque : 11111;Code de l'agence : 22222;Date de début de téléchargement : 01/09/2026;Date de fin de téléchargement : 11/10/2026\n" +
        "Numéro de compte : 12345678901;Nom du compte : COMPTE COURANT;Devise : EUR\n\n" +
        "Solde en fin de période;;;1180,00\n" +
        "Date comptable;Libelle simplifie;Reference;Informations;Type;Debit;Credit;Date operation\n" +
        "07/10/2026;CB MAGASIN;;;Paiement CB;-20,00;;07/10/2026\n" +
        "09/10/2026;VIR SEPA ASSOCIATION;;;Virement;;+100,00;09/10/2026\n" +
        "15/09/2026;CB AUTRE;;;Paiement CB;-100,00;;15/09/2026\n" +
        "Solde en début de période;;;1200,00\n";
    var table = BankStatementParser.ReadCsv(csv);
    var map = BankStatementParser.DetectMapping(table)!;
    var warn = new List<string>();
    var lines = BankStatementParser.ParseCsv(table, map, warn);
    Eq(3, lines.Count, "3 opérations lues");
    var bal = BankStatementParser.ExtractBalances(csv, lines);
    Eq(120000L, bal.Opening, "solde d'ouverture lu");
    Eq(118000L, bal.Closing, "solde de clôture lu");
    Eq(Dt(2026, 9, 1), bal.Start, "début de période");
    Eq(Dt(2026, 10, 11), bal.End, "fin de période");

    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var banque = c.Accounts().First(a => a.Name == "Banque");
    Check(c.IsFirstImport(banque.Id), "aucune écriture : premier import");
    var pre = c.PrecheckStatement(banque.Id, lines, bal);
    Check(!pre.Inconsistent && pre.WillSetOpening, "relevé cohérent, ouverture à fixer");
    var plan = c.PlanBankImport(banque.Id, lines);
    c.ApplyBankImport(plan, true);
    var rep = c.FinishStatementImport(banque.Id, lines, bal, wasFirstImport: true);
    Eq(120000L, c.Opening(y.Id, banque.Id), "solde d'ouverture fixé d'après le relevé");
    Check(rep.Consistent, "solde du compte = solde du relevé après import");
    Eq(118000L, c.AccountBalanceAt(banque.Id, Dt(2026, 10, 11)), "solde du compte à la date de fin");
    Check(!c.IsFirstImport(banque.Id), "plus un premier import");

    // Relevé incohérent (une ligne manque) et solde d'ouverture différent du logiciel.
    var lines2 = lines.Take(2).ToList();
    var pre2 = c.PrecheckStatement(banque.Id, lines2, bal);
    Check(pre2.Inconsistent && pre2.Note.Contains("incohérent"), "relevé incohérent signalé");
    var bal2 = bal with { Opening = 130000L, Closing = 128000L };
    var pre3 = c.PrecheckStatement(banque.Id, lines, bal2);
    Check(pre3.Inconsistent && pre3.Note.Contains("Solde du compte dans le logiciel"), "solde du logiciel différent de l'ouverture du relevé");

    const string ofx = "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST><DTSTART>20260901<DTEND>20261011\n" +
        "<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260907<TRNAMT>-10,00<FITID>1<NAME>X</STMTTRN></BANKTRANLIST>" +
        "<LEDGERBAL><BALAMT>990,00<DTASOF>20261011</LEDGERBAL></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";
    var bo = BankStatementParser.ExtractBalances(ofx, new List<BankLine>());
    Eq(99000L, bo.Closing, "OFX : solde de clôture");
    Eq(Dt(2026, 9, 1), bo.Start, "OFX : début de période");
    Check(bo.Opening is null, "OFX : pas de solde d'ouverture inventé");
}
// ---- Format Crédit Agricole : en-tête libre, libellés sur plusieurs lignes entre guillemets, Débit/Crédit positifs
Console.WriteLine("Format Crédit Agricole");
{
    const string csv = "\nTéléchargement du 12/10/2026;\n\n\nM.OU MME DUPONT JEAN\nCompte de Dépôt carte n° 12345678901;\nSolde au 12/10/2026 2 273,63 €\n\n" +
        "Liste des opérations du compte entre le 01/01/2026 et le 11/10/2026;\n\nDate;Libellé;Débit euros;Crédit euros;\n" +
        "09/10/2026;\"Prélèvement\nAMAZON PAYMENTS - Amazon - 405-0000000-0000000 REF1\n\n)FEFFUMNBNT1QH1SDUJXHLDIC-WVAK\nDE94ZZZ00000561653\nREF1\";25,49;;\n" +
        "08/10/2026;\"Paiement par carte\nX0000 BOULANGERIE   06/10\n\n\n\";4,50;;\n" +
        "05/10/2026;\"Virement en votre faveur\nASSO ADHESION 123456789012345678901234567890\";;100,00;\n" +
        "02/01/2026;\"Chèque emis\n0000001/0000000/000000000\n\n\n\";150,00;;\n";
    var table = BankStatementParser.ReadCsv(csv);
    var map = BankStatementParser.DetectMapping(table)!;
    var warn = new List<string>();
    var lines = BankStatementParser.ParseCsv(table, map, warn);
    Eq(4, lines.Count, "CA : 4 opérations lues malgré les libellés multi-lignes");
    Eq(0, warn.Count, "CA : aucun avertissement");
    Eq(-2549L, lines[0].AmountCents, "CA : débit positif lu comme sortie");
    Eq(10000L, lines[2].AmountCents, "CA : crédit lu comme entrée");
    Check(lines[0].Label.StartsWith("Prélèvement AMAZON"), "CA : libellé multi-lignes recollé");
    var bal = BankStatementParser.ExtractBalances(csv, lines);
    Eq(227363L, bal.Closing, "CA : « Solde au … » lu comme solde");
    Eq(Dt(2026, 1, 1), bal.Start, "CA : début de période « entre le … et le … »");
    Check(!Money.TryParse("123456789012345678901234567890", out _), "numéro très long : pas un montant (pas de dépassement)");
}
// ---- Immobilisations : amortissement linéaire, bilan équilibré, exercices validés protégés
Console.WriteLine("Immobilisations");
{
    var machine = new FixedAsset(1, "Machine", Dt(2026, 3, 15), 100000, 4, AssetProrata.Monthly, false, null, null, null, null);
    Eq(20833L, AssetCalc.Cumulative(machine, Dt(2026, 12, 31)), "amortissement : 10 mois sur 48 (prorata mensuel)");
    Eq(100000L, AssetCalc.Cumulative(machine, Dt(2030, 12, 31)), "amortissement : plafonné au coût");
    Eq(0L, AssetCalc.Cumulative(machine, Dt(2026, 2, 28)), "amortissement : rien avant l'acquisition");
    var daily = machine with { Prorata = AssetProrata.Daily };
    Eq(100000L, AssetCalc.Cumulative(daily, Dt(2030, 3, 14)), "amortissement : au jour, fin exacte de la durée");
    var sold = machine with { DisposedOn = Dt(2027, 6, 30) };
    Eq(AssetCalc.Cumulative(machine, Dt(2027, 6, 30)), AssetCalc.Cumulative(sold, Dt(2028, 12, 31)), "amortissement : arrêté à la sortie");

    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 1, 1), Dt(2026, 12, 31));
    var banque = c.Accounts().First(a => a.Name == "Banque");
    var cats = c.Categories(y.Id);
    var adh = cats.First(k => k.Kind == CategoryKind.Product && k.Name == "Adhésions");
    var assur = cats.First(k => k.Kind == CategoryKind.Charge && k.Name == "Assurance");
    c.SaveEntry(new Entry(0, y.Id, Dt(2026, 2, 1), "Cotisations", false, EntryKind.Normal, banque.Id, Direction.In, null, 500000, null, null),
        new[] { new Allocation(adh.Id, 500000) });
    c.SaveEntry(new Entry(0, y.Id, Dt(2026, 3, 15), "Machine à popcorn", false, EntryKind.Normal, banque.Id, Direction.Out, null, 100000, null, null),
        new[] { new Allocation(assur.Id, 100000) });
    var b0 = c.ComputeBilan(y.Id);
    Eq(400000L, b0.Result, "sans immobilisation : l'achat est une charge de 1 000 €");

    var id = c.AddFixedAsset("Machine à popcorn", Dt(2026, 3, 15), 100000, 4, AssetProrata.Monthly, existing: false, categoryName: "Assurance");
    var b1 = c.ComputeBilan(y.Id);
    Eq(100000L, b1.AssetsReclass, "bilan : achat porté à l'actif");
    Eq(20833L, b1.AssetsDepreciation, "bilan : dotation de l'exercice");
    Eq(500000L - 20833L, b1.Result, "bilan : résultat après amortissement");
    Eq(79167L, b1.AssetsNetClosing, "bilan : valeur nette à la clôture");
    Eq(0L, b1.BalanceDifference, "bilan : actif = passif avec immobilisation");

    var fec = c.ExportFec(y.Id);
    Check(fec.Contains("681120") && fec.Contains("281800") && fec.Contains("218000"), "FEC : dotation, amortissement et immobilisation");

    // Impression du bilan : exercice non clôturé = filigrane et mention provisoire ; clôturé = ni l'un ni l'autre.
    string PdfText(byte[] pdf) => System.Text.Encoding.Latin1.GetString(pdf);
    var provisional = PdfText(c.ExportBilanPdf(y.Id, detailed: false, "Association test", "tresorier"));
    Check(provisional.StartsWith("%PDF-1.4") && provisional.Contains("%%EOF"), "bilan PDF : structure valide");
    Check(provisional.Contains("BILAN PROVISOIRE") && provisional.Contains("NON CL"), "bilan PDF : filigrane et mention « non clôturé »");
    Check(provisional.Contains("synth"), "bilan PDF : édition synthétique");
    var provisionalFull = c.ExportBilanPdf(y.Id, detailed: true, "Association test");
    // Diagnostic visuel : TCF_PDF_OUT=<dossier> écrit les éditions d'exemple (non utilisé en test normal).
    if (Environment.GetEnvironmentVariable("TCF_PDF_OUT") is { Length: > 0 } pdfDir)
    {
        Directory.CreateDirectory(pdfDir);
        File.WriteAllBytes(Path.Combine(pdfDir, "bilan-provisoire-detail.pdf"), provisionalFull);
        File.WriteAllBytes(Path.Combine(pdfDir, "bilan-provisoire-synthese.pdf"), c.ExportBilanPdf(y.Id, detailed: false, "Association test", "tresorier"));
    }
    Check(PdfText(provisionalFull).Contains("Machine") || PdfText(provisionalFull).Contains("Assurance"), "bilan PDF détaillé : lignes de compte");
    Check(provisionalFull.Length > c.ExportBilanPdf(y.Id, detailed: false).Length, "bilan PDF détaillé : plus complet que la synthèse");

    c.ValidateYear(y.Id, Dt(2026, 12, 31));
    var closedPdf = PdfText(c.ExportBilanPdf(y.Id, detailed: false, "Association test"));
    Check(!closedPdf.Contains("BILAN PROVISOIRE") && closedPdf.Contains("EXERCICE CL"), "bilan PDF : exercice clôturé sans filigrane");
    Throws(() => c.UpdateFixedAsset(id, "Machine", Dt(2026, 3, 15), 90000, 4, AssetProrata.Monthly, false, null, null),
        "immobilisation d'un exercice validé : valeur figée", "exercice validé");
    c.UpdateFixedAsset(id, "Machine à popcorn (salle)", Dt(2026, 3, 15), 100000, 4, AssetProrata.Monthly, false, "Assurance", "libellé seul");
    Throws(() => c.DeleteFixedAsset(id), "immobilisation d'un exercice validé : suppression refusée", "exercice validé");
    Throws(() => c.AddFixedAsset("Rétroactif", Dt(2026, 5, 1), 50000, 3, AssetProrata.Monthly, false), "acquisition dans un exercice validé refusée", "exercice validé");
    Throws(() => c.DisposeFixedAsset(id, Dt(2026, 10, 1)), "sortie dans un exercice validé refusée", "postérieure");

    var y2 = c.NextYear(c.Year(y.Id)!) ?? c.CreateYear(Dt(2027, 1, 1), Dt(2027, 12, 31), cloneStructure: true);
    var b2 = c.ComputeBilan(y2.Id);
    Eq(79167L, b2.AssetsNetOpening, "exercice suivant : valeur nette reportée");
    Eq(25000L, b2.AssetsDepreciation, "exercice suivant : dotation annuelle");
    Eq(0L, b2.BalanceDifference, "exercice suivant : actif = passif");

    c.DisposeFixedAsset(id, Dt(2027, 6, 30));
    var b3 = c.ComputeBilan(y2.Id);
    Eq(0L, b3.AssetsNetClosing, "sortie : plus d'immobilisation à la clôture");
    Eq(100000L - AssetCalc.Cumulative(sold, Dt(2027, 6, 30)), b3.AssetsDisposals, "sortie : valeur nette passée en charge");
    Eq(0L, b3.BalanceDifference, "sortie : actif = passif");

    Eq(Compta.DefaultAssetThresholdCents, c.AssetThresholdCents(), "seuil : 500 € par défaut");
    c.SaveAssetSettings(true, 30000);
    Check(c.AssetsEnabled() && c.AssetThresholdCents() == 30000, "réglages : activation et seuil enregistrés");
}

// ---- Statut Développeur (s'ajoute au rôle) et configurations multiples
Console.WriteLine("Statut Développeur et configurations");
{
    var f = NewFactory(TempDb());
    var admin = NewCompta(f);
    var dev = admin.CreateUser("dev", "Dev", Role.Treasurer, "Motdepasse2026", isDeveloper: true);
    Check(dev.IsDeveloper && dev.Role == Role.Treasurer, "développeur : statut ajouté au rôle trésorier");
    Eq("Trésorier + Développeur", dev.RoleText, "développeur : libellé du rôle");
    var plain = admin.CreateUser("simple", "Simple", Role.Admin, "Motdepasse2026");
    Check(!plain.IsDeveloper, "compte ordinaire : pas développeur");

    var devSession = new Session(dev.Id, dev.UserName, dev.DisplayName, dev.Role, dev.IsDeveloper);
    Check(devSession.Can(Permission.ManageConfigurations), "développeur : peut gérer les configurations");
    Check(devSession.Can(Permission.WriteLedger) && !devSession.Can(Permission.ManageUsers), "développeur trésorier : droits du rôle inchangés");
    var adminSession = new Session(plain.Id, plain.UserName, plain.DisplayName, plain.Role, plain.IsDeveloper);
    Check(!adminSession.Can(Permission.ManageConfigurations), "administrateur non développeur : pas de configurations");
    Check(!Session.System.Can(Permission.ManageConfigurations), "session système : pas de configurations");

    // Seul un administrateur attribue ou retire le statut.
    var treasurerCompta = NewCompta(f, new Session(dev.Id, dev.UserName, dev.DisplayName, Role.Treasurer, true));
    Throws(() => treasurerCompta.UpdateUser(plain.Id, "Simple", Role.Admin, true, isDeveloper: true), "attribution refusée à un non-administrateur", "");
    admin.UpdateUser(plain.Id, "Simple", Role.Admin, true, isDeveloper: true);
    Check(admin.Users().First(u => u.Id == plain.Id).IsDeveloper, "administrateur : statut attribué");
    admin.UpdateUser(plain.Id, "Simple", Role.Admin, true, isDeveloper: false);
    Check(!admin.Users().First(u => u.Id == plain.Id).IsDeveloper, "administrateur : statut retiré");
    admin.UpdateUser(plain.Id, "Simple 2", Role.Admin, true);
    Check(!admin.Users().First(u => u.Id == plain.Id).IsDeveloper, "modification sans statut : inchangé");

    // Configurations : un fichier par configuration, dossier temporaire.
    var dir = Path.Combine(Path.GetTempPath(), "tcf-config-" + Guid.NewGuid().ToString("N"));
    try
    {
        var settings = new AppSettings { UseDatabaseServer = false, DatabasePath = Path.Combine(dir, "prod.db") };
        ConfigProfiles.Save(ConfigProfiles.FromSettings(settings, "Production"), dir);
        var server = new DbConnectionInfo { Provider = DatabaseProvider.PostgreSql, Host = "srv", Port = 5432, Database = "essais", UserName = "u" };
        ConfigProfiles.Save(new ConfigProfile { Name = "Essais", UseServer = true, Server = server }, dir);
        var list = ConfigProfiles.List(dir);
        Eq(2, list.Count, "configurations : deux enregistrées");
        Eq("Essais", list[0].Name, "configurations : triées par nom");
        Check(ConfigProfiles.IsCurrent(list.First(p => p.Name == "Production"), settings), "configuration active reconnue");
        Check(!ConfigProfiles.IsCurrent(list.First(p => p.Name == "Essais"), settings), "autre configuration non active");
        ConfigProfiles.Apply(list.First(p => p.Name == "Essais"), settings);
        Check(settings.UseDatabaseServer && settings.DatabaseServer.Database == "essais", "bascule : réglages du poste mis à jour");
        Check(ConfigProfiles.IsCurrent(list.First(p => p.Name == "Essais"), settings), "bascule : configuration devenue active");
        Throws(() => ConfigProfiles.Save(new ConfigProfile { Name = " ", UseServer = false, FilePath = "x.db" }, dir), "nom vide refusé", "obligatoire");
        ConfigProfiles.Delete("Essais", dir);
        Eq(1, ConfigProfiles.List(dir).Count, "configuration supprimée");
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}

// ---- Déconnexion automatique : réglage de la base, administrateur seulement
Console.WriteLine("Déconnexion automatique");
{
    var f = NewFactory(TempDb());
    var admin = NewCompta(f);
    var reader = NewCompta(f, new Session(9, "lecteur", "Lecteur", Role.Reader));
    Eq(0, admin.GetIdleTimeoutMinutes(), "inactivité : désactivée par défaut");
    admin.SetIdleTimeoutMinutes(15);
    Eq(15, admin.GetIdleTimeoutMinutes(), "inactivité : délai enregistré");
    Eq(15, reader.GetIdleTimeoutMinutes(), "inactivité : même délai pour tous les utilisateurs");
    Throws(() => reader.SetIdleTimeoutMinutes(5), "inactivité : non administrateur refusé", "");
    Throws(() => admin.SetIdleTimeoutMinutes(5000), "inactivité : délai trop long refusé", "entre 0");
    Eq(15, admin.GetIdleTimeoutMinutes(), "inactivité : valeur refusée sans effet");
    admin.SetIdleTimeoutMinutes(0);
    Eq(0, reader.GetIdleTimeoutMinutes(), "inactivité : désactivation");
    Check(admin.AuditLog(10).Any(l => l.Action == "Déconnexion automatique"), "inactivité : changement tracé au journal");
    admin.RecordForcedTakeover("tresorier");
    Check(admin.AuditLog(10).Any(l => l.Action == "Reprise d'écriture forcée" && l.Detail!.Contains("tresorier")), "reprise forcée : tracée au journal");
    Throws(() => reader.RecordForcedTakeover(null), "reprise forcée : non administrateur refusé", "");
}

// ---- Format livret : ligne de préambule (compte, numéro masqué, solde) puis opérations sans en-tête, montant signé
Console.WriteLine("Format livret (préambule sans en-tête)");
{
    var csv = "Livret A;Livret A;****6322;09/10/2026;;30\r\n" +
              "16/10/2026;DEPOT INITIAL;DEPOT INITIAL;DEPOT INITIAL DU COMPTE 30004005680000382885163;30;\r\n";
    var table = BankStatementParser.ReadCsv(csv);
    var map = BankStatementParser.DetectMapping(table);
    Check(map is not null, "livret : colonnes reconnues sans en-tête");
    var warns = new List<string>();
    var lines = map is null ? new List<BankLine>() : BankStatementParser.ParseCsv(table, map, warns);
    Eq(1, lines.Count, "livret : une seule opération (préambule ignoré)");
    Eq(0, warns.Count, "livret : aucun avertissement");
    Check(lines.Count == 1 && lines[0].Date == Dt(2026, 10, 16), "livret : date de l'opération (colonne 1, pas celle du préambule)");
    Check(lines.Count == 1 && lines[0].AmountCents == 3000, "livret : dépôt de 30,00 € en entrée");
    Check(lines.Count == 1 && lines[0].Label.StartsWith("DEPOT INITIAL"), "livret : libellé");

    // Même format, plusieurs opérations dont un retrait négatif.
    var csv2 = csv + "20/10/2026;RETRAIT;RETRAIT;RETRAIT DAB;-12,50;\r\n";
    var t2 = BankStatementParser.ReadCsv(csv2);
    var m2 = BankStatementParser.DetectMapping(t2)!;
    var l2 = BankStatementParser.ParseCsv(t2, m2, new List<string>());
    Eq(2, l2.Count, "livret : deux opérations");
    Eq(-1250L, l2.Last().AmountCents, "livret : retrait négatif lu comme sortie");
}

// ---- Moyen de paiement déduit du libellé bancaire
Console.WriteLine("Moyen de paiement");
{
    void Pay(string raw, string mode, string clean)
    {
        var (m, c) = PaymentLabel.Split(raw);
        Check(m == mode && c == clean, $"Moyen « {raw} » → {m} / {c}");
    }
    Pay("CARTE 02/10 PARC DE MOULINS", "CB", "PARC DE MOULINS");
    Pay("CB LECLERC 03/09", "CB", "LECLERC 03/09");
    Pay("PRLV PayPal Europe S.a.r.l. et C", "Prélèvement", "PayPal Europe S.a.r.l. et C");
    Pay("PRELEVEMENT SEPA EDF", "Prélèvement", "EDF");
    Pay("VIR INST Sumup Limited", "Virement", "Sumup Limited");
    Pay("VIR SECURSOLUTION", "Virement", "SECURSOLUTION");
    Pay("VIR", "Virement", "VIR");
    Pay("CHEQUE 1234567", "Chèque", "1234567");
    Pay("RETRAIT DAB 12/03 MOULINS", "Retrait", "MOULINS");
    Pay("FRAIS TENUE DE COMPTE", "Frais", "FRAIS TENUE DE COMPTE");
    Pay("Cotisation membres", "", "Cotisation membres");
    Pay("BANQUE PRIME OUVERTURE", "", "BANQUE PRIME OUVERTURE");
    Pay("CARTONS EMBALLAGE", "", "CARTONS EMBALLAGE");
    Pay("", "", "");
}

// ---- Format Fortuneo : « Date opération » + « Date valeur », débits négatifs sur 4 champs, crédits « ; 9,36; »
Console.WriteLine("Format Fortuneo");
{
    const string csv = "Date opération;Date valeur;libellé;Débit;Crédit;\n" +
        "08/10/2026;08/10/2026;VIR INST PAYPAL;; 9,36;\n" +
        "03/10/2026;03/10/2026;CARTE 02/10 PARC DE MOULINS;-0,6;\n" +
        "18/06/2026;18/06/2026;PRLV PayPal Europe S.a.r.l. et C;-100;\n" +
        "18/06/2026;18/06/2026;PRLV PayPal Europe S.a.r.l. et C;-100;\n" +
        "07/02/2026;06/02/2026;BANQUE PRIME OUVERTURE;; 160;\n";
    var table = BankStatementParser.ReadCsv(csv);
    var map = BankStatementParser.DetectMapping(table)!;
    Eq(2, map.LabelColumn, "Fortuneo : libellé = 3e colonne (pas « Date opération »)");
    Eq(0, map.DateColumn, "Fortuneo : date = date d'opération");
    var warn = new List<string>();
    var lines = BankStatementParser.ParseCsv(table, map, warn);
    Eq(5, lines.Count, "Fortuneo : 5 opérations (deux prélèvements identiques conservés)");
    Eq(0, warn.Count, "Fortuneo : aucun avertissement");
    Eq(936L, lines[0].AmountCents, "Fortuneo : crédit avec espace");
    Eq(-60L, lines[1].AmountCents, "Fortuneo : débit négatif sur ligne courte");
    Eq("VIR INST PAYPAL", lines[0].Label, "Fortuneo : libellé");
}// ---- Versions (éditions) du logiciel : noms, fichiers de release, cohérence avec editions.json
Console.WriteLine("Versions du logiciel");
{
    Eq("asso", Editions.Current.Id, "tests : version historique par défaut");
    var asso = new AppEdition("asso");
    Eq("Tout Compte Fait Asso", asso.AppName, "nom affiché de la version Asso");
    Eq("version Asso", asso.VersionMention, "mention ajoutée au numéro de version");
    Eq("ToutCompteFaitAsso.exe", asso.ExeFileName, "exécutable de la version Asso");
    Eq("ToutCompteFait", asso.DataName, "version historique : noms de données d'origine (réglages, registre, licence)");
    Eq("tout_compte_fait", asso.DefaultServerDatabaseName, "version historique : base serveur d'origine");
    Eq("asso", asso.LicenseEdition, "version envoyée au serveur de licences");
    Eq("tout-compte-fait", asso.ProductSlug, "produit de licence commun à toutes les versions");
    Check(asso.HasMemberRefunds, "Asso : remboursements aux membres");
    Check(!new AppEdition("autoentreprise").HasMemberRefunds, "Autoentreprise : pas de remboursements aux membres");
    var pro = new AppEdition("pro");
    Eq("ToutCompteFaitPro", pro.DataName, "autre version : noms de données propres");
    Eq("tout_compte_fait_pro", pro.DefaultServerDatabaseName, "autre version : base serveur propre");
    Check(asso.DataName != pro.DataName && asso.ExeFileName != pro.ExeFileName, "deux versions ne partagent ni exécutable ni données");
    Check(AppEdition.IsValidId("asso") && AppEdition.IsValidId("pro2") && !AppEdition.IsValidId("Pro") && !AppEdition.IsValidId("") && !AppEdition.IsValidId("2pro")
          && !AppEdition.IsValidId("a b"), "identifiants de version valides");

    // Fichiers d'une release : chaque version prend le sien ; la version historique reprend les anciens noms (≤ 1.2.x).
    var files = new[]
    {
        ("ToutCompteFaitAsso-v1.3.0-win-x64.zip", "u/asso.zip"), ("ToutCompteFaitAsso-v1.3.0-win-x64.msi", "u/asso.msi"),
        ("ToutCompteFaitPro-v1.3.0-win-x64.zip", "u/pro.zip"), ("ToutCompteFaitPro-v1.3.0-win-x64.msi", "u/pro.msi")
    };
    Eq("u/asso.msi", UpdateService.PickAsset(files, ".msi", asso)!.Value.Url, "Asso : son installateur");
    Eq("u/pro.zip", UpdateService.PickAsset(files, ".zip", pro)!.Value.Url, "Pro : son archive");
    var legacyFiles = new[] { ("ToutCompteFait-v1.2.2-win-x64.zip", "u/old.zip"), ("ToutCompteFait-v1.2.2-win-x64.msi", "u/old.msi") };
    Eq("u/old.msi", UpdateService.PickAsset(legacyFiles, ".msi", asso)!.Value.Url, "Asso : repli sur les fichiers d'avant les versions");
    Check(UpdateService.PickAsset(legacyFiles, ".msi", pro) is null, "Pro : jamais les fichiers de la version historique");
    Check(UpdateService.PickAsset(files.Where(f => f.Item1.Contains("Asso")), ".msi", pro) is null, "release sans fichier pour cette version");

    // editions.json (utilisé par les scripts de release) suit les mêmes règles de nommage que le programme.
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "editions.json"))) dir = dir.Parent;
    Check(dir is not null, "editions.json trouvé à la racine du dépôt");
    if (dir is not null)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "editions.json")));
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.RootElement.GetProperty("editions").EnumerateArray())
        {
            var id = e.GetProperty("id").GetString()!;
            var ed = new AppEdition(id);
            Check(AppEdition.IsValidId(id), $"editions.json : identifiant « {id} » valide");
            Eq(ed.ExeBaseName, e.GetProperty("exe").GetString(), $"editions.json[{id}] : exécutable");
            Eq(ed.AppName, e.GetProperty("name").GetString(), $"editions.json[{id}] : nom affiché");
            Eq(ed.DataName, e.GetProperty("dataName").GetString(), $"editions.json[{id}] : nom des données");
            Check(codes.Add(e.GetProperty("upgradeCode").GetString()!) && codes.Add(e.GetProperty("dataFolderGuid").GetString()!)
                  && codes.Add(e.GetProperty("backupFolderGuid").GetString()!), $"editions.json[{id}] : GUID uniques (jamais partagés entre versions)");
        }
        Check(codes.Contains("ADEA732C-8143-4CC9-AFCB-E08D07DE3AF1"), "UpgradeCode historique conservé : les installations existantes passent sur Asso");
    }
}// ---- Autoentreprise : profil, types de déclaration URSSAF, rapport, recherche d'entreprise
Console.WriteLine("Autoentreprise");
{
    // Codes APE → type proposé
    Eq(UrssafKind.Sale, ApeMapper.Suggest("47.91B")!.Kind, "APE commerce de détail : BIC vente");
    Eq(UrssafKind.Service, ApeMapper.Suggest("43.21A")!.Kind, "APE travaux d'installation : BIC prestation");
    Eq(UrssafKind.LiberalSsi, ApeMapper.Suggest("6202A")!.Kind, "APE informatique : BNC (format sans point accepté)");
    Eq(UrssafKind.LiberalCipav, ApeMapper.Suggest("71.11Z")!.Kind, "APE architecture : BNC CIPAV");
    Eq(UrssafKind.Service, ApeMapper.Suggest("45.20A")!.Kind, "réparation automobile : prestation (pas vente)");
    Eq("62.02A", ApeMapper.Normalize("6202a"), "normalisation d'un code APE");
    Check(ApeMapper.Suggest("00.00Z") is null && ApeMapper.Normalize("abc") is null, "code APE inconnu ou invalide");

    // Noms de catégories
    Eq("BIC vente", UrssafKinds.CategoryName(UrssafKind.Sale, null), "nom sans personnalisation");
    Eq("BIC prestation – Dépannage", UrssafKinds.CategoryName(UrssafKind.Service, " Dépannage "), "nom personnalisé après le type");
    Eq(UrssafKind.Service, UrssafKinds.FromCategoryName("BIC prestation – Dépannage"), "type retrouvé d'après le nom");
    Eq(UrssafKind.LiberalCipav, UrssafKinds.FromCategoryName("BNC CIPAV – Plans"), "BNC CIPAV avant BNC");
    Check(UrssafKinds.FromCategoryName("Cotisations") is null, "nom sans type");

    // Périodes et échéances
    var q3 = UrssafPeriod.ForDate(new DateOnly(2026, 8, 15), DeclarationMode.Quarterly);
    Eq("2026-T3", q3.Key, "trimestre du 15/08");
    Eq(new DateOnly(2026, 10, 31), q3.DueDate, "échéance du 3e trimestre");
    Eq(new DateOnly(2027, 1, 31), UrssafPeriod.Quarterly(2026, 4).DueDate, "échéance du 4e trimestre : janvier suivant");
    var m = UrssafPeriod.ForDate(new DateOnly(2026, 2, 10), DeclarationMode.Monthly);
    Eq(new DateOnly(2026, 2, 28), m.End, "fin de février");
    Eq(new DateOnly(2026, 3, 31), m.DueDate, "échéance mensuelle : fin du mois suivant");
    Eq(4, UrssafPeriod.OfYear(2026, DeclarationMode.Quarterly).Count, "4 trimestres");
    Eq(12, UrssafPeriod.OfYear(2026, DeclarationMode.Monthly).Count, "12 mois");

    // SIREN / SIRET
    string WithLuhn(string eight)
    {
        for (var d = 0; d < 10; d++) if (BusinessLookup.TryParseNumber(eight + d, out _)) return eight + d;
        throw new InvalidOperationException();
    }
    var siren = WithLuhn("12345678");
    Check(BusinessLookup.TryParseNumber(siren, out var digits) && digits == siren, "SIREN valide (clé de Luhn)");
    Check(!BusinessLookup.TryParseNumber(siren[..8] + ((siren[8] - '0' + 1) % 10), out _), "SIREN à la clé fausse refusé");
    Check(!BusinessLookup.TryParseNumber("12 34", out _), "numéro trop court refusé");

    // Lecture de la réponse de l'API (jeu fictif, mêmes champs que le service réel)
    var json = $$$"""
        {"results":[{"siren":"{{{siren}}}","nom_complet":"DUPONT JEAN (DUPONT SERVICES)","nature_juridique":"1000",
          "activite_principale":"43.21A","date_creation":"2024-02-01",
          "siege":{"siret":"{{{siren}}}00012","activite_principale":"43.21A","adresse":"1 RUE DES ESSAIS 03000 MOULINS",
                   "code_postal":"03000","libelle_commune":"MOULINS"}}],"total_results":1}
        """;
    var info = BusinessLookup.Parse(json, siren);
    Eq("DUPONT JEAN (DUPONT SERVICES)", info.Name, "nom de l'entreprise");
    Eq("43.21A", info.ApeMain, "code APE principal");
    Eq("1 RUE DES ESSAIS 03000 MOULINS", info.Address, "adresse");
    Eq(new DateOnly(2024, 2, 1), info.CreationDate, "date de création");
    Eq(siren + "00012", info.Siret, "SIRET du siège");
    Eq(null, info.Rna, "pas de numéro RNA pour une entreprise");
    // Association : le registre fournit le numéro RNA dans « complements ».
    var assoJson = $$$"""
        {"results":[{"siren":"{{{siren}}}","nom_complet":"CLUB DES ESSAIS","nature_juridique":"9220",
          "date_creation":"1999-06-15","complements":{"est_association":true,"identifiant_association":"W031000123"},
          "siege":{"siret":"{{{siren}}}00012","adresse":"2 PLACE DU CLUB 03000 MOULINS","code_postal":"03000","libelle_commune":"MOULINS"}}]}
        """;
    var asso = BusinessLookup.Parse(assoJson, siren);
    Eq("W031000123", asso.Rna, "association : numéro RNA");
    Eq("9220", asso.LegalNature, "association : forme juridique");
    Eq("CLUB DES ESSAIS", asso.Name, "association : nom");
    Throws(() => BusinessLookup.Parse("{\"results\":[]}", siren), "aucune entreprise : message clair", "Aucune entreprise");
    Throws(() => BusinessLookup.Parse("pas du json", siren), "réponse illisible : message clair", "inattendue");

    // Base : profil, types, rapport
    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 1, 1), Dt(2026, 12, 31));
    Check(c.GetBusinessProfile().IsEmpty, "profil vide au départ");
    c.SaveBusinessProfile(info with { Mode = DeclarationMode.Monthly });
    var back = c.GetBusinessProfile();
    Eq("43.21A", back.ApeMain, "profil relu : APE");
    Eq(DeclarationMode.Monthly, back.Mode, "profil relu : mode de déclaration");
    Eq(new DateOnly(2024, 2, 1), back.CreationDate, "profil relu : date de création");

    var banque = c.Accounts().First(a => a.Name == "Banque");
    var vente = c.AddActivityCategory(y.Id, UrssafKind.Sale, "Boutique");
    var presta = c.AddActivityCategory(y.Id, UrssafKind.Service, null);
    var libre = c.AddCategory(y.Id, CategoryKind.Product, "Apport libre test");
    var charge = c.AddCategory(y.Id, CategoryKind.Charge, "Charge libre test");
    Eq("BIC vente – Boutique", vente.Name, "catégorie d'activité nommée d'après le type");
    var kinds = c.CategoryUrssafKinds();
    Eq(UrssafKind.Sale, kinds["BIC vente – Boutique"], "type enregistré");
    Check(!kinds.ContainsKey("Apport libre test"), "produit libre : sans type");

    void Income(DateOnly d, long cents, params (long cat, long amount)[] split) =>
        c.SaveEntry(new Entry(0, y.Id, d, "client", false, EntryKind.Normal, banque.Id, Direction.In, null, cents, null, null),
            split.Select(s => new Allocation(s.cat, s.amount)).ToList());
    Income(Dt(2026, 7, 5), 100_000, (vente.Id, 100_000));
    Income(Dt(2026, 8, 20), 50_000, (presta.Id, 30_000), (vente.Id, 20_000));
    Income(Dt(2026, 9, 30), 10_000, (libre.Id, 10_000));
    Income(Dt(2026, 10, 2), 70_000, (presta.Id, 70_000));      // hors du 3e trimestre
    c.SaveEntry(new Entry(0, y.Id, Dt(2026, 8, 25), "achat", false, EntryKind.Normal, banque.Id, Direction.Out, null, 5_000, null, null),
        new[] { new Allocation(charge.Id, 5_000) });          // une charge n'est jamais du chiffre d'affaires
    c.SaveEntry(new Entry(0, y.Id, Dt(2026, 9, 10), "reçu non classé", false, EntryKind.Normal, banque.Id, Direction.In, null, 4_000, null, null),
        Array.Empty<Allocation>(), strict: false);

    var rep = c.ComputeUrssafReport(q3);
    Eq(150_000L, rep.TotalTurnoverCents, "CA du 3e trimestre : ventes + prestations");
    Eq(120_000L, rep.Lines.First(l => l.Kind == UrssafKind.Sale).TurnoverCents, "CA BIC vente");
    Eq(30_000L, rep.Lines.First(l => l.Kind == UrssafKind.Service).TurnoverCents, "CA BIC prestation");
    Eq(10_000L, rep.UnclassifiedCents, "produit sans type : signalé, hors total");
    Eq(1, rep.PendingEntries, "entrée non ventilée signalée");
    Eq(120_000L * 1230 / 10000 + 30_000L * 2120 / 10000, rep.TotalContributionCents, "cotisations estimées aux taux par défaut");
    Check(rep.DeclaredAt is null, "période non déclarée");

    // ACRE : réduction de 50 % jusqu'à la date indiquée
    c.SaveUrssafParams(c.GetUrssafParams() with { AcreUntil = Dt(2026, 12, 31) });
    var acre = c.ComputeUrssafReport(q3);
    Eq(120_000L * 615 / 10000 + 30_000L * 1060 / 10000, acre.TotalContributionCents, "cotisations avec ACRE (taux divisés par deux)");
    Eq(1060, c.GetUrssafParams().RateFor(UrssafKind.Service, Dt(2026, 9, 30)) , "taux ACRE d'une prestation");

    // Plafonds
    var ceil = c.ComputeUrssafCeilings(2026);
    Eq(220_000L, ceil.TotalCents, "CA cumulé de l'année");
    Eq(100_000L, ceil.ServicesCents, "dont prestations");

    // Déclaration marquée comme faite
    c.MarkUrssafDeclared(q3, "déclarée sur le site");
    Check(c.ComputeUrssafReport(q3).DeclaredAt is not null, "période marquée comme déclarée");
    Eq(150_000L, c.Declarations().Single().TurnoverCents, "montant conservé avec la déclaration");
    c.UnmarkUrssafDeclared(q3.Key);
    Check(c.Declarations().Count == 0, "marque retirée");

    // Renommer la catégorie emporte son type
    c.RenameCategory(vente.Id, "BIC vente – Marché");
    Eq(UrssafKind.Sale, c.CategoryUrssafKinds()["BIC vente – Marché"], "le type suit le renommage");

    // Un lecteur ne modifie ni le profil ni les types
    var reader = NewCompta(f, new Session(9, "lecteur", "Lecteur", Role.Reader));
    Throws(() => reader.SaveBusinessProfile(info), "lecteur : profil refusé", "ne permet pas");
}// ---- Virements entre comptes : une seule écriture
Console.WriteLine("Virements entre comptes");
{
    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var banque = c.Accounts().First(a => a.Name == "Banque");
    var livret = c.Accounts().First(a => a.Name == "Livret A");
    c.SetOpening(y.Id, banque.Id, 100000);
    c.SetOpening(y.Id, livret.Id, 500000);

    // 1. Import des deux relevés : la deuxième moitié du virement est reconnue.
    var lineLivret = new BankLine(Dt(2026, 10, 6), "VIREMENT PAR INTERNET", -64945);
    var lineBanque = new BankLine(Dt(2026, 10, 6), "VIREMENT PAR INTERNET", 64945);
    c.ApplyBankImport(c.PlanBankImport(livret.Id, new[] { lineLivret }), true);
    var plan = c.PlanBankImport(banque.Id, new[] { lineBanque });
    Eq(BankLineStatus.ToConfirm, plan.Lines[0].Status, "contrepartie sur l'autre compte : à confirmer");
    Check(plan.Lines[0].Candidates!.Any(x => x.IsTransfer && x.CounterpartAccountId == livret.Id), "candidat virement proposé");
    Throws(() => c.ApplyBankImport(plan, true), "décision obligatoire", "incertaine");
    var res = c.ApplyBankImport(plan, true, new Dictionary<int, BankDecision>
        { [0] = new BankDecision(BankDecisionKind.Transfer, plan.Lines[0].Candidates!.First(x => x.IsTransfer).EntryId) });
    Eq(1, res.TransfersLinked, "1 virement reconnu");
    var all = c.Entries(y.Id);
    Eq(1, all.Count, "une seule écriture pour le virement (pas deux)");
    Eq(EntryKind.Transfer, all[0].Entry.Kind, "devenue un virement entre comptes");
    Eq(livret.Id, all[0].Entry.AccountId, "du livret");
    Eq(banque.Id, all[0].Entry.ToAccountId, "vers la banque");
    Eq(100000L + 64945, c.AccountBalanceAt(banque.Id, Dt(2026, 10, 6)), "solde banque");
    Eq(500000L - 64945, c.AccountBalanceAt(livret.Id, Dt(2026, 10, 6)), "solde livret");
    // Le relevé suivant ne recrée rien : les deux lignes sont retrouvées sur le virement.
    Eq(BankLineStatus.Unchanged, c.PlanBankImport(banque.Id, new[] { lineBanque }).Lines[0].Status, "relevé réimporté : déjà à jour");

    // 2. Doublons déjà présents : détection puis fusion.
    var ch = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Charge);
    var o = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 11, 10), "Virement vers livret", false, EntryKind.Normal, banque.Id, Direction.Out, null, 20000, null, null),
        new[] { new Allocation(ch.Id, 20000) });
    var i = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 11, 11), "Virement de la banque", true, EntryKind.Normal, livret.Id, Direction.In, null, 20000, null, null),
        Array.Empty<Allocation>(), strict: false);
    var pairs = c.FindTransferPairs(y.Id);
    Eq(1, pairs.Count, "une paire en double détectée");
    Eq(o, pairs[0].Out.Id, "sortie de la paire");
    Eq(i, pairs[0].In.Id, "entrée de la paire");
    c.MergeIntoTransfer(o, i);
    Eq(2, c.Entries(y.Id).Count, "fusion : plus qu''une écriture pour ce virement");
    var merged = c.EntryById(o)!;
    Check(merged.Entry.Kind == EntryKind.Transfer && merged.Entry.ToAccountId == livret.Id && merged.Allocations.Count == 0 && merged.Entry.Pointed,
        "virement fusionné sans ventilation, pointé");
    Eq(0, c.FindTransferPairs(y.Id).Count, "plus de paire à fusionner");
    Eq(1, c.Entries(y.Id).Count(r => r.Entry.Kind == EntryKind.Transfer && r.Entry.AmountCents == 20000), "un seul virement de 200 €");
}
// ---- Association automatique produit / charge de même nom
Console.WriteLine("Association automatique des opérations");
{
    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31), cloneStructure: false);
    var p = c.AddCategory(y.Id, CategoryKind.Product, "Loto Octobre");
    Check(c.Categories(y.Id).First(k => k.Id == p.Id).OperationId is null, "produit seul : aucune opération");
    var ch = c.AddCategory(y.Id, CategoryKind.Charge, "loto octobre");
    var cats = c.Categories(y.Id);
    var op = c.Operations(y.Id).FirstOrDefault(o => o.Name == "loto octobre" || o.Name == "Loto Octobre");
    Check(op is not null, "opération créée avec le même nom");
    Check(cats.First(k => k.Id == p.Id).OperationId == op!.Id && cats.First(k => k.Id == ch.Id).OperationId == op.Id,
        "produit et charge de même nom rattachés à la même opération");
    Eq(1, c.Operations(y.Id).Count, "une seule opération");

    // Renommage : devient identique à un autre nom -> rattaché.
    var p2 = c.AddCategory(y.Id, CategoryKind.Product, "Kermesse");
    var c2 = c.AddCategory(y.Id, CategoryKind.Charge, "Kermesse 2026");
    c.RenameCategory(c2.Id, "Kermesse");
    var cats2 = c.Categories(y.Id);
    Check(cats2.First(k => k.Id == p2.Id).OperationId is { } o2 && cats2.First(k => k.Id == c2.Id).OperationId == o2,
        "renommage : rattachement automatique");

    // Choix existant conservé : charge déjà dans une autre opération.
    var autre = c.AddOperation(y.Id, "Autre");
    var p3 = c.AddCategory(y.Id, CategoryKind.Product, "Garage");
    var c3 = c.AddCategory(y.Id, CategoryKind.Charge, "Garagiste");
    c.SetCategoryOperation(c3.Id, autre.Id);
    c.RenameCategory(c3.Id, "Garage");
    var cats3 = c.Categories(y.Id);
    Eq(autre.Id, cats3.First(k => k.Id == c3.Id).OperationId, "rattachement existant jamais déplacé");
    Eq(autre.Id, cats3.First(k => k.Id == p3.Id).OperationId, "le produit rejoint l'opération déjà choisie pour la charge");

    // Catégories existantes : association en lot.
    var p4 = c.AddCategory(y.Id, CategoryKind.Product, "Buvette");
    var c4 = c.AddCategory(y.Id, CategoryKind.Charge, "Buvette");
    c.SetCategoryOperation(p4.Id, null); c.SetCategoryOperation(c4.Id, null);
    Eq(2, c.AutoLinkOperations(y.Id), "association en lot : 2 rattachements");
    Eq(0, c.AutoLinkOperations(y.Id), "idempotent");
}
// ---- Fichier de configuration du serveur
{
    var cfgPath = Path.Combine(Path.GetTempPath(), "tcf-config-" + Guid.NewGuid().ToString("N") + ServerConfigFile.Extension);
    var src = new DbConnectionInfo { Provider = DatabaseProvider.MariaDb, Host = "srv.exemple", Port = 3307, Database = "tcf", UserName = "u", UseSsl = true };
    src.Password = "secret-é";
    ServerConfigFile.Write(cfgPath, src, includePassword: false);
    var noPwd = ServerConfigFile.Read(cfgPath);
    Check(noPwd.Provider == DatabaseProvider.MariaDb && noPwd.Host == "srv.exemple" && noPwd.Port == 3307 && noPwd.Database == "tcf"
          && noPwd.UserName == "u" && noPwd.UseSsl && noPwd.Password == "", "config serveur : aller-retour sans mot de passe");
    Check(!File.ReadAllText(cfgPath).Contains("secret"), "config serveur : mot de passe absent du fichier");
    ServerConfigFile.Write(cfgPath, src, includePassword: true);
    Eq("secret-é", ServerConfigFile.Read(cfgPath).Password, "config serveur : mot de passe inclus sur demande");
    File.WriteAllText(cfgPath, "{\"foo\":1}");
    var bad = false;
    try { ServerConfigFile.Read(cfgPath); } catch (ComptaException) { bad = true; }
    Check(bad, "config serveur : fichier étranger refusé");
    try { File.Delete(cfgPath); } catch { }
}

// ------------------------------------------------------------------ conformité : numérotation, annulation, journal chaîné
Console.WriteLine("Conformité : numérotation continue et contre-passation");
{
    var path = TempDb();
    {
        var f = NewFactory(path);
        var c = NewCompta(f);
        var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
        var banque = c.Accounts().First(a => a.Name == "Banque");
        var livret = c.Accounts().First(a => a.Name == "Livret A");
        var cats = c.Categories(y.Id);
        var adh = cats.First(k => k.Kind == CategoryKind.Product && k.Name == "Adhésions");
        var assur = cats.First(k => k.Kind == CategoryKind.Charge && k.Name == "Assurance");

        var e1 = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 1), "Cotisation", false, EntryKind.Normal, banque.Id, Direction.In, null, 5000, null, null),
            new[] { new Allocation(adh.Id, 5000) });
        var e2 = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 2), "Assurance", false, EntryKind.Normal, banque.Id, Direction.Out, null, 3000, null, null),
            new[] { new Allocation(assur.Id, 3000) });
        var e3 = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 3), "Virement", false, EntryKind.Transfer, banque.Id, Direction.Out, livret.Id, 1000, null, null),
            Array.Empty<Allocation>());
        Eq(1L, c.EntryById(e1)!.Entry.Seq, "numéro 1 attribué à la première écriture");
        Eq(3L, c.EntryById(e3)!.Entry.Seq, "numéros séquentiels");

        var before = c.ComputeBilan(y.Id);
        var closingBank = before.Accounts.First(a => a.Account.Id == banque.Id).Closing;
        var closingLivret = before.Accounts.First(a => a.Account.Id == livret.Id).Closing;

        // Annulation : jamais de suppression, effet net nul, numérotation continue.
        var rev = c.CancelEntry(e2, "montant erroné");
        Eq(4L, c.EntryById(rev)!.Entry.Seq, "l'annulation reçoit le numéro suivant (sans trou)");
        Check(c.EntryById(rev)!.Entry.IsReversal && c.EntryById(e2)!.Entry.Cancelled, "original annulé / écriture d'annulation");
        Eq(2, c.Entries(y.Id).Count, "écriture annulée et son annulation masquées par défaut");
        Eq(4, c.Entries(y.Id, includeCancelled: true).Count, "journal complet : annulation visible");
        var after = c.ComputeBilan(y.Id);
        Eq(closingBank + 3000, after.Accounts.First(a => a.Account.Id == banque.Id).Closing, "solde : la dépense annulée n'existe plus");
        Eq(0L, after.ChargesTotal, "charges : annulation sans effet résiduel");
        Eq(0L, after.BalanceDifference, "bilan équilibré après annulation");
        Throws(() => c.CancelEntry(e2), "double annulation refusée", "déjà annulée");
        Throws(() => c.CancelEntry(rev), "annulation d'une annulation refusée", "annulation");
        Throws(() => c.SetPointed(e2, true), "écriture annulée : pointage refusé", "annulée");
        Throws(() => c.SaveEntry(new Entry(e2, y.Id, Dt(2026, 10, 2), "Autre", false, EntryKind.Normal, banque.Id, Direction.Out, null, 3000, null, null),
            new[] { new Allocation(assur.Id, 3000) }), "écriture annulée : modification refusée", "annulée");
        Throws(() => c.SaveEntry(new Entry(rev, y.Id, Dt(2026, 10, 2), "Autre", false, EntryKind.Normal, banque.Id, Direction.In, null, 3000, null, null),
            new[] { new Allocation(assur.Id, -3000) }), "écriture d'annulation : modification refusée", "annulation");

        // Virement annulé : comptes rétablis.
        c.CancelEntry(e3);
        var b3 = c.ComputeBilan(y.Id);
        Eq(closingBank + 3000 + 1000, b3.Accounts.First(a => a.Account.Id == banque.Id).Closing, "virement annulé : compte débité rétabli");
        Eq(closingLivret - 1000, b3.Accounts.First(a => a.Account.Id == livret.Id).Closing, "virement annulé : compte crédité rétabli");

        // Aucune suppression possible, même hors service.
        using (var ctx = f.Create())
        {
            ctx.Entries.Remove(ctx.Entries.First());
            Throws(() => ctx.SaveChanges(), "suppression refusée (garde EF)", "ne se supprime");
        }
        Throws(() => Sql(f, "DELETE FROM entries WHERE id = {0}", e1), "suppression refusée (trigger SQL)", "supprime");

        // Journal : création, modification avant/après, annulation, avec numéros.
        c.SaveEntry(new Entry(e1, y.Id, Dt(2026, 10, 1), "Cotisation 2026", false, EntryKind.Normal, banque.Id, Direction.In, null, 5000, null, null),
            new[] { new Allocation(adh.Id, 5000) });
        var log = c.AuditLog(100);
        Check(log.Any(l => l.Action == "Écriture créée" && l.Detail!.Contains("n° 1")), "création journalisée avec son numéro");
        Check(log.Any(l => l.Action == "Écriture modifiée" && l.Detail!.Contains("Avant :") && l.Detail.Contains("Cotisation 2026")), "modification journalisée avant/après");
        Check(log.Any(l => l.Action == "Écriture annulée" && l.Detail!.Contains("contre-passation n° 4") && l.Detail.Contains("montant erroné")), "annulation journalisée avec motif");

        // Chaîne du journal intacte.
        var chain = c.VerifyAuditChain();
        Check(chain.Ok && chain.Rows > 5, "journal d'audit : chaîne intacte", chain.Problem);
        Check(chain.Unsealed == 0, "journal d'audit : aucune ligne hors chaîne");
    }
    Cleanup(path);
}

Console.WriteLine("Conformité : journal d'audit inaltérable");
{
    var path = TempDb();
    {
        var f = NewFactory(path);
        var c = NewCompta(f);
        c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
        c.AddAccount("Caisse", AccountKind.Cash);
        Check(c.VerifyAuditChain().Ok, "chaîne intacte au départ");
        Throws(() => Sql(f, "UPDATE audit_log SET action = 'x' WHERE id = 1"), "modification d'une ligne refusée (trigger SQL)", "modifié");
        Throws(() => Sql(f, "DELETE FROM audit_log WHERE id = 1"), "suppression d'une ligne refusée (trigger SQL)", "purgé");
        using (var ctx = f.Create())
        {
            var a = ctx.Audit.First();
            a.Detail = "falsifié";
            Throws(() => ctx.SaveChanges(), "modification refusée (garde EF)", "journal");
        }
        using (var ctx = f.Create())
        {
            ctx.Audit.Remove(ctx.Audit.First());
            Throws(() => ctx.SaveChanges(), "suppression refusée (garde EF)", "journal");
        }

        // Altération hors logiciel (déclencheur retiré) : la chaîne la détecte.
        Sql(f, "DROP TRIGGER audit_no_update");
        Sql(f, "UPDATE audit_log SET detail = 'falsifié' WHERE id = 2");
        var bad = c.VerifyAuditChain();
        Check(!bad.Ok && bad.FirstBadId == 2, "altération détectée à la bonne ligne", bad.Problem);
        Sql(f, "UPDATE audit_log SET detail = (SELECT detail FROM audit_log WHERE id = 2) WHERE id = 2");   // sans effet
    }
    Cleanup(path);
}

Console.WriteLine("Conformité : suppression de lignes du journal avant validation détectée");
{
    var path = TempDb();
    {
        var f = NewFactory(path);
        var c = NewCompta(f);
        var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
        var banque = c.Accounts().First(a => a.Name == "Banque");
        var adh = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Product);
        c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 1), "Cotisation", false, EntryKind.Normal, banque.Id, Direction.In, null, 5000, null, null),
            new[] { new Allocation(adh.Id, 5000) });
        c.ValidateYear(y.Id);
        Check(c.VerifyAuditChain().Ok, "chaîne intacte après validation");
        // Retrait (hors logiciel) des dernières lignes antérieures à la validation.
        Sql(f, "DROP TRIGGER audit_no_delete");
        Sql(f, "DELETE FROM audit_log WHERE action = 'Écriture créée'");
        var r = c.VerifyAuditChain();
        Check(!r.Ok, "lignes retirées : détecté (rupture de chaîne)", r.Problem);
    }
    Cleanup(path);
}

Console.WriteLine("Conformité : empreintes versionnées et évolution de la base");
{
    var path = TempDb();
    {
        var f = NewFactory(path);
        var c = NewCompta(f);
        var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
        var banque = c.Accounts().First(a => a.Name == "Banque");
        var adh = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Product);
        var id = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 1), "Cotisation", false, EntryKind.Normal, banque.Id, Direction.In, null, 5000, null, "note interne"),
            new[] { new Allocation(adh.Id, 5000) });
        c.AddAttachment(id, "recu.txt", System.Text.Encoding.UTF8.GetBytes("reçu"));
        c.ValidateYear(y.Id);
        Eq(IntegritySeal.Current, c.IntegritySealVersion(y.Id), "empreinte à la formule courante");
        Check(c.ComputeChecksum(y.Id).StartsWith("v2:"), "empreinte préfixée par sa version");

        // L'empreinte v2 couvre la note, la référence d'import et le CONTENU des justificatifs.
        Sql(f, "DROP TRIGGER lock_entries_upd");
        Sql(f, "UPDATE entries SET note = 'modifiée' WHERE id = {0}", id);
        Check(!c.VerifyIntegrity(y.Id), "v2 : modification de la note détectée");
        Sql(f, "UPDATE entries SET note = 'note interne' WHERE id = {0}", id);
        Check(c.VerifyIntegrity(y.Id), "v2 : note rétablie, empreinte conforme");
        Sql(f, "DROP TRIGGER lock_attachments_upd");
        Sql(f, "UPDATE attachments SET data = X'00112233' WHERE entry_id = {0}", id);
        Check(!c.VerifyIntegrity(y.Id), "v2 : contenu d'un justificatif remplacé : détecté");
        f.EnsureDatabaseUpToDate("test");   // recrée les déclencheurs
    }
    {
        // Exercice scellé avec la formule v1 (ancienne version du logiciel) : toujours vérifiable, mis à niveau sans perte.
        var f = NewFactory(TempDb());
        var c = NewCompta(f);
        var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
        var banque = c.Accounts().First(a => a.Name == "Banque");
        var adh = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Product);
        var id = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 1), "Cotisation", false, EntryKind.Normal, banque.Id, Direction.In, null, 5000, null, null),
            new[] { new Allocation(adh.Id, 5000) });
        c.ValidateYear(y.Id);
        var v1 = (string)typeof(Compta).GetMethod("ComputeChecksumV1", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(c, new object[] { y.Id })!;
        Sql(f, "UPDATE fiscal_years SET checksum = {0} WHERE id = {1}", v1, y.Id);    // seule l'empreinte est réinscriptible
        Eq(1, c.IntegritySealVersion(y.Id), "empreinte d'ancienne version reconnue");
        Check(c.VerifyIntegrity(y.Id), "ancienne formule : exercice toujours conforme");
        Throws(() => Sql(f, "UPDATE fiscal_years SET label = 'autre' WHERE id = {0}", y.Id), "exercice validé : libellé toujours verrouillé", "validé");
        Eq(1, c.UpgradeIntegritySeals(), "mise à niveau de l'empreinte");
        Eq(IntegritySeal.Current, c.IntegritySealVersion(y.Id), "empreinte à la formule courante");
        Check(c.VerifyIntegrity(y.Id), "conforme après mise à niveau");
        Check(c.AuditLog(20).Any(l => l.Action == "Empreinte d'exercice mise à niveau"), "mise à niveau tracée");
        Eq(0, c.UpgradeIntegritySeals(), "idempotent");
        Check(c.VerifyAuditChain().Ok, "journal toujours intact");

        // Un exercice altéré n'est jamais « blanchi » par la mise à niveau.
        var f2 = NewFactory(TempDb());
        var c2 = NewCompta(f2);
        var y2 = c2.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
        var b2 = c2.Accounts().First(a => a.Name == "Banque");
        var a2 = c2.Categories(y2.Id).First(k => k.Kind == CategoryKind.Product);
        var i2 = c2.SaveEntry(new Entry(0, y2.Id, Dt(2026, 10, 1), "Cotisation", false, EntryKind.Normal, b2.Id, Direction.In, null, 5000, null, null),
            new[] { new Allocation(a2.Id, 5000) });
        c2.ValidateYear(y2.Id);
        var v1b = (string)typeof(Compta).GetMethod("ComputeChecksumV1", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(c2, new object[] { y2.Id })!;
        Sql(f2, "UPDATE fiscal_years SET checksum = {0} WHERE id = {1}", v1b, y2.Id);
        Sql(f2, "DROP TRIGGER lock_entries_upd");
        Sql(f2, "UPDATE entries SET amount_cents = 1 WHERE id = {0}", i2);
        Eq(0, c2.UpgradeIntegritySeals(), "exercice altéré : aucune mise à niveau");
        Check(!c2.VerifyIntegrity(y2.Id), "exercice altéré : alerte conservée");
    }
    Cleanup(path);
}

Console.WriteLine("Conformité : migration d'une base contenant un exercice validé");
{
    var path = TempDb();
    {
        var f = NewFactory(path);
        var c = NewCompta(f);
        var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
        var banque = c.Accounts().First(a => a.Name == "Banque");
        var adh = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Product);
        c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 1), "Cotisation A", false, EntryKind.Normal, banque.Id, Direction.In, null, 5000, null, null),
            new[] { new Allocation(adh.Id, 5000) });
        c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 2), "Cotisation B", false, EntryKind.Normal, banque.Id, Direction.In, null, 2500, null, null),
            new[] { new Allocation(adh.Id, 2500) });
        c.ValidateYear(y.Id);
        // Exercice scellé « à l'ancienne » : formule v1 + écritures sans numéro + journal sans chaînage, version 5.
        var v1 = (string)typeof(Compta).GetMethod("ComputeChecksumV1", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(c, new object[] { y.Id })!;
        Sql(f, "UPDATE fiscal_years SET checksum = {0} WHERE id = {1}", v1, y.Id);
        Sql(f, "DROP TRIGGER lock_entries_upd");
        Sql(f, "DROP INDEX ux_entries_seq");
        Sql(f, "UPDATE entries SET seq = 0");
        Sql(f, "DROP TRIGGER audit_no_update");
        Sql(f, "UPDATE audit_log SET hash = NULL, prev_hash = NULL");
        Sql(f, "PRAGMA user_version = 5");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Mise à jour du logiciel : les migrations doivent pouvoir compléter l'exercice validé (verrous levés le temps
        // de la migration), puis tout est reverrouillé et l'empreinte reste exacte.
        var f2 = new DbContextFactory(path);
        f2.EnsureDatabaseUpToDate("test");
        var c2 = NewCompta(f2);
        Eq(DbContextFactory.LatestVersion, f2.GetDatabaseVersion(), "base migrée à la dernière version");
        var rows = c2.Entries(y.Id);
        Eq(1L, rows[0].Entry.Seq, "écritures de l'exercice validé numérotées (1)");
        Eq(2L, rows[1].Entry.Seq, "écritures de l'exercice validé numérotées (2)");
        Check(c2.Year(y.Id)!.Validated, "exercice toujours validé");
        Check(c2.VerifyIntegrity(y.Id), "empreinte v1 toujours exacte après migration");
        Check(c2.VerifyAuditChain().Ok, "journal d'avant migration scellé et chaîné");
        Throws(() => Sql(f2, "UPDATE entries SET amount_cents = 1 WHERE year_id = {0}", y.Id), "exercice reverrouillé après migration", "validé");
        Throws(() => Sql(f2, "DELETE FROM audit_log WHERE id = 1"), "journal reverrouillé après migration", "purgé");
        Eq(1, c2.UpgradeIntegritySeals(), "mise à niveau de l'empreinte après migration");
        Check(c2.VerifyIntegrity(y.Id), "empreinte v2 conforme");
        // Le logiciel continue d'évoluer : numéros de compte (paramétrage) modifiables sur exercice validé.
        c2.SetCategoryAccountNumber(CategoryKind.Product, adh.Name, "756000");
        Check(c2.VerifyIntegrity(y.Id), "numéro de compte saisi : empreinte inchangée");
        Check(c2.ExportFec(y.Id).Contains("756000\t" + adh.Name), "FEC : numéro de compte saisi utilisé");
    }
    Cleanup(path);
}

Console.WriteLine("Conformité : numérotation à la clôture avec écritures déplacées");
{
    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var banque = c.Accounts().First(a => a.Name == "Banque");
    var adh = c.Categories(y.Id).First(k => k.Kind == CategoryKind.Product);
    long Add(DateOnly d, string l) => c.SaveEntry(new Entry(0, y.Id, d, l, false, EntryKind.Normal, banque.Id, Direction.In, null, 1000, null, null),
        new[] { new Allocation(adh.Id, 1000) });
    Add(Dt(2026, 10, 1), "A");
    var late = Add(Dt(2027, 2, 15), "B tardive");
    Add(Dt(2026, 11, 1), "C");
    var cancelled = Add(Dt(2027, 3, 1), "D tardive annulée");
    c.CancelEntry(cancelled);
    c.ValidateYear(y.Id, Dt(2027, 1, 31));
    var rows = c.Entries(y.Id, includeCancelled: true);
    Eq(2, rows.Count, "exercice clôturé : seules les écritures avant la clôture");
    Check(rows.Select(r => r.Entry.Seq).SequenceEqual(new long[] { 1, 2 }), "exercice clôturé : numérotation sans trou");
    var next = c.NextYear(c.Year(y.Id)!)!;
    var moved = c.Entries(next.Id, includeCancelled: true);
    Eq(3, moved.Count, "exercice suivant : écritures déplacées (annulation comprise)");
    Check(moved.Select(r => r.Entry.Seq).OrderBy(s => s).SequenceEqual(new long[] { 1, 2, 3 }), "exercice suivant : numérotation continue");
    Check(c.VerifyIntegrity(y.Id), "empreinte conforme après déplacement et renumérotation");
    Check(c.AuditLog(20).Any(l => l.Action == "Exercice validé" && l.Detail!.Contains("numéros (ancien→nouveau)")), "renumérotation tracée");
}

Console.WriteLine("Conformité : exports (livre-journal, FEC)");
{
    var f = NewFactory(TempDb());
    var c = NewCompta(f);
    var y = c.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var banque = c.Accounts().First(a => a.Name == "Banque");
    var livret = c.Accounts().First(a => a.Name == "Livret A");
    c.SetOpening(y.Id, banque.Id, 100000);
    var cats = c.Categories(y.Id);
    var adh = cats.First(k => k.Kind == CategoryKind.Product && k.Name == "Adhésions");
    var assur = cats.First(k => k.Kind == CategoryKind.Charge && k.Name == "Assurance");
    c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 1), "Cotisations; \"été\"", true, EntryKind.Normal, banque.Id, Direction.In, null, 12345, null, null),
        new[] { new Allocation(adh.Id, 12345) });
    var bad = c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 2), "Assurance (erreur)", false, EntryKind.Normal, banque.Id, Direction.Out, null, 4000, null, null),
        new[] { new Allocation(assur.Id, 4000) });
    c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 3), "Virement livret", false, EntryKind.Transfer, banque.Id, Direction.Out, livret.Id, 2000, null, null),
        Array.Empty<Allocation>());
    c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 4), "Non classé", false, EntryKind.Normal, banque.Id, Direction.Out, null, 700, null, null),
        Array.Empty<Allocation>(), strict: false);
    c.SaveEntry(new Entry(0, y.Id, Dt(2026, 10, 5), "Remb. Julien", false, EntryKind.MemberRefund, banque.Id, Direction.Out, null, 900, "Julien", null),
        new[] { new Allocation(assur.Id, 900) });
    c.CancelEntry(bad, "erreur de saisie");
    c.AddAdjustment(y.Id, AdjustmentType.ToNext, CategoryKind.Charge, assur.Id, "Assurance 2027", 1500, "Assurance");

    var csv = c.ExportLedgerCsv(y.Id);
    var csvLines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    Eq(1 + 6, csvLines.Length, "CSV : en-tête + 6 lignes (annulation comprise)");
    Check(csvLines[0].StartsWith("N°;Date;Libellé"), "CSV : en-tête");
    Check(csvLines[1].Contains("\"Cotisations; \"\"été\"\"\""), "CSV : champs échappés");
    Check(csv.Contains("123,45"), "CSV : montants à la virgule");
    Check(csv.Contains(";Annulée;") && csv.Contains(";Annulation;"), "CSV : annulations signalées");

    var pdf = c.ExportLedgerPdf(y.Id, "Association test");
    var head = System.Text.Encoding.Latin1.GetString(pdf, 0, 8);
    var tail = System.Text.Encoding.Latin1.GetString(pdf, pdf.Length - 8, 8);
    Check(head.StartsWith("%PDF-1.4") && tail.Contains("%%EOF"), "PDF : structure valide");

    var fec = c.ExportFec(y.Id);
    var fecLines = fec.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    Eq(18, fecLines[0].Split('\t').Length, "FEC : 18 colonnes en en-tête");
    Check(fecLines.Skip(1).All(l => l.Split('\t').Length == 18), "FEC : 18 colonnes sur chaque ligne");
    decimal Dec(string s) => decimal.Parse(s.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
    var debit = fecLines.Skip(1).Sum(l => Dec(l.Split('\t')[11]));
    var credit = fecLines.Skip(1).Sum(l => Dec(l.Split('\t')[12]));
    Eq(debit, credit, "FEC : débit total = crédit total");
    foreach (var g in fecLines.Skip(1).GroupBy(l => l.Split('\t')[2]))
        if (g.Sum(l => Dec(l.Split('\t')[11])) != g.Sum(l => Dec(l.Split('\t')[12])))
            Check(false, "FEC : écriture n° " + g.Key + " déséquilibrée");
    Check(fecLines.Skip(1).Any(l => l.Split('\t')[0] == "AN"), "FEC : à-nouveaux de l'exercice");
    Check(fecLines.Skip(1).Any(l => l.Split('\t')[4] == "471000"), "FEC : écriture non classée sur compte d'attente");
    Check(fecLines.Skip(1).Any(l => l.Split('\t')[4] == "486000"), "FEC : régularisation (charge constatée d'avance)");
    Check(fecLines.Skip(1).Select(l => l.Split('\t')[3]).All(d => d.Length == 8), "FEC : dates AAAAMMJJ");
    Eq("000000000FEC20270831.txt", Compta.FecFileName(null, y.End), "FEC : nom de fichier sans SIREN");
    Eq("123456789FEC20270831.txt", Compta.FecFileName("123 456 789", y.End), "FEC : nom de fichier avec SIREN");
    Throws(() => c.SetCategoryAccountNumber(CategoryKind.Charge, "Assurance", "12"), "numéro de compte trop court refusé", "3 à 20");

    // Import du FEC exporté dans une base neuve : aller-retour.
    var f2 = NewFactory(TempDb());
    var c2 = NewCompta(f2);
    var y2 = c2.CreateYear(Dt(2026, 9, 1), Dt(2027, 8, 31));
    var plan = c2.PlanFecImport(y2.Id, fec);
    Eq(6, plan.Entries.Count, "import FEC : 6 écritures de trésorerie (régularisation ignorée)");
    Eq(1, plan.NoCash, "import FEC : régularisation sans mouvement d'argent ignorée");
    var res = c2.ApplyFecImport(plan);
    Eq(6, res.Created, "import FEC : écritures créées");
    Eq(0, res.Failed.Count, "import FEC : aucune écriture refusée");
    var banque2 = c2.Accounts().First(a => a.Name == "Banque");
    Eq(100000L, c2.Opening(y2.Id, banque2.Id), "import FEC : solde d'ouverture repris des à-nouveaux");
    var back = c2.Entries(y2.Id, includeCancelled: true);
    Check(back.Any(r => r.Entry.Label.Contains("Cotisations") && r.Entry.AmountCents == 12345 && r.Allocations.Count == 1),
        "import FEC : produit ventilé");
    Check(back.Any(r => r.Entry.Kind == EntryKind.Transfer && r.Entry.AmountCents == 2000), "import FEC : virement entre comptes");
    Check(back.Any(r => r.Entry.Label == "Non classé" && r.Allocations.Count == 0), "import FEC : non classé à ventiler");
    Eq(0, c2.PlanFecImport(y2.Id, fec).Entries.Count, "import FEC : second import sans doublon");

    // FEC comptable avec TVA et séparateur « ; » : la TVA est reportée sur la charge (ventilation = montant payé).
    var f3 = NewFactory(TempDb());
    var c3 = NewCompta(f3);
    var y3 = c3.CreateYear(Dt(2026, 1, 1), Dt(2026, 12, 31));
    var vat = "JournalCode;JournalLib;EcritureNum;EcritureDate;CompteNum;CompteLib;CompAuxNum;CompAuxLib;PieceRef;PieceDate;EcritureLib;Debit;Credit;EcritureLet;DateLet;ValidDate;Montantdevise;Idevise\r\n"
              + "BQ;Banque;12;20260315;606300;Petit équipement;;;F1;20260315;Achat café;100,00;0,00;;;;;\r\n"
              + "BQ;Banque;12;20260315;445660;TVA déductible;;;F1;20260315;Achat café;20,00;0,00;;;;;\r\n"
              + "BQ;Banque;12;20260315;512000;Banque;;;F1;20260315;Achat café;0,00;120,00;;;;;\r\n"
              + "VE;Ventes;3;20260316;411000;Clients;;;F2;20260316;Facture;50,00;0,00;;;;;\r\n"
              + "VE;Ventes;3;20260316;706000;Prestations;;;F2;20260316;Facture;0,00;50,00;;;;;\r\n";
    var vplan = c3.PlanFecImport(y3.Id, vat);
    Eq(1, vplan.Entries.Count, "FEC TVA : une écriture de trésorerie");
    Eq(1, vplan.NoCash, "FEC TVA : vente à crédit ignorée");
    var vres = c3.ApplyFecImport(vplan);
    Eq(1, vres.Created, "FEC TVA : écriture créée");
    var ve = c3.Entries(y3.Id).Single();
    Eq(12000L, ve.Entry.AmountCents, "FEC TVA : montant payé TTC");
    Eq(12000L, ve.Allocations.Sum(a => a.AmountCents), "FEC TVA : ventilation = montant payé");
    Throws(() => c3.PlanFecImport(y3.Id, "a;b\r\n1;2\r\n"), "fichier non FEC refusé", "n'est pas un FEC");
}

// ------------------------------------------------------------------ licences
Console.WriteLine("Licences");
{
    var now = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    var clock = now;
    var handler = new FakeLicenseHandler();
    var store = new MemoryLicenseStore();
    var dir = Path.Combine(Path.GetTempPath(), "tcf-lic-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var device = new FakeDevice();
    var licOptions = new LicenseOptions { ProductSlug = "tout-compte-fait", StoreName = "ToutCompteFait" };
    LicenseService NewService() => new(new LicenseClient(licOptions, handler), device, store, new BypassFile(dir), () => clock);
    string Ok(string key, string type = "full", bool full = true, string? expires = null, string features = "", string? customer = "Asso Test", string extra = "") =>
        $"{{\"valid\":true,\"license\":{{\"key\":\"{key}\",\"type\":\"{type}\",\"expiresAt\":{(expires is null ? "null" : "\"" + expires + "\"")}," +
        $"\"features\":[{features}],\"isFull\":{(full ? "true" : "false")},\"customerName\":{(customer is null ? "null" : "\"" + customer + "\"")},\"maxActivations\":2}}{extra}}}";
    string Refused(string reason) => $"{{\"valid\":false,\"reason\":\"{reason}\"}}";

    // Identité de machine : stable, SHA-256 de MachineGuid.
    if (OperatingSystem.IsWindows())
    {
        var id1 = new WindowsDeviceIdentity().DeviceId;
        var id2 = new WindowsDeviceIdentity().DeviceId;
        Eq(id1, id2, "deviceId stable");
        Check(id1.Length == 64 && id1.All(Uri.IsHexDigit), "deviceId = SHA-256 hexadécimal");
        using var rk = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var guid = (string)rk!.GetValue("MachineGuid")!;
        Eq(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(guid))).ToLowerInvariant(), id1,
            "deviceId = SHA-256 de MachineGuid");

        // Stockage registre + DPAPI (clé de test, jamais celle de l'application).
        var testKey = @"Software\TomLine prod&co\ToutCompteFait.Tests\" + Guid.NewGuid().ToString("N");
        var ws = new WindowsLicenseStore("ToutCompteFait.Tests", testKey);
        Check(ws.Load() is null, "registre : aucune licence au départ");
        ws.Save(new StoredLicense { Key = "SECRET-KEY-123", Type = "full", IsFull = true, CustomerName = "Asso", LastValidatedAt = now });
        var back = ws.Load();
        Check(back is { Key: "SECRET-KEY-123", IsFull: true, CustomerName: "Asso" }, "registre : licence relue");
        using (var raw = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
        {
            var blob = (byte[])raw!.GetValue("License")!;
            Check(!System.Text.Encoding.UTF8.GetString(blob).Contains("SECRET-KEY-123"), "registre : clé jamais en clair (DPAPI)");
        }
        ws.Clear();
        Check(ws.Load() is null, "registre : licence supprimée");
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKey, false);
    }

    // Démo : la clé reçue est stockée automatiquement.
    handler.Reply = (_, _) => Ok("DEMO-KEY-1", "demo", full: false, expires: "2026-10-19T10:00:00Z");
    var svc = NewService();
    var st = await svc.RequestDemoAsync();
    Eq(LicenseStatus.Valid, st.Status, "démo : licence valide");
    Eq("DEMO-KEY-1", store.Current?.Key, "démo : clé stockée automatiquement");
    Eq(7, st.DaysLeft(now), "démo : 7 jours restants");
    Check(svc.HasFeature("export-fec") && svc.HasFeature("autre"), "démo : tout est débloqué");
    Check(handler.LastPath == "/v1/request-demo" && handler.LastBody.Contains("\"productSlug\":\"tout-compte-fait\"")
          && handler.LastBody.Contains("\"deviceId\":\"" + device.DeviceId + "\""), "démo : corps de requête");

    // Activation d'une clé : refus puis succès.
    store.Current = null;
    handler.Reply = (_, _) => Refused("license_not_found");
    st = await svc.ActivateAsync("BAD-KEY");
    Check(st.Status == LicenseStatus.NoLicense && st.Reason == "license_not_found" && store.Current is null, "activation refusée : rien de stocké");
    handler.Reply = (_, _) => Refused("activation_limit_reached");
    st = await svc.ActivateAsync("KEY-FULL");
    Eq("activation_limit_reached", st.Reason, "activation : limite atteinte");
    handler.Reply = (_, _) => Ok("KEY-FULL", features: "\"export-fec\"", full: false, type: "standard");
    st = await svc.ActivateAsync("KEY-FULL");
    Eq(LicenseStatus.Valid, st.Status, "activation réussie");
    Check(svc.HasFeature("export-fec") && !svc.HasFeature("modele-x"), "fonctionnalités : liste de la licence");
    Eq("Asso Test", st.License!.CustomerName, "nom du client conservé");
    handler.Reply = (_, _) => Ok("KEY-FULL", full: true);
    st = await svc.RevalidateAsync();
    Check(svc.HasFeature("nimporte-quoi"), "licence pleine : toutes les fonctionnalités");

    // Validation au lancement : le serveur fait foi, sans grâce.
    handler.Reply = (_, _) => Refused("license_revoked");
    st = await NewService().StartAsync();
    Check(st.Status == LicenseStatus.Locked && st.Reason == "license_revoked", "révoquée : verrouillage immédiat");
    Check(store.Current is null, "révoquée : licence locale supprimée");

    // Serveur injoignable : grâce hors-ligne.
    store.Current = new StoredLicense { Key = "K", Type = "standard", IsFull = true, LastValidatedAt = now.AddDays(-10) };
    handler.Reply = (_, _) => throw new HttpRequestException("réseau coupé");
    st = await NewService().StartAsync();
    Eq(LicenseStatus.OfflineGrace, st.Status, "hors-ligne : grâce 30 jours");
    Eq(now.AddDays(20), st.GraceUntil, "hors-ligne : jusqu'à dernière validation + 30 j");
    store.Current = new StoredLicense { Key = "K", IsFull = true, LastValidatedAt = now.AddDays(-31) };
    st = await NewService().StartAsync();
    Check(st.Status == LicenseStatus.Locked && st.Reason == "offline_grace_expired", "hors-ligne : grâce épuisée");
    store.Current = new StoredLicense { Key = "K", IsFull = false, Type = "demo", ExpiresAt = now.AddDays(2), LastValidatedAt = now.AddDays(-1) };
    st = await NewService().StartAsync();
    Eq(now.AddDays(2), st.GraceUntil, "hors-ligne : plafonné par l'échéance");
    clock = now.AddDays(3);
    st = await NewService().StartAsync();
    Check(st.Status == LicenseStatus.Locked && st.Reason == "license_expired", "hors-ligne : échéance dépassée");
    clock = now;
    store.Current = new StoredLicense { Key = "K", IsFull = true, LastValidatedAt = null };
    st = await NewService().StartAsync();
    Check(st.Status == LicenseStatus.Locked && st.Reason == "no_prior_validation", "hors-ligne sans validation préalable : aucune grâce");
    handler.Reply = (_, _) => throw new TaskCanceledException("délai dépassé");
    store.Current = new StoredLicense { Key = "K", IsFull = true, LastValidatedAt = now };
    Eq(LicenseStatus.OfflineGrace, (await NewService().StartAsync()).Status, "délai dépassé : traité comme hors-ligne");

    // Désactivation.
    handler.Reply = (_, _) => "{\"valid\":true}";
    svc = NewService();
    store.Current = new StoredLicense { Key = "K2", IsFull = true, LastValidatedAt = now };
    var (done, _) = await svc.DeactivateAsync();
    Check(done && store.Current is null && handler.LastPath == "/v1/deactivate", "désactivation : serveur puis suppression locale");
    store.Current = new StoredLicense { Key = "K2", IsFull = true, LastValidatedAt = now };
    handler.Reply = (_, _) => throw new HttpRequestException("réseau coupé");
    var (done2, reason2) = await NewService().DeactivateAsync();
    Check(!done2 && reason2 == "network" && store.Current is not null, "désactivation hors-ligne : licence conservée");

    // licence.ini : licence gratuite, aucun /v1/validate, ping avec le nom du client.
    var ini = Path.Combine(dir, "licence.ini");
    File.WriteAllText(ini, "Association de démonstration\n");
    store.Current = new StoredLicense { Key = "K3", IsFull = true, LastValidatedAt = now };
    var calls = new List<string>();
    handler.Reply = (path, _) => { calls.Add(path); return "{\"valid\":true}"; };
    st = await NewService().StartAsync();
    Eq(LicenseStatus.Bypass, st.Status, "licence.ini : mode licence gratuite");
    Check(calls.SequenceEqual(new[] { "/v1/bypass/ping" }), "licence.ini : ping seulement, aucune validation");
    Check(System.Text.Json.JsonDocument.Parse(handler.LastBody).RootElement.GetProperty("customerName").GetString() == "Association de démonstration",
        "ping : nom du client lu dans licence.ini");
    Check(NewService().HasFeature("x") == false, "service neuf : rien avant démarrage");
    File.WriteAllText(ini, "");
    await NewService().StartAsync();
    Check(!handler.LastBody.Contains("customerName"), "ping : customerName absent si licence.ini vide");

    // remove_bypass : suppression du fichier, /v1/bypass/removed, retour au mode licence normale.
    calls.Clear();
    handler.Reply = (path, _) =>
    {
        calls.Add(path);
        return path == "/v1/bypass/ping" ? "{\"valid\":true,\"command\":\"remove_bypass\"}"
             : path == "/v1/validate" ? Ok("K3", full: true) : "{\"valid\":true}";
    };
    st = await NewService().StartAsync();
    Check(!File.Exists(ini), "remove_bypass : licence.ini supprimé");
    Check(calls.SequenceEqual(new[] { "/v1/bypass/ping", "/v1/bypass/removed", "/v1/validate" }), "remove_bypass : ping, removed puis validation normale");
    Eq(LicenseStatus.Valid, st.Status, "remove_bypass : retour en licence normale");

    // install_bypass (commande de /v1/validate) : création de licence.ini (bypassName) puis ping.
    calls.Clear();
    handler.Reply = (path, _) =>
    {
        calls.Add(path);
        return path == "/v1/validate" ? Ok("K3", customer: null, extra: ",\"commands\":[\"install_bypass\"],\"bypassName\":\"Client Gratuit\"")
                                      : "{\"valid\":true}";
    };
    st = await NewService().StartAsync();
    Check(File.Exists(ini) && File.ReadAllText(ini).Contains("Client Gratuit"), "install_bypass : licence.ini créé avec bypassName");
    Check(calls.SequenceEqual(new[] { "/v1/validate", "/v1/bypass/ping" }), "install_bypass : validation puis ping");
    Eq(LicenseStatus.Bypass, st.Status, "install_bypass : passage en licence gratuite");
    // Fichier existant vide + licence avec customerName : jamais écrasé.
    File.Delete(ini);
    store.Current = new StoredLicense { Key = "K3", IsFull = true, LastValidatedAt = now };
    handler.Reply = (path, _) => path != "/v1/bypass/ping"
        ? Ok("K3", customer: "Asso Test", extra: ",\"commands\":[\"install_bypass\"],\"bypassName\":\"Autre\"")
        : "{\"valid\":true}";
    var svc3 = NewService();
    await svc3.ActivateAsync("K3");          // état « valide » sans licence.ini
    File.WriteAllText(ini, "");               // le fichier apparaît ensuite (vide)
    await svc3.RevalidateAsync();
    Eq("", File.ReadAllText(ini), "install_bypass : licence.ini vide non écrasé (licence avec nom de client)");

    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine(failures == 0 ? $"TOUT EST OK ({checks} vérifications)" : $"{failures} ECHEC(S) sur {checks} vérifications");
return failures == 0 ? 0 : 1;

sealed class FakeLicenseHandler : HttpMessageHandler
{
    public Func<string, string, string> Reply { get; set; } = (_, _) => "{\"valid\":true}";
    public string LastPath { get; private set; } = "";
    public string LastBody { get; private set; } = "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastPath = request.RequestUri!.AbsolutePath;
        LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var body = Reply(LastPath, LastBody);   // peut lever pour simuler une coupure réseau
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

sealed class MemoryLicenseStore : ILicenseStore
{
    public StoredLicense? Current { get; set; }
    public StoredLicense? Load() => Current;
    public void Save(StoredLicense license) => Current = license;
    public void Clear() => Current = null;
}

sealed class FakeDevice : IDeviceIdentity
{
    public string DeviceId => "abc123";
    public string DeviceName => "POSTE-TEST";
}

/// <summary>Contexte de génération de schéma hors connexion (le vrai contexte détecte la version du serveur MariaDB en
/// se connectant) : sert uniquement à vérifier que le modèle se traduit pour chaque moteur.</summary>
sealed class ServerScriptContext : AppDbContext
{
    private readonly DatabaseProvider _p;

    public ServerScriptContext(DatabaseProvider p)
        : base(DbTarget.ForServer(new DbConnectionInfo { Provider = p, Host = "x", Database = "x", UserName = "x" }))
    {
        _p = p;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder b)
    {
        switch (_p)
        {
            case DatabaseProvider.PostgreSql: b.UseNpgsql("Host=x;Database=x;Username=x;Password=x"); break;
            case DatabaseProvider.MariaDb:
                b.UseMySql("Server=x;Database=x;User=x;Password=x", new MySqlServerVersion(new Version(10, 11, 0)));
                break;
            default: b.UseSqlServer("Server=x;Database=x;User Id=x;Password=x;TrustServerCertificate=true"); break;
        }
    }
}



