using System.Windows.Threading;
using ToutCompteFait.Core.Services;

namespace ToutCompteFait.Services;

/// <summary>Sauvegarde automatique planifiée de la base (fichier SQLite ou serveur) dans Sauvegardes.
/// Vérifie chaque minute si une échéance est passée ; au démarrage, rattrape une échéance manquée pendant que le
/// logiciel était fermé. Pas de sauvegarde sur un poste en lecture seule.</summary>
public sealed class AutoBackupScheduler
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _running;
    private DateTime _lastAttempt = DateTime.MinValue;

    public string? LastError { get; private set; }

    /// <summary>Déclenché (thread interface) après chaque sauvegarde automatique réussie.</summary>
    public event Action? BackupCreated;

    public AutoBackupScheduler()
    {
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public void Start()
    {
        BackupService.MaxToKeep = App.Settings.MaxBackupsToKeep;
        BackupService.ProtectedFiles = new HashSet<string>(App.Settings.ProtectedBackups, StringComparer.OrdinalIgnoreCase);
        _ = DbContextFactory.BackupDirectory; // crée le dossier Save DB même si aucune sauvegarde n'existe encore
        _timer.Start();
        _ = CheckAsync();
    }

    /// <summary>Sauvegarde immédiate (bouton « Sauvegarder maintenant »).</summary>
    public async Task<string> BackupNowAsync(string reason)
    {
        _running = true;
        try
        {
            return await Task.Run(() => App.DbFactory.CreateBackup(reason));
        }
        finally
        {
            _running = false;
        }
    }

    private async Task CheckAsync()
    {
        var s = App.Settings;
        if (_running || App.IsReadOnly || !BackupSchedule.IsDue(s, DateTime.Now)) return;
        if (DateTime.Now - _lastAttempt < RetryDelay) return;

        _lastAttempt = DateTime.Now;
        _running = true;
        try
        {
            await Task.Run(() => App.DbFactory.CreateBackup("auto"));
            s.AutoBackupLastRun = DateTime.Now;
            s.Save();
            LastError = null;
            BackupCreated?.Invoke();
        }
        catch (Exception ex)
        {
            LastError = ex.Message; // réessayé dans 15 minutes
        }
        finally
        {
            _running = false;
        }
    }
}

