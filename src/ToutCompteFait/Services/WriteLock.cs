using System.IO;
using System.Text;

namespace ToutCompteFait.Services;

/// <summary>Verrou « un seul rédacteur » (même principe que FiltresApp) : le premier poste qui ouvre la base garde
/// ouvert en exclusivité d'écriture un fichier « &lt;base&gt;.lock » voisin ; les autres ouvrent la base en
/// lecture seule. Repose sur les modes de partage de fichier Windows : libéré automatiquement par le système si
/// l'application plante ou si le poste s'éteint (aucun verrou orphelin à nettoyer).</summary>
public sealed class WriteLock : IDisposable
{
    private readonly FileStream _stream;
    private WriteLock(FileStream s) => _stream = s;

    public static string LockPath(string dbPath) => dbPath + ".lock";

    public static WriteLock? TryAcquire(string dbPath)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(LockPath(dbPath), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var info = Encoding.UTF8.GetBytes($"{Environment.UserName}\nPoste : {Environment.MachineName}\nDepuis le {DateTime.Now:dd/MM/yyyy HH:mm}");
        stream.SetLength(0);
        stream.Write(info);
        stream.Flush(flushToDisk: true);
        return new WriteLock(stream);
    }

    /// <summary>« utilisateur (poste) » du rédacteur actuel, ou null si illisible.</summary>
    public static string? ReadOwner(string dbPath)
    {
        try
        {
            using var s = new FileStream(LockPath(dbPath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(s, Encoding.UTF8);
            var user = r.ReadLine()?.Trim();
            var host = r.ReadLine()?.Replace("Poste :", "").Trim();
            if (string.IsNullOrEmpty(user)) return null;
            return string.IsNullOrEmpty(host) ? user : $"{user} ({host})";
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose() => _stream.Dispose();
}
