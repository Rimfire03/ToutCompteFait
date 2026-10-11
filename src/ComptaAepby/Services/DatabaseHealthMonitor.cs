using ComptaAepby.Core;
using Microsoft.EntityFrameworkCore;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ComptaAepby.Core.Services;

namespace ComptaAepby.Services;

/// <summary>Contrôle régulier de l'accès à la base : alimente la puce du pied de page (même principe que
/// FiltresApp). Un « SELECT 1 » sur une connexion neuve en lecture seule, toutes les 15 secondes, en
/// arrière-plan, une vérification à la fois.</summary>
public partial class DatabaseHealthMonitor : ObservableObject
{
    private const int IntervalSeconds = 15;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(IntervalSeconds) };
    private int _running;
    private DateTime? _downSince;

    [ObservableProperty] private bool _isKnown;
    [ObservableProperty] private bool _isOk;
    [ObservableProperty] private bool _isDown;
    /// <summary>Base accessible mais ce poste est en lecture seule : puce orange.</summary>
    [ObservableProperty] private bool _isReadOnlyMode;
    [ObservableProperty] private string _tooltip = "Vérification de la base en cours...";

    public DatabaseHealthMonitor()
    {
        _timer.Tick += async (_, _) => await CheckNowAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = CheckNowAsync();
    }

    public void Stop() => _timer.Stop();

    public async Task CheckNowAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var target = App.Target;
            var error = await Task.Run(() => Probe(target));
            Apply(error, target.Describe());
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private void Apply(string? error, string path)
    {
        var now = DateTime.Now;
        IsKnown = true;
        if (error is null)
        {
            _downSince = null;
            IsOk = true;
            IsDown = false;
            IsReadOnlyMode = App.IsReadOnly;
            Tooltip = $"Base accessible - {path}" +
                      (App.IsReadOnly ? $"\nPoste en LECTURE SEULE : aucune modification n'est enregistrée.{(App.WriteLockOwner is null ? "" : "\nÉcriture en cours : " + App.WriteLockOwner)}" : "\nAccès en écriture sur ce poste.") +
                      $"\nDernier contrôle : {now:HH:mm:ss}";
        }
        else
        {
            _downSince ??= now;
            IsOk = false;
            IsDown = true;
            Tooltip = $"Connexion perdue depuis {_downSince:HH:mm:ss} - {path}\n{error}\nNouvel essai automatique toutes les {IntervalSeconds} s.";
        }
    }

    /// <summary>Retourne null si la base répond, sinon le motif de l'échec (SELECT 1 sur une connexion neuve en lecture
    /// seule, délai court).</summary>
    private static string? Probe(DbTarget target)
    {
        try
        {
            if (!target.IsServer && !System.IO.File.Exists(target.FilePath)) return "Fichier de base introuvable.";
            var probe = target;
            if (target.IsServer)
            {
                var server = target.Server!.Clone();
                server.ConnectTimeoutSeconds = 5;
                probe = DbTarget.ForServer(server);
            }
            using var ctx = new DbContextFactory(probe, readOnly: true).Create();
            var conn = ctx.Database.GetDbConnection();
            conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                cmd.CommandTimeout = 5;
                cmd.ExecuteScalar();
            }
            finally
            {
                conn.Close();
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.InnermostMessage();
        }
    }
}

