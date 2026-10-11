using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ToutCompteFait.Core.Licensing;

/// <summary>Identité stable de la machine.</summary>
public interface IDeviceIdentity
{
    /// <summary>SHA-256 (hex minuscule) de l'identifiant matériel de la machine : jamais aléatoire ni régénéré.</summary>
    string DeviceId { get; }
    string DeviceName { get; }
}

/// <summary>Stockage de la licence sans fichier : registre + DPAPI (Windows) ou Keychain (macOS).</summary>
public interface ILicenseStore
{
    StoredLicense? Load();
    void Save(StoredLicense license);
    void Clear();
}

public static class LicensePlatform
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static IDeviceIdentity CreateIdentity() =>
        OperatingSystem.IsWindows() ? new WindowsDeviceIdentity()
        : OperatingSystem.IsMacOS() ? new MacDeviceIdentity()
        : throw new PlatformNotSupportedException("Licences : Windows et macOS uniquement.");

    public static ILicenseStore CreateStore() =>
        OperatingSystem.IsWindows() ? new WindowsLicenseStore()
        : OperatingSystem.IsMacOS() ? new MacKeychainLicenseStore()
        : throw new PlatformNotSupportedException("Licences : Windows et macOS uniquement.");

    internal static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal static string Serialize(StoredLicense l) => JsonSerializer.Serialize(l, Json);

    internal static StoredLicense? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<StoredLicense>(json, Json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Windows : SHA-256 de <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c> (vue 64 bits).</summary>
public sealed class WindowsDeviceIdentity : IDeviceIdentity
{
    private string? _id;

    public string DeviceId => _id ??= Read();
    public string DeviceName => Environment.MachineName;

    private static string Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var guid = key?.GetValue("MachineGuid") as string;
        if (string.IsNullOrWhiteSpace(guid))
            throw new InvalidOperationException("Identifiant machine introuvable (MachineGuid).");
        return LicensePlatform.Sha256Hex(guid.Trim());
    }
}

/// <summary>Windows : licence dans le registre (HKCU), chiffrée par DPAPI (portée utilisateur), aucun fichier.</summary>
public sealed class WindowsLicenseStore : ILicenseStore
{
    private const string DefaultKeyPath = @"Software\TomLine prod&co\ToutCompteFait";
    private const string ValueName = "License";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ToutCompteFait.license.v1");

    private readonly string KeyPath;

    /// <param name="keyPath">Clé de registre sous HKCU (tests uniquement) ; par défaut, celle de l'application.</param>
    public WindowsLicenseStore(string? keyPath = null) => KeyPath = keyPath ?? DefaultKeyPath;

    public StoredLicense? Load()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue(ValueName) is not byte[] blob) return null;
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
            var l = LicensePlatform.Deserialize(json);
            return l is { Key.Length: > 0 } ? l : null;
        }
        catch (Exception ex) when (ex is CryptographicException or System.Security.SecurityException or UnauthorizedAccessException or FormatException)
        {
            return null;   // illisible (autre session Windows, registre altéré) : comme une licence absente
        }
    }

    public void Save(StoredLicense license)
    {
        if (!OperatingSystem.IsWindows()) return;
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(LicensePlatform.Serialize(license)), Entropy, DataProtectionScope.CurrentUser);
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(ValueName, blob, Microsoft.Win32.RegistryValueKind.Binary);
    }

    public void Clear()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>macOS : SHA-256 de <c>IOPlatformUUID</c> (commande <c>ioreg</c>).</summary>
public sealed class MacDeviceIdentity : IDeviceIdentity
{
    private string? _id;

    public string DeviceId => _id ??= Read();
    public string DeviceName => Environment.MachineName;

    private static string Read()
    {
        var output = MacShell.Run("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice", out _);
        foreach (var line in output.Split('\n'))
        {
            var i = line.IndexOf("IOPlatformUUID", StringComparison.Ordinal);
            if (i < 0) continue;
            var parts = line[i..].Split('"');
            if (parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]))
                return LicensePlatform.Sha256Hex(parts[3].Trim());
        }
        throw new InvalidOperationException("Identifiant machine introuvable (IOPlatformUUID).");
    }
}

/// <summary>macOS : licence dans le trousseau (Keychain) de l'utilisateur, via l'outil système <c>security</c>.</summary>
public sealed class MacKeychainLicenseStore : ILicenseStore
{
    private const string Service = "ToutCompteFait.License";

    public StoredLicense? Load()
    {
        var output = MacShell.Run("/usr/bin/security", $"find-generic-password -s {Service} -a {Account} -w", out var code);
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            var l = LicensePlatform.Deserialize(Encoding.UTF8.GetString(Convert.FromBase64String(output.Trim())));
            return l is { Key.Length: > 0 } ? l : null;
        }
        catch (FormatException) { return null; }
    }

    public void Save(StoredLicense license)
    {
        var secret = Convert.ToBase64String(Encoding.UTF8.GetBytes(LicensePlatform.Serialize(license)));
        MacShell.Run("/usr/bin/security", $"add-generic-password -U -s {Service} -a {Account} -w {secret}", out var code);
        if (code != 0) throw new InvalidOperationException("Enregistrement de la licence dans le trousseau impossible.");
    }

    public void Clear() => MacShell.Run("/usr/bin/security", $"delete-generic-password -s {Service} -a {Account}", out _);

    private static string Account => Environment.UserName;
}

internal static class MacShell
{
    public static string Run(string file, string args, out int exitCode)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Commande système indisponible.");
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        exitCode = p.ExitCode;
        return output;
    }
}
