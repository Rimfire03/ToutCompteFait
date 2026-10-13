using System.Text.Json;

namespace ToutCompteFait.Core;

/// <summary>Une version du devlog : ce qui a changé, écrit en français pour l'utilisateur final.
/// <c>Show</c> : afficher ce texte au lancement après la mise à jour (décision prise à chaque release).</summary>
public sealed record DevLogEntry(string Version, string? Date, bool Show, string Title, IReadOnlyList<string> Items);

/// <summary>Devlog affiché au premier lancement après une mise à jour (fichier devlog.json embarqué dans l'application).</summary>
public static class DevLog
{
    private sealed class FileModel
    {
        public List<EntryModel> Entries { get; set; } = new();
    }

    private sealed class EntryModel
    {
        public string Version { get; set; } = "";
        public string? Date { get; set; }
        public bool Show { get; set; }
        public string? Title { get; set; }
        public List<string>? Items { get; set; }
    }

    public static IReadOnlyList<DevLogEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<DevLogEntry>();
        try
        {
            var model = JsonSerializer.Deserialize<FileModel>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return (model?.Entries ?? new())
                .Where(e => Version.TryParse(e.Version, out _))
                .Select(e => new DevLogEntry(e.Version, e.Date, e.Show, e.Title ?? "Nouveautés", e.Items ?? new List<string>()))
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<DevLogEntry>();
        }
    }

    /// <summary>Entrées à présenter : à afficher (<c>Show</c>), de la version vue la dernière fois (exclue) à la version
    /// courante (incluse), la plus récente d'abord. Sans version déjà vue, seule la version courante est présentée.</summary>
    public static IReadOnlyList<DevLogEntry> Pending(IReadOnlyList<DevLogEntry> all, string current, string? lastSeen)
    {
        if (!Version.TryParse(current, out var cur)) return Array.Empty<DevLogEntry>();
        Version.TryParse(lastSeen, out var seen);
        return all
            .Where(e => e.Show && Version.TryParse(e.Version, out var v) && v <= cur &&
                        (seen is null ? v == cur : v > seen))
            .OrderByDescending(e => Version.Parse(e.Version))
            .ToList();
    }

    /// <summary>Entrée d'une version précise (pour l'afficher à la demande depuis l'aide).</summary>
    public static DevLogEntry? For(IReadOnlyList<DevLogEntry> all, string version) =>
        all.FirstOrDefault(e => Version.TryParse(e.Version, out var v) && Version.TryParse(version, out var w) && v == w);
}