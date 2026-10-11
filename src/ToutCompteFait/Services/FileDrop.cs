using System.IO;
using System.Windows;

namespace ToutCompteFait;

/// <summary>Glisser-déposer de fichiers depuis l'Explorateur : accepte seulement des fichiers, jamais des dossiers.</summary>
public static class FileDrop
{
    public static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    /// <summary>Fichiers déposés (les dossiers sont ignorés).</summary>
    public static string[] Files(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] paths ? paths.Where(File.Exists).ToArray() : Array.Empty<string>();

    /// <summary>À appeler dans DragOver : montre le curseur « copie » si des fichiers sont glissés, sinon l'interdit.</summary>
    public static void Accept(DragEventArgs e, bool accepted = true)
    {
        e.Effects = accepted && HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
}
