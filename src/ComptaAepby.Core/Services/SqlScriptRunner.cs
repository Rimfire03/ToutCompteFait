using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace ComptaAepby.Core.Services;

/// <summary>Exécute un script SQL (fichier .sql) sur la base ouverte. Le script est envoyé tel quel au moteur
/// (pas de paramètres : les accolades ou apostrophes des données ne sont pas interprétées). S'il ne gère pas lui-même
/// sa transaction (BEGIN … COMMIT), il est exécuté dans une transaction : en cas d'erreur, rien n'est appliqué.</summary>
public static class SqlScriptRunner
{
    public static void Run(DbContextFactory factory, string script)
    {
        if (string.IsNullOrWhiteSpace(script)) throw new InvalidOperationException("Le script SQL est vide.");
        using var ctx = factory.Create();
        var conn = ctx.Database.GetDbConnection();
        conn.Open();
        try
        {
            var ownTransaction = Regex.IsMatch(script, @"^\s*(BEGIN|START\s+TRANSACTION|BEGIN\s+TRAN)\b",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            System.Data.Common.DbTransaction? tx = ownTransaction ? null : conn.BeginTransaction();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = 0;
                cmd.CommandText = script;
                cmd.ExecuteNonQuery();
                tx?.Commit();
            }
            catch
            {
                try { tx?.Rollback(); } catch { /* connexion déjà annulée */ }
                if (ownTransaction)
                {
                    // Le script a ouvert sa propre transaction : la fermer si la connexion est restée dedans.
                    try
                    {
                        using var rb = conn.CreateCommand();
                        rb.CommandText = "ROLLBACK";
                        rb.ExecuteNonQuery();
                    }
                    catch { /* aucune transaction ouverte */ }
                }
                throw;
            }
        }
        finally
        {
            conn.Close();
        }
    }
}
