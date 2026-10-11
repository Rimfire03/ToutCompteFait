using ComptaAepby.Core;
using ComptaAepby.Core.Data;
using ComptaAepby.Core.Import;
using ComptaAepby.Core.Security;
using ComptaAepby.Core.Services;
using Microsoft.EntityFrameworkCore;

// Utilitaire : « makedb <fichier.db> <classeur.xlsx>... » crée une base à partir de classeurs Excel.
if (args.Length >= 3 && args[0] == "makedb")
{
    var target = args[1];
    if (File.Exists(target)) { Console.WriteLine("Le fichier existe déjà : " + target); return 2; }
    var mf = new DbContextFactory(target);
    mf.EnsureDatabaseUpToDate("makedb");
    var mc = new Compta(mf, Session.System);
    var rep = ExcelImporter.Import(mc, args.Skip(2));
    Console.WriteLine(rep.ToString());
    return 0;
}

// Utilitaire : « adduser <fichier.db> <identifiant> <mot de passe> » crée le premier administrateur (base de démonstration).
if (args.Length >= 4 && args[0] == "adduser")
{
    var af = new DbContextFactory(args[1]);
    var au = new AuthService(af).CreateFirstAdmin(args[2], args[2], args[3]);
    Console.WriteLine($"Administrateur « {au.UserName} » créé.");
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
            cmd.CommandText = "SELECT " + string.Join(", ", props.Select(p => "\"" + p.Name + "\"")) + " FROM \"" + table + "\"";
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
        c.DeleteEntry(nvId);

        c.ValidateYear(y.Id);
        Check(c.Year(y.Id)!.Validated, "exercice validé");
        Check(c.VerifyIntegrity(y.Id), "empreinte valide");
        var anyEntry = c.Entries(y.Id).First().Entry;
        Throws(() => c.SaveEntry(new Entry(0, y.Id, Dt(2026, 11, 3), "après validation", false, EntryKind.Normal, banque.Id, Direction.Out, null, 100, null, null),
            new[] { new Allocation(assur.Id, 100) }), "ajout refusé (service)", "validé");
        Throws(() => c.DeleteEntry(anyEntry.Id), "suppression refusée (service)", "validé");
        Throws(() => c.AddCategory(y.Id, CategoryKind.Charge, "Nouvelle"), "catégorie refusée (service)", "validé");
        Throws(() => c.SetPointed(anyEntry.Id, false), "pointage refusé (service)", "validé");

        // Défense en profondeur : couche EF (garde SaveChanges) puis déclencheurs SQLite.
        using (var ctx = f.Create())
        {
            var e = ctx.Entries.First();
            e.AmountCents += 1;
            Throws(() => ctx.SaveChanges(), "modification refusée (garde EF sur SaveChanges)", "validé");
        }
        Throws(() => Sql(f, "DELETE FROM entries WHERE year_id = {0}", y.Id), "suppression refusée (trigger SQL)", "validé");
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
        Throws(() => cl.DeleteEntry(id), "lecteur : suppression refusée", "ne permet pas");
        Throws(() => cl.AddCategory(y.Id, CategoryKind.Charge, "Nouvelle"), "lecteur : catégorie refusée", "ne permet pas");
        Throws(() => cl.CreateYear(Dt(2028, 9, 1), Dt(2029, 8, 31)), "lecteur : exercice refusé", "ne permet pas");
        Eq(1, cl.Entries(y.Id).Count, "lecteur : consultation autorisée");
        Check(cl.ComputeBilan(y.Id).ChargesTotal == 1000, "lecteur : bilan consultable");

        // Validation : trésorier oui ; réouverture : administrateur seulement.
        ct.ValidateYear(y.Id);
        Throws(() => ct.ReopenYear(y.Id, "oups"), "trésorier : réouverture refusée", "ne permet pas");
        ca.ReopenYear(y.Id, "contrôle");
        Check(ca.Year(y.Id)!.Open, "administrateur : réouverture autorisée");
        Throws(() => ExcelImporter.Import(ct, Array.Empty<string>()), "trésorier : import Excel refusé", "ne permet pas");

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

    var csv2 = "Date,Description,Amount\n2026-02-01,Carte AEPBY,-12.50\n2026-02-02,Remboursement,30.00\n";
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

// ------------------------------------------------------------------ rapprochement avec les classeurs Excel
Console.WriteLine("Import Excel et rapprochement avec les formules du classeur");
{
    const string root = @"G:\Mon Drive\Trésorerie";
    var f2425 = Path.Combine(root, "2024-2025", "livre 2024-2025.xlsx");
    var f2526 = Path.Combine(root, "2025-2026", "livre 2025-2026.xlsx");
    var f2627 = new[] { "livre v2.xlsx", "livre2026-2027.xlsx", "livre 2026-2027.xlsx" }
        .Select(n => Path.Combine(root, "2026-2027", n)).FirstOrDefault(File.Exists) ?? Path.Combine(root, "2026-2027", "livre v2.xlsx");
    if (!File.Exists(f2425) || !File.Exists(f2526) || !File.Exists(f2627))
    {
        Console.WriteLine("  (classeurs Excel absents : rapprochement ignoré)");
    }
    else
    {
        var path = TempDb();
        {
            var f = NewFactory(path);
            var c = NewCompta(f);
            ExcelImporter.Import(c, new[] { f2627, f2425, f2526 }); // ordre volontairement mélangé
            var years = c.Years();
            Eq(3, years.Count, "3 exercices importés");
            var y24 = years[0]; var y25 = years[1]; var y26 = years[2];
            Eq("2024-2025", y24.Label, "libellé 2024-2025");
            Eq("2025-2026", y25.Label, "libellé 2025-2026");
            Eq("2026-2027", y26.Label, "libellé 2026-2027");

            var banque = c.Accounts().First(a => a.Name == "Banque");
            var livret = c.Accounts().First(a => a.Name == "Livret A");

            var b25 = c.ComputeBilan(y25.Id);
            decimal Cat(Bilan b, CategoryKind k, string name) =>
                (k == CategoryKind.Product ? b.Products : b.Charges).First(x => x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)).Total / 100m;
            Eq(2985.08m, Cat(b25, CategoryKind.Charge, "Investissement"), "2025-26 charges Investissement");
            Eq(388.23m, Cat(b25, CategoryKind.Charge, "Garage"), "2025-26 charges Garage");
            Eq(4071.56m, Cat(b25, CategoryKind.Charge, "Loto Octobre"), "2025-26 charges Loto Octobre (3 003,49 + report n-1 1 068,07)");
            Eq(5280.79m, Cat(b25, CategoryKind.Charge, "Kermesse"), "2025-26 charges Kermesse");
            Eq(10060m, Cat(b25, CategoryKind.Charge, "Subvention annuel"), "2025-26 charges Subvention annuel");
            Eq(58.12m, Cat(b25, CategoryKind.Charge, "Loto n+1"), "2025-26 charges Loto n+1 (après report n+1)");
            Eq(1310.75m, Cat(b25, CategoryKind.Product, "Adhésions"), "2025-26 produits Adhésions");
            Eq(10854.81m, Cat(b25, CategoryKind.Product, "Loto Octobre"), "2025-26 produits Loto Octobre");
            Eq(6519.23m, Cat(b25, CategoryKind.Product, "Loto mars"), "2025-26 produits Loto mars");
            Eq(2559.67m, Cat(b25, CategoryKind.Product, "Locations"), "2025-26 produits Locations");
            Eq(3251821L, b25.ChargesTotal, "2025-26 total charges (Excel 32 518,21)");
            Eq(3792761L, b25.ProductsTotal, "2025-26 total produits (Excel 37 927,61)");
            Eq(540940L, b25.Result, "2025-26 résultat (Excel 5 409,40)");
            Eq(81308L, b25.Report, "2025-26 report au passif (Excel 813,08)");
            Eq(1910587L, b25.OpeningTotal, "2025-26 ouverture (Excel 19 105,87)");
            Eq(2246381L, b25.Accounts.First(a => a.Account.Id == banque.Id).Closing, "2025-26 solde banque (Excel 22 463,81)");
            Eq(304646L, b25.Accounts.First(a => a.Account.Id == livret.Id).Closing, "2025-26 solde livret (Excel 3 046,46)");
            Eq(2551027L, b25.ClosingTotal, "2025-26 actif (Excel 25 510,27)");
            Eq(0L, b25.BalanceDifference, "2025-26 bilan équilibré dans le logiciel");
            Eq(18192L, b25.UnallocatedGap, "2025-26 écart de ventilation = écart actif/passif d'Excel (181,92)");

            var b24 = c.ComputeBilan(y24.Id);
            Eq(3197981L + 16899L, b24.ChargesTotal, "2024-25 total charges (Excel 31 979,81 + 168,99 saisis en texte)");
            Eq(2909453L, b24.ProductsTotal, "2024-25 total produits (Excel 29 094,53)");
            Eq(-288528L - 16899L, b24.Result, "2024-25 résultat (Excel -2 885,28 − 168,99)");
            Eq(1899156L, b24.Accounts.First(a => a.Account.Id == banque.Id).Closing, "2024-25 solde banque (Excel 18 991,56)");
            Eq(11431L, b24.Accounts.First(a => a.Account.Id == livret.Id).Closing, "2024-25 solde livret (Excel 114,31)");
            Eq(2135881L, b24.OpeningTotal, "2024-25 ouverture (Excel 21 358,81)");
            Eq(0L, b24.BalanceDifference, "2024-25 bilan équilibré dans le logiciel");
            Eq(1910587L, b25.OpeningTotal, "2025-26 ouvre sur la clôture 2024-25");

            var b26 = c.ComputeBilan(y26.Id);
            Eq(2551027L, b26.OpeningTotal, "2026-27 ouverture = clôture 2025-26");
            var carried = c.Adjustments(y26.Id);
            Eq(1, carried.Count, "2026-27 : 1 report reçu");
            Eq(25499L, carried[0].AmountCents, "2026-27 : report 254,99");
            Check(carried[0].CategoryId is null, "2026-27 : catégorie à choisir (non devinée)");
            Eq(0L, b26.BalanceDifference, "2026-27 bilan équilibré");
            Check(c.ValidationIssues(y25.Id).Any(i => i.Blocking), "2025-26 non validable tant que les écritures ne sont pas corrigées");
        }
        Cleanup(path);
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
    Eq(1, c.Entries(y.Id).Count, "une seule écriture reste dans l'exercice clôturé");
    Eq(2, c.Entries(ny.Id).Count, "deux écritures déplacées dans l'exercice suivant");
    var nAdh = c.Categories(ny.Id).First(k => k.Kind == CategoryKind.Product && k.Name == "Adhésions");
    Check(c.Entries(ny.Id).All(r => r.Allocations.All(a => a.CategoryId == nAdh.Id)), "ventilation rattachée aux catégories de l'exercice suivant");
    Eq(10000L, c.ComputeBilan(y.Id).ProductsTotal, "produits de l'exercice clôturé");
    Eq(2500L, c.ComputeBilan(ny.Id).ProductsTotal, "produits de l'exercice suivant");
    Eq(110000L, c.Opening(ny.Id, banque.Id), "solde d'ouverture de l'exercice suivant");
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

Console.WriteLine(failures == 0 ? $"TOUT EST OK ({checks} vérifications)" : $"{failures} ECHEC(S) sur {checks} vérifications");
return failures == 0 ? 0 : 1;

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



