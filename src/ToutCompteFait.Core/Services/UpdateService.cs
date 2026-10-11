using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ToutCompteFait.Core.Services;

/// <summary>Informations sur une nouvelle version disponible, extraites de la release GitHub.</summary>
/// <param name="DbSchema">Version du schéma de base lue dans les notes de la release (marqueur
/// <c>&lt;!-- db-schema: N --&gt;</c> ajouté par tools\Release.ps1), null si absente.</param>
/// <param name="IsChannelSwitch">Retour sur le canal Main depuis une build Dev : la version proposée peut être
/// plus ancienne que celle installée.</param>
public record UpdateInfo(string Version, string ReleaseUrl, string ReleaseNotes, string AssetUrl, string AssetName,
    int? DbSchema = null, bool IsChannelSwitch = false);

/// <summary>Vérifie et applique les mises à jour en s'appuyant sur les releases GitHub du dépôt
/// (exécutable portable publié en asset .zip sur chaque release) : télécharge l'archive, la décompresse,
/// puis remplace le contenu du dossier d'installation par celui de l'archive avant de relancer
/// l'application. Le remplacement se fait via un processus PowerShell entièrement détaché (voir
/// <see cref="DownloadAndApplyAsync"/>), pas un script .bat sur disque (une première version basée sur un
/// script batch s'était révélée peu fiable : le script pouvait supprimer son propre dossier pendant qu'il
/// s'exécutait encore, s'interrompant avant de relancer l'application).</summary>
public static class UpdateChannels
{
    public const string Main = "Main";
    public const string Dev = "Dev";
    public static readonly string[] All = [Main, Dev];
}

public class UpdateService
{
    private const string RepoOwner = "Rimfire03";
    private const string RepoName = "ToutCompteFait";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ToutCompteFait-UpdateChecker");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Interroge la dernière release GitHub et retourne ses infos si sa version est plus
    /// récente que <paramref name="currentVersion"/> (format "MAJOR.MINOR.PATCH"). Retourne null
    /// UNIQUEMENT quand la vérification a réussi et qu'il n'y a pas de mise à jour disponible.
    /// Lève une exception en cas d'échec réel (réseau, API indisponible, réponse inattendue) : avaler
    /// ces erreurs en silence (comme avant) rendait impossible de distinguer "à jour" de "la
    /// vérification a échoué", ce qui empêchait de diagnostiquer un vrai problème (ex. limite de débit
    /// de l'API GitHub, pare-feu). L'appelant du démarrage silencieux (voir App.Updates.cs) est
    /// responsable de ne pas déranger l'utilisateur pour ces erreurs ; le bouton "Vérifier maintenant"
    /// (Paramètres) les affiche telles quelles.</summary>
    /// <summary>Explication quand la dernière vérification n'a rien proposé alors qu'un retour sur Main était
    /// demandé (ex. base incompatible), sinon null.</summary>
    public string? LastCheckNote { get; private set; }

    /// <param name="installedIsDev">La build installée est une build du canal Dev.</param>
    /// <param name="currentDbSchema">Version de schéma de base gérée par la build installée : sert à ne proposer
    /// le retour sur Main que si la dernière version Main partage ce même schéma.</param>
    public async Task<UpdateInfo?> CheckForUpdateAsync(string currentVersion, string channel = UpdateChannels.Main,
        bool installedIsDev = false, int? currentDbSchema = null)
    {
        LastCheckNote = null;
        // Main : dernière release stable (branche main). Dev : release la plus récente de la branche dev, dont le
        // tag se termine par "-dev" (publiée en pré-version, donc jamais vue par /releases/latest).
        var dev = string.Equals(channel, UpdateChannels.Dev, StringComparison.OrdinalIgnoreCase);
        var url = dev
            ? $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=30"
            : $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"L'API GitHub a répondu {(int)response.StatusCode} {response.ReasonPhrase} : {body}");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;
        if (dev)
        {
            // Liste triée de la plus récente à la plus ancienne : première release non brouillon dont le tag finit par -dev.
            var first = root.EnumerateArray().FirstOrDefault(r =>
                !(r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True)
                && r.TryGetProperty("tag_name", out var tn)
                && (tn.GetString() ?? "").EndsWith("-dev", StringComparison.OrdinalIgnoreCase));
            if (first.ValueKind != JsonValueKind.Object) return null;
            root = first;
        }

        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        var versionText = tag.TrimStart('v', 'V');   // ex. "1.12.0" ou "1.12.0-dev"
        var releaseNotes = root.TryGetProperty("body", out var notesEl) ? notesEl.GetString() ?? "" : "";
        var marker = System.Text.RegularExpressions.Regex.Match(releaseNotes, @"<!--\s*db-schema:\s*(\d+)\s*-->");
        int? dbSchema = marker.Success ? int.Parse(marker.Groups[1].Value) : null;

        var channelSwitch = false;
        if (!IsNewer(versionText, currentVersion))
        {
            // Retour sur Main depuis une build Dev : la version Main est proposée même si son numéro est plus ancien,
            // mais seulement si elle gère le même schéma de base (sinon l'ancienne version ne saurait pas ouvrir la base).
            if (dev || !installedIsDev) return null;
            if (dbSchema is null || currentDbSchema is null || dbSchema != currentDbSchema)
            {
                LastCheckNote = $"La dernière version Main ({versionText}) n'est pas proposée : " +
                    (dbSchema is null
                        ? "la version de sa base de données n'est pas connue."
                        : $"sa base de données (version {dbSchema}) est différente de la vôtre (version {currentDbSchema}).") +
                    " Une version Main plus récente sera proposée dès qu'elle sera compatible.";
                return null;
            }
            channelSwitch = true;
        }

        // Version installée (MSI) : on récupère le .msi ; version portable : le .zip.
        var ext = InstallMode.IsInstalled ? ".msi" : ".zip";
        string? assetUrl = null;
        string? assetName = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
                assetUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                assetName = name;
                break;
            }
        }
        if (assetUrl == null || assetName == null)
            throw new InvalidOperationException($"La release {tag} sur GitHub n'a pas de fichier {ext} en pièce jointe.");

        var htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? "" : "";

        return new UpdateInfo(versionText, htmlUrl, releaseNotes, assetUrl, assetName, dbSchema, channelSwitch);
    }

    /// <summary>Télécharge l'archive .zip de la nouvelle version, la décompresse intégralement, puis lance
    /// un processus PowerShell détaché qui attend la fermeture du processus courant, copie tout le
    /// contenu décompressé (l'exécutable, mais aussi tout autre fichier requis à côté - ex. une bibliothèque à côté) par-dessus le dossier d'installation, relance l'application, puis nettoie le dossier
    /// temporaire. Seuls les fichiers présents dans l'archive sont écrasés/ajoutés : les données propres
    /// à l'utilisateur qui n'y figurent jamais (Donnees\compta.db, réglages, sauvegardes...) ne sont
    /// jamais touchées - l'appelant est malgré tout invité à sauvegarder la base avant d'appeler cette
    /// méthode (voir DbContextFactory.CreateBackup), par précaution.
    /// <para>La commande PowerShell est transmise encodée en base64 (-EncodedCommand), jamais comme
    /// ligne de commande classique : les chemins concernés (dossier d'installation, profil utilisateur...)
    /// peuvent contenir des accents ou espaces, et cet encodage évite tout problème de parsing/échappement
    /// côté interpréteur de commandes - PowerShell décode directement les caractères Unicode.</para>
    /// <para>L'appelant doit fermer l'application juste après (le remplacement de l'exécutable ne peut se
    /// faire tant qu'il est verrouillé).</para></summary>
    public async Task DownloadAndApplyAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ToutCompteFait-Update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, info.AssetName);

        using (var response = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1L;
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var destination = File.Create(zipPath);
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read));
                readTotal += read;
                if (total > 0) progress?.Report(readTotal * 100.0 / total);
            }
        }

        var currentExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Impossible de déterminer l'exécutable en cours d'exécution.");

        // Version installée : le paquet MSI se charge du remplacement (élévation UAC demandée par msiexec) ;
        // les données (Donnees\compta.db, réglages) et la licence (registre) ne sont pas touchées.
        if (zipPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            RunDetachedMsi(Environment.ProcessId, zipPath, currentExePath, tempDir);
            return;
        }

        var extractDir = Path.Combine(tempDir, "extracted");
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        var exeName = Path.GetFileName(currentExePath);
        var installDir = Path.GetDirectoryName(currentExePath)
            ?? throw new InvalidOperationException("Impossible de déterminer le dossier d'installation.");
        var currentPid = Environment.ProcessId;

        // L'archive publiée place toujours l'exécutable directement à sa racine (voir "Publier
        // l'exécutable portable" dans le README) : vérifie que la structure est bien celle attendue
        // avant de lancer le remplacement, plutôt que d'échouer silencieusement plus tard. Le nom
        // attendu est déduit de l'exécutable actuellement lancé plutôt que codé en dur, pour ne pas
        // se désynchroniser si l'exécutable est renommé un jour.
        if (!File.Exists(Path.Combine(extractDir, exeName)))
            throw new InvalidOperationException($"{exeName} introuvable à la racine de l'archive téléchargée.");

        RunDetachedReplace(currentPid, extractDir, installDir, currentExePath, tempDir);
    }

    /// <summary>Échappe une chaîne pour une chaîne PowerShell entre apostrophes (seul caractère spécial à
    /// doubler dans ce contexte).</summary>
    private static string EscapeSingleQuoted(string value) => value.Replace("'", "''");

    /// <summary>Même principe que <see cref="RunDetachedReplace"/> : processus PowerShell détaché qui attend la
    /// fermeture de l'application, lance <c>msiexec /i</c> élevé (mise à jour majeure du paquet), puis relance
    /// l'application et nettoie.</summary>
    private static void RunDetachedMsi(int pid, string msiPath, string targetExePath, string tempDir)
    {
        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            while (Get-Process -Id {{pid}} -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }
            Start-Process -FilePath 'msiexec.exe' -ArgumentList '/i "{{EscapeSingleQuoted(msiPath)}}" /passive /norestart' -Verb RunAs -Wait
            Start-Process -FilePath '{{EscapeSingleQuoted(targetExePath)}}'
            Start-Sleep -Seconds 2
            Remove-Item -Path '{{EscapeSingleQuoted(tempDir)}}' -Recurse -Force
            """;

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static void RunDetachedReplace(int pid, string extractDir, string installDir, string targetExePath, string tempDir)
    {
        var extractDirEsc = EscapeSingleQuoted(extractDir);
        var installDirEsc = EscapeSingleQuoted(installDir);
        var targetExePathEsc = EscapeSingleQuoted(targetExePath);
        var tempDirEsc = EscapeSingleQuoted(tempDir);

        // Aucun fichier de script n'est écrit sur disque (contrairement à une première version basée sur
        // un .bat) : la commande vit entièrement dans l'argument -EncodedCommand du processus PowerShell
        // détaché, donc rien ne peut être "supprimé sous ses propres pieds" pendant son exécution.
        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            while (Get-Process -Id {{pid}} -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }

            $ErrorActionPreference = 'Stop'
            $attempts = 0
            while ($true) {
                try {
                    Copy-Item -Path (Join-Path '{{extractDirEsc}}' '*') -Destination '{{installDirEsc}}' -Recurse -Force
                    break
                } catch {
                    $attempts++
                    if ($attempts -ge 15) { throw }
                    Start-Sleep -Seconds 1
                }
            }

            Start-Process -FilePath '{{targetExePathEsc}}'

            $ErrorActionPreference = 'SilentlyContinue'
            Start-Sleep -Seconds 2
            Remove-Item -Path '{{tempDirEsc}}' -Recurse -Force
            """;

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static bool IsNewer(string candidate, string current)
    {
        var (cMajor, cMinor, cPatch) = ParseVersion(candidate);
        var (curMajor, curMinor, curPatch) = ParseVersion(current);

        if (cMajor != curMajor) return cMajor > curMajor;
        if (cMinor != curMinor) return cMinor > curMinor;
        return cPatch > curPatch;
    }

    private static (int Major, int Minor, int Patch) ParseVersion(string version)
    {
        var dash = version.IndexOf('-');   // suffixe de canal ("-dev") ignoré pour la comparaison
        var parts = (dash >= 0 ? version[..dash] : version).Split('.', StringSplitOptions.RemoveEmptyEntries);
        int Part(int i) => i < parts.Length && int.TryParse(parts[i], out var n) ? n : 0;
        return (Part(0), Part(1), Part(2));
    }
}
