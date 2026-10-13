using TomLine.Licensing;

namespace ToutCompteFait.Core.Licensing;

/// <summary>Fichier de licence gratuite <c>licence.ini</c> (toujours ce nom, à côté de l'exécutable). Son contenu est le
/// nom du client ; quand le fichier est posé par l'application (commande du serveur de licences), une ligne
/// <c>edition=&lt;version&gt;</c> y est ajoutée. Un fichier marqué pour une autre version n'est jamais valable ici : deux
/// versions installées dans le même dossier ne partagent pas une licence gratuite (« une licence = une seule version »).
/// Un fichier sans marque (remis à la main, ou posé par une ancienne version) vaut pour l'exécutable à côté duquel il se
/// trouve.</summary>
public sealed class EditionBypassFile : IBypassFile
{
    public const string FileName = "licence.ini";
    private const string EditionPrefix = "edition=";

    private readonly string _edition;

    public EditionBypassFile(AppEdition edition, string? directory = null)
    {
        _edition = edition.Id;
        Path = System.IO.Path.Combine(directory ?? AppContext.BaseDirectory, FileName);
    }

    public string Path { get; }

    /// <summary>Version pour laquelle le fichier a été posé, ou null s'il n'est pas marqué.</summary>
    private string? FileEdition()
    {
        try
        {
            foreach (var line in File.ReadAllLines(Path))
            {
                var l = line.Trim();
                if (l.StartsWith(EditionPrefix, StringComparison.OrdinalIgnoreCase))
                    return l[EditionPrefix.Length..].Trim().ToLowerInvariant();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private bool BelongsToThisEdition() =>
        FileEdition() is not { } e || string.Equals(e, _edition, StringComparison.OrdinalIgnoreCase);

    public bool Exists => File.Exists(Path) && BelongsToThisEdition();

    public string? ReadCustomerName()
    {
        if (!Exists) return null;
        try
        {
            var line = File.ReadAllLines(Path).Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith(EditionPrefix, StringComparison.OrdinalIgnoreCase));
            if (line is null) return null;
            return line.Length > 200 ? line[..200] : line;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool TryCreate(string? content)
    {
        try
        {
            var name = (content ?? "").Trim();
            File.WriteAllText(Path, (name.Length > 0 ? name + Environment.NewLine : "") + EditionPrefix + _edition + Environment.NewLine);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public bool TryDelete()
    {
        // Le fichier d'une autre version n'est pas le nôtre : on n'y touche pas (pour nous, il n'existe pas).
        if (File.Exists(Path) && !BelongsToThisEdition()) return true;
        try { File.Delete(Path); return !File.Exists(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}