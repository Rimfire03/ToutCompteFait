using TomLine.Licensing;

namespace ToutCompteFait.Core.Licensing;

/// <summary>Fichier de licence gratuite propre à une version : <c>licence.ini</c> pour la version historique (Asso, les
/// fichiers déjà posés continuent de fonctionner) et <c>licence-&lt;version&gt;.ini</c> pour les autres
/// (<c>licence-autoentreprise.ini</c>). Deux versions installées dans le même dossier ne partagent ainsi jamais une
/// licence gratuite : « une licence = une seule version ».</summary>
public sealed class EditionBypassFile : IBypassFile
{
    private readonly BypassFile _inner;

    public EditionBypassFile(AppEdition edition, string? directory = null)
    {
        FileName = FileNameFor(edition);
        Path = System.IO.Path.Combine(directory ?? AppContext.BaseDirectory, FileName);
        _inner = new BypassFile(directory ?? AppContext.BaseDirectory);
    }

    public static string FileNameFor(AppEdition edition) =>
        edition.Id == AppEdition.Legacy ? BypassFile.FileName : $"licence-{edition.Id}.ini";

    public string FileName { get; }
    public string Path { get; }

    public bool Exists => File.Exists(Path);

    public string? ReadCustomerName()
    {
        try
        {
            var line = File.ReadAllLines(Path).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line is null) return null;
            return line.Length > 200 ? line[..200] : line;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool TryCreate(string? content)
    {
        try
        {
            File.WriteAllText(Path, (content ?? "").Trim() + (string.IsNullOrWhiteSpace(content) ? "" : Environment.NewLine));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public bool TryDelete()
    {
        try { File.Delete(Path); return !File.Exists(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}