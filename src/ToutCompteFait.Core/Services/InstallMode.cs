using Microsoft.Win32;

namespace ToutCompteFait.Core.Services;

/// <summary>Distingue la version installée (paquet MSI, par machine) de la version portable (zip).
/// L'installateur écrit <c>InstallDir</c> dans <c>HKLM\SOFTWARE\TomLine\ToutCompteFait</c> ; l'application est
/// « installée » seulement si elle tourne depuis ce dossier : une copie portable posée ailleurs sur un poste où
/// l'installateur est passé reste portable.</summary>
public static class InstallMode
{
    public const string RegistryKeyPath = @"SOFTWARE\TomLine\ToutCompteFait";

    private static bool? _installed;

    public static bool IsInstalled => _installed ??= Detect();

#pragma warning disable CA1416 // Registre : l'application ne tourne que sous Windows.
    private static bool Detect()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(RegistryKeyPath, false);
            if (key?.GetValue("InstallDir") is not string dir || string.IsNullOrWhiteSpace(dir)) return false;
            return string.Equals(Norm(dir), Norm(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
#pragma warning restore CA1416

    private static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
}