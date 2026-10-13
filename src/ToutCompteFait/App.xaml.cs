using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using ToutCompteFait.Core;
using ToutCompteFait.Core.Security;
using ToutCompteFait.Core.Services;
using ToutCompteFait.Dialogs;
using ToutCompteFait.Services;

namespace ToutCompteFait;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>Base ouverte : fichier SQLite ou serveur (mêmes réglages que FiltresApp).</summary>
    public static DbTarget Target { get; private set; } = DbTarget.ForFile("");

    public static DbContextFactory DbFactory { get; private set; } = null!;

    /// <summary>Service comptable de la session (null tant que personne n'est connecté).</summary>
    public static Compta Compta { get; private set; } = null!;

    public static Session Session { get; private set; } = Session.System;
    public static AppUser? User { get; private set; }

    public static DatabaseHealthMonitor DatabaseHealth { get; } = new();
    public static AutoBackupScheduler AutoBackup { get; } = new();

    /// <summary>Description de la base (chemin du fichier ou serveur), pour l'affichage.</summary>
    public static string DatabasePath => Target.Describe();

    /// <summary>La base est ouverte en lecture seule sur ce poste (un autre poste écrit, ou rôle lecteur).</summary>
    public static bool IsReadOnly { get; private set; }

    /// <summary>« utilisateur » du rédacteur quand ce poste est en lecture seule faute de verrou.</summary>
    public static string? WriteLockOwner { get; private set; }

    /// <summary>Fichier de base imposé en ligne de commande (--db), non mémorisé dans les réglages.</summary>
    public static string? CliDatabase { get; private set; }

    /// <summary>Diagnostic (--selftest, avec --shots) : saisit deux écritures de test. À n'utiliser que sur une base de démonstration.</summary>
    public static bool RunSelfTest { get; private set; }

    /// <summary>Mode captures d'écran (--shots) : aucune fenêtre modale d'information ne doit s'ouvrir.</summary>
    public static bool ShotsMode { get; private set; }

    private static WriteLock? _fileLock;
    private static ServerWriteLock? _serverLock;
    private static System.Windows.Threading.DispatcherTimer? _serverLockTimer;

    public static string DisplayVersion { get; } =
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0)).ToString(3);

    public static int DatabaseVersion => DbFactory.GetDatabaseVersion();

    public static string DatabaseModeLabel
    {
        get
        {
            if (Target.IsServer) return "Serveur " + DatabaseProviderInfo.DisplayName(Target.Provider);
            try
            {
                var root = Path.GetPathRoot(Target.FilePath);
                if (!string.IsNullOrEmpty(root) && (root.StartsWith(@"\\") || new DriveInfo(root).DriveType == DriveType.Network))
                    return "Réseau (SQLite)";
            }
            catch { /* tant pis : libellé local */ }
            return "Locale (SQLite)";
        }
    }

    /// <summary>Nom de la base ouverte (base du serveur, ou fichier SQLite sans son extension).</summary>
    public static string DatabaseName
    {
        get
        {
            try
            {
                if (Target.IsServer) return Target.Server?.Database ?? "";
                return Path.GetFileNameWithoutExtension(Target.FilePath ?? "");
            }
            catch { return ""; }
        }
    }

    /// <summary>Statut Développeur : bascule vers une configuration enregistrée (autre base de données) puis redémarre le
    /// logiciel sur l'écran de connexion. Retourne un message d'erreur, ou null.</summary>
    public static string? SwitchConfiguration(ConfigProfile profile)
    {
        if (!Session.Can(Permission.ManageConfigurations)) return "Réservé au statut Développeur.";
        ConfigProfiles.Apply(profile, Settings);
        // Le mot de passe mémorisé appartient à la base précédente : on le oublie, la connexion est redemandée.
        Settings.SetRememberedPassword(null);
        Settings.Save();
        Restart();
        return null;
    }

    /// <summary>Nom de l'association ou de l'entreprise : réglage de ce poste s'il existe, sinon celui de la fiche
    /// enregistrée dans la base (récupérée par SIREN / SIRET, partagée entre les postes).</summary>
    public static string? OrganizationName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Settings.OrganizationName)) return Settings.OrganizationName.Trim();
            try { return Compta.GetBusinessProfile().Name; } catch { return null; }
        }
    }

    /// <summary>SIREN : réglage de ce poste s'il existe, sinon celui de la fiche enregistrée dans la base.</summary>
    public static string? Siren
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Settings.Siren)) return Settings.Siren.Trim();
            try { return Compta.GetBusinessProfile().Siren; } catch { return null; }
        }
    }

    /// <summary>Nom de la base si elle porte le mot « test » (base d'essai), sinon null : l'écran affiche alors un
    /// bandeau d'avertissement rouge.</summary>
    public static string? TestDatabaseName =>
        DatabaseName is { Length: > 0 } n && n.Contains("test", StringComparison.OrdinalIgnoreCase) ? n : null;

    public static string ReadOnlyMessage
    {
        get
        {
            if (Session.Role == Role.Reader && WriteLockOwner is null)
                return "Rôle « Lecteur » : consultation seule. Les données se mettent à jour automatiquement.";
            return "Lecture seule : un autre poste détient l'accès en écriture" +
                   (WriteLockOwner is null ? "." : $" ({WriteLockOwner}).") +
                   " Vous pouvez consulter toutes les données ; elles se mettent à jour automatiquement.";
        }
    }

    // ------------------------------------------------------------------ droits

    /// <summary>Le rôle de l'utilisateur donne ce droit (indépendamment de l'état de la base).</summary>
    public static bool HasRole(Permission p) => Session.Can(p);

    /// <summary>Le droit existe ET la base est inscriptible sur ce poste : l'action peut être proposée.</summary>
    public static bool Can(Permission p) => !IsReadOnly && Session.Can(p);

    // ------------------------------------------------------------------ démarrage

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            Ui.Error(args.Exception is ComptaException ? args.Exception.Message
                : "Une erreur inattendue est survenue :\n\n" + args.Exception.Message);
        };

        string? shotsDir = null, cliYear = null, cliUser = null, cliPassword = null;
        RunSelfTest = e.Args.Contains("--selftest");
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--db") CliDatabase = e.Args[i + 1];
            if (e.Args[i] == "--shots") { shotsDir = e.Args[i + 1]; ShotsMode = true; }
            if (e.Args[i] == "--year") cliYear = e.Args[i + 1];
            if (e.Args[i] == "--user") cliUser = e.Args[i + 1];
            if (e.Args[i] == "--password") cliPassword = e.Args[i + 1];
        }

        var frIdx = Array.IndexOf(e.Args, "--firstrunshots");
        if (frIdx >= 0 && frIdx + 1 < e.Args.Length)
        {
            var frDir = e.Args[frIdx + 1];
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await FirstRunShotsAsync(frDir);
                Shutdown(0);
            });
            return;
        }

        var uiIdx = Array.IndexOf(e.Args, "--ocrui");
        if (uiIdx >= 0 && uiIdx + 2 < e.Args.Length)
        {
            var dirArg = e.Args[uiIdx + 1];
            var pngArg = e.Args[uiIdx + 2];
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Services.OcrSelfTest.UiShotAsync(dirArg, pngArg);
                Shutdown(0);
            });
            return;
        }

        var rsIdx = Array.IndexOf(e.Args, "--readstatement");
        if (rsIdx >= 0 && rsIdx + 2 < e.Args.Length)
        {
            var file = e.Args[rsIdx + 1];
            var outFile = e.Args[rsIdx + 2];
            var prof = rsIdx + 3 < e.Args.Length ? e.Args[rsIdx + 3] : null;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Services.OcrSelfTest.DumpAsync(file, outFile, prof);
                Shutdown(0);
            });
            return;
        }

        // Diagnostic sans base ni connexion : lecture d'un relevé factice (PDF numérique, puis image + OCR Windows).
        var ocrIdx = Array.IndexOf(e.Args, "--ocrtest");
        if (ocrIdx >= 0 && ocrIdx + 1 < e.Args.Length)
        {
            var dir = e.Args[ocrIdx + 1];
            _ = Dispatcher.InvokeAsync(async () =>
            {
                var failed = await Services.OcrSelfTest.RunAsync(dir);
                Shutdown(failed == 0 ? 0 : 1);
            });
            return;
        }

        try
        {
            Settings = AppSettings.Load();
            if (CliDatabase is not null)
            {
                Settings.UseDatabaseServer = false;
                Settings.DatabasePath = CliDatabase;
            }
            if (cliYear is not null) Settings.LastYear = cliYear;
            // CLUF puis licence, avant tout accès aux données. Les modes de diagnostic (captures d'écran, auto-test) ne
            // passent pas par là : ils ne font que photographier l'interface d'une base de démonstration.
            if (shotsDir is null && !RunSelfTest && !LicenseGate.Run()) { Shutdown(); return; }
            if (!StartSession(cliUser, cliPassword)) { Shutdown(); return; }
        }
        catch (Exception ex)
        {
            while (ex.InnerException is not null && ex is not ComptaException) ex = ex.InnerException;
            MessageBox.Show("Impossible d'ouvrir la base de données :\n\n" + ex.Message,
                Editions.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        DatabaseHealth.Start();
        AutoBackup.Start();
        if (Settings.AutoUpdateEnabled && shotsDir is null) _ = CheckForUpdateOnStartupAsync();
        if (shotsDir is not null) _ = window.RunScreenshotsAsync(shotsDir);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DatabaseHealth.Stop();
        ReleaseWriteLock();
        base.OnExit(e);
    }

    /// <summary>Ouvre la base, authentifie l'utilisateur puis fixe le mode (écriture ou lecture seule) selon son rôle.
    /// Retourne false si l'utilisateur renonce.</summary>
    private static bool StartSession(string? cliUser, string? cliPassword)
    {
        Target = Settings.ResolvedTarget;

        if (Target.IsServer)
        {
            if (!EnsureServerReachable()) return false;
        }
        else if (!EnsureDatabaseFileSelected()) return false;

        // 1. Création / mise à jour éventuelle du schéma : réservée au poste qui détient l'écriture.
        var probe = new DbContextFactory(Target, readOnly: true);
        var needsWork = probe.IsNewDatabase() || (probe.GetDatabaseVersion() < DbContextFactory.LatestVersion);
        if (needsWork)
        {
            if (!TryAcquireWriteLock())
            {
                MessageBox.Show(
                    "La base de données doit être créée ou mise à jour, mais elle est utilisée en écriture par " +
                    (WriteLockOwner is null ? "un autre poste" : "@" + WriteLockOwner) +
                    ".\n\nLa mise à jour se fera au prochain lancement sur un poste ayant l'accès en écriture. " +
                    "Demandez à cette personne de fermer le logiciel, puis relancez-le.",
                    "Mise à jour de la base", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            if (!probe.IsNewDatabase() && !ConfirmMigration(probe)) return false;
            var writer = new DbContextFactory(Target, readOnly: false);
            writer.EnsureDatabaseUpToDate(DisplayVersion);
        }
        var version = new DbContextFactory(Target, readOnly: true).GetDatabaseVersion();
        if (version > DbContextFactory.LatestVersion)
        {
            // Le logiciel est trop ancien pour la base : proposer d'abord la mise à jour (la base n'est pas touchée).
            if (OfferUpdateForNewerDatabase()) return false;
            var by = new DbContextFactory(Target, readOnly: true).GetLastMigratedByAppVersion();
            MessageBox.Show(
                $"Cette version du logiciel ({DisplayVersion}) est trop ancienne pour ouvrir la base (version {version}, " +
                $"le logiciel gère jusqu'à la version {DbContextFactory.LatestVersion})." +
                (by is null ? "" : $"\nBase mise à jour par le logiciel {by} ou plus récent.") +
                "\n\nInstallez la dernière version du logiciel. Rien n'a été modifié.",
                "Logiciel trop ancien", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // 2. Authentification (lecture seule suffit ; la création du premier compte demande l'écriture).
        var readFactory = new DbContextFactory(Target, readOnly: true);
        var auth = new AuthService(readFactory);
        AppUser? user;
        if (!auth.HasUsers())
        {
            if (!TryAcquireWriteLock())
            {
                MessageBox.Show(
                    "Aucun compte utilisateur n'existe encore, et un autre poste détient l'accès en écriture" +
                    (WriteLockOwner is null ? "." : $" ({WriteLockOwner}).") +
                    "\n\nLe premier administrateur doit être créé depuis ce poste : réessayez quand l'autre aura fermé le logiciel.",
                    "Premier lancement", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            var writer = new DbContextFactory(Target, readOnly: false);
            var setup = new LoginWindow(new AuthService(writer));
            if (setup.ShowDialog() != true || setup.User is null) return false;
            user = setup.User;
        }
        else if (cliUser is not null)
        {
            user = auth.TryLogin(cliUser, cliPassword ?? "", out var err);
            if (user is null) throw new ComptaException(err ?? "Connexion refusée.");
        }
        else
        {
            var login = new LoginWindow(auth);
            if (login.ShowDialog() != true || login.User is null) return false;
            user = login.User;
        }

        User = user;
        Session = new Session(user.Id, user.UserName, user.DisplayName, user.Role, user.IsDeveloper);
        Settings.LastUserName = user.UserName;
        Settings.Save();

        // 3. Mot de passe provisoire : à changer avant d'aller plus loin.
        if (user.MustChangePassword && !ForcePasswordChange()) return false;

        // 4. Mode : écriture seulement pour un rôle qui en a besoin et si personne d'autre n'écrit.
        var wantsWrite = Session.Can(Permission.WriteLedger) || Session.Can(Permission.ManageStructure);
        if (!wantsWrite) ReleaseWriteLock();
        var gotLock = wantsWrite && (_fileLock is not null || _serverLock is not null || TryAcquireWriteLock());
        BuildFactory(readOnly: !gotLock);
        if (gotLock)
        {
            try { Compta.SyncAllCarries(); } catch (ComptaException) { /* signalé par les écrans concernés */ }
            // Évolution du logiciel : l'empreinte des exercices validés passe à la formule courante (après vérification
            // de l'ancienne, tracée au journal). Un exercice altéré n'est jamais réécrit.
            try { Compta.UpgradeIntegritySeals(); } catch (Exception) { /* sans gravité : refait au prochain démarrage */ }
            Compta.RecordLogin();
        }
        return true;
    }

    private static void BuildFactory(bool readOnly)
    {
        DbFactory = new DbContextFactory(Target, readOnly);
        IsReadOnly = readOnly;
        if (!readOnly) WriteLockOwner = null;
        Compta = new Compta(DbFactory, Session);
    }

    private static bool ConfirmMigration(DbContextFactory probe)
    {
        var from = probe.GetDatabaseVersion();
        var pending = Target.IsServer ? DbContextFactory.PendingServerMigrations(from) : DbContextFactory.PendingMigrations(from);
        if (pending.Count == 0) return true;
        var changes = string.Join("\n", pending.Select(c => "  • " + c));
        return MessageBox.Show(
            $"La base de données est en version {from} ; cette version du logiciel ({DisplayVersion}) a besoin de la version {DbContextFactory.LatestVersion}.\n\n" +
            $"Modifications à appliquer :\n{changes}\n\n" +
            (Target.IsServer
                ? "Faites une sauvegarde du serveur avant de continuer si ce n'est pas déjà fait. "
                : "Une copie de sauvegarde complète de la base sera faite (dossier « Sauvegardes ») avant la mise à jour. ") +
            "Après la mise à jour, les postes équipés d'une version plus ancienne du logiciel ne pourront plus ouvrir la base.\n\n" +
            "Mettre à jour la base maintenant ?",
            "Mise à jour de la base de données", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private static bool ForcePasswordChange()
    {
        // Le changement écrit dans la base : verrou d'écriture obtenu le temps de l'opération.
        var hadLock = _fileLock is not null || _serverLock is not null;
        if (!hadLock && !TryAcquireWriteLock())
        {
            MessageBox.Show(
                "Votre mot de passe est provisoire et doit être changé, mais la base est utilisée en écriture par un autre poste" +
                (WriteLockOwner is null ? "." : $" ({WriteLockOwner}).") +
                "\n\nRéessayez quand l'autre poste aura fermé le logiciel.",
                "Mot de passe à changer", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var writer = new DbContextFactory(Target, readOnly: false);
        var compta = new Compta(writer, Session);
        var dlg = new ChangePasswordWindow(compta, forced: true);
        var ok = dlg.ShowDialog() == true;
        if (!hadLock) ReleaseWriteLock();
        return ok;
    }

    // ------------------------------------------------------------------ choix du fichier / du serveur

    /// <summary>Premier lancement / fichier introuvable : se connecter à une base distante, créer une base locale vide
    /// ou choisir un fichier existant.</summary>
    private static bool EnsureDatabaseFileSelected()
    {
        while (!File.Exists(Settings.ResolvedDatabasePath))
        {
            var first = new FirstRunWindow(Settings.ResolvedDatabasePath);
            if (first.ShowDialog() != true) return false;

            switch (first.Choice)
            {
                case FirstRunChoice.CreateLocal:
                    return true; // créée juste après par EnsureDatabaseUpToDate

                case FirstRunChoice.OpenFile:
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "Choisir une base de données existante",
                        Filter = "Base Tout Compte Fait (*.db)|*.db|Tous les fichiers (*.*)|*.*"
                    };
                    if (dlg.ShowDialog() == true)
                    {
                        Settings.DatabasePath = dlg.FileName;
                        Settings.Save();
                        Target = Settings.ResolvedTarget;
                    }
                    continue;

                case FirstRunChoice.LoadConfig:
                    var loaded = first.DroppedConfigPath is { } droppedPath ? ConfigLoader.ReadFile(droppedPath) : ConfigLoader.Pick(null);
                    if (loaded is null) continue;
                    Settings.DatabaseServer = loaded;   // préremplit la fenêtre (mot de passe à saisir s'il manque)
                    goto case FirstRunChoice.ConnectServer;

                case FirstRunChoice.ConnectServer:
                    var server = new DatabaseServerWindow(firstRun: true);
                    if (server.ShowDialog() != true) continue;   // retour au choix
                    Target = Settings.ResolvedTarget;
                    return EnsureServerReachable();

                default:
                    return false;
            }
        }
        Target = Settings.ResolvedTarget;
        return true;
    }
    /// <summary>Mode serveur : vérifie que le serveur répond. En cas d'échec, propose de réessayer, de revenir au
    /// fichier SQLite local ou de quitter.</summary>
    private static bool EnsureServerReachable()
    {
        while (true)
        {
            string error;
            try
            {
                new DbContextFactory(Target).TestConnection();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnermostMessage();
            }

            var choice = MessageBox.Show(
                $"Impossible de joindre le serveur de base de données :\n{Settings.DatabaseServer.Describe()}\n\n{error}\n\n" +
                "Oui : réessayer.\n" +
                "Non : utiliser de nouveau le fichier SQLite local.\n" +
                "Annuler : fermer le logiciel.",
                "Serveur de base de données injoignable", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Yes) continue;
            if (choice != MessageBoxResult.No) return false;

            Settings.UseDatabaseServer = false;
            Settings.Save();
            Target = Settings.ResolvedTarget;
            return EnsureDatabaseFileSelected();
        }
    }

    // ------------------------------------------------------------------ verrou d'écriture

    /// <summary>Tente de devenir le rédacteur : fichier « .lock » voisin (SQLite) ou ligne de verrou avec battement
    /// de cœur (serveur), comme FiltresApp.</summary>
    public static bool TryAcquireWriteLock()
    {
        if (_fileLock is not null || _serverLock is not null) return true;
        if (Target.IsServer)
        {
            var result = ServerWriteLock.TryAcquire(Target, User?.UserName);
            if (result.Lock is null && result.OwnerUser is not null)
            {
                WriteLockOwner = result.OwnerUser;
                return false;
            }
            _serverLock = result.Lock; // null si la base n'existe pas encore : créée par ce poste, verrou pris après
            if (_serverLock is not null) StartServerLockHeartbeat();
            return true;
        }

        _fileLock = WriteLock.TryAcquire(Target.FilePath!, User?.UserName);
        if (_fileLock is null)
        {
            WriteLockOwner = WriteLock.ReadOwner(Target.FilePath!);
            return false;
        }
        return true;
    }

    private static void ReleaseWriteLock()
    {
        _serverLockTimer?.Stop();
        _serverLock?.Dispose();
        _serverLock = null;
        _fileLock?.Dispose();
        _fileLock = null;
    }

    /// <summary>Renouvelle le verrou du serveur toutes les 15 s. Si un autre poste l'a repris (ce poste est resté
    /// injoignable plus d'une minute), le logiciel redémarre en lecture seule.</summary>
    private static void StartServerLockHeartbeat()
    {
        _serverLockTimer?.Stop();
        _serverLockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _serverLockTimer.Tick += async (_, _) =>
        {
            var held = _serverLock;
            if (held is null) return;
            bool still;
            try { still = await Task.Run(held.Refresh); }
            catch { return; } // serveur injoignable : on réessaie (la puce du pied de page signale la perte)
            if (still) return;

            _serverLockTimer?.Stop();
            _serverLock = null;
            string? by = null;
            try { by = await Task.Run(() => ServerWriteLock.ReadOwner(Target)); } catch { /* sans gravité */ }
            MessageBox.Show(
                (by is null ? "Un autre poste a repris" : $"{by} a repris") +
                " l'accès en écriture à la base (reprise forcée par un administrateur, ou ce poste est resté injoignable trop longtemps).\n\n" +
                "Vous venez de perdre le droit d'écriture : le logiciel va redémarrer en lecture seule. " +
                "Les modifications non enregistrées sont perdues.",
                "Accès en écriture perdu", MessageBoxButton.OK, MessageBoxImage.Warning);
            Restart();
        };
        _serverLockTimer.Start();
    }

    /// <summary>Ce poste est en lecture seule faute de verrou : tente de prendre la main (l'autre poste a fermé).</summary>
    public static bool TryTakeOverWrite()
    {
        if (!IsReadOnly) return true;
        if (!(Session.Can(Permission.WriteLedger) || Session.Can(Permission.ManageStructure))) return false;
        if (!TryAcquireWriteLock()) return false;
        BuildFactory(readOnly: false);
        try { Compta.SyncAllCarries(); } catch (ComptaException) { }
        DatabaseReopened?.Invoke();
        return true;
    }

    /// <summary>Administrateur uniquement : prend l'écriture même si un autre poste la détient. Base sur serveur : le
    /// verrou est écrasé ; fichier SQLite : une demande est déposée, le poste rédacteur libère le verrou (quelques
    /// secondes). Le poste évincé reçoit une alerte et passe en lecture seule. Retourne un message d'erreur, ou null.</summary>
    public static string? ForceTakeOverWrite()
    {
        if (!Session.Can(Permission.ManageUsers)) return "Réservé aux administrateurs.";
        if (!IsReadOnly) return null;
        var previous = WriteLockOwner;
        if (Target.IsServer)
        {
            try { _serverLock = ServerWriteLock.ForceAcquire(Target, User?.UserName); }
            catch (Exception ex) { return "Serveur injoignable : " + ex.InnermostMessage(); }
            StartServerLockHeartbeat();
        }
        else
        {
            var path = Target.FilePath!;
            try { WriteLock.RequestTakeover(path, User?.UserName ?? Environment.UserName); }
            catch (Exception ex) { return "Demande de reprise impossible : " + ex.Message; }
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (_fileLock is null && DateTime.UtcNow < deadline)
            {
                _fileLock = WriteLock.TryAcquire(path, User?.UserName);
                if (_fileLock is null) System.Threading.Thread.Sleep(300);
            }
            WriteLock.ClearTakeoverRequest(path);
            if (_fileLock is null)
                return "Le poste en écriture" + (previous is null ? "" : $" ({previous})") +
                       " n'a pas libéré l'accès dans le délai. Il est peut-être bloqué ou utilise une ancienne version du logiciel : " +
                       "fermez le logiciel sur ce poste.";
        }
        WriteLockOwner = null;
        BuildFactory(readOnly: false);
        try { Compta.SyncAllCarries(); } catch (ComptaException) { }
        try { Compta.RecordForcedTakeover(previous); } catch (Exception) { /* trace seulement */ }
        DatabaseReopened?.Invoke();
        return null;
    }

    /// <summary>Poste rédacteur (fichier SQLite) : un administrateur demande la reprise. Le verrou est libéré, une
    /// alerte est affichée puis le logiciel redémarre en lecture seule. Retourne true si la reprise a été honorée.</summary>
    public static bool HonorTakeoverRequest()
    {
        if (IsReadOnly || _fileLock is null || Target.IsServer) return false;
        var by = WriteLock.ConsumeTakeoverRequest(Target.FilePath!);
        if (by is null) return false;
        ReleaseWriteLock();
        MessageBox.Show(
            $"{by} (administrateur) vient de reprendre de force l'accès en écriture à la base.\n\n" +
            "Vous venez de perdre le droit d'écriture : le logiciel va redémarrer en lecture seule. " +
            "Les modifications non enregistrées sont perdues.",
            "Accès en écriture perdu", MessageBoxButton.OK, MessageBoxImage.Warning);
        Restart();
        return true;
    }

    /// <summary>Levé quand le mode d'accès change (reprise de l'écriture).</summary>
    public static event Action? DatabaseReopened;

    /// <summary>Exécute une opération qui doit écrire (ex. changer son mot de passe) même sur un poste en lecture
    /// seule : prend le verrou le temps de l'opération puis le rend.</summary>
    public static T WithWriteAccess<T>(Func<Compta, T> action)
    {
        if (!IsReadOnly) return action(Compta);
        var hadLock = _fileLock is not null || _serverLock is not null;
        if (!hadLock && !TryAcquireWriteLock())
            throw new ComptaException("La base est utilisée en écriture par un autre poste" +
                                      (WriteLockOwner is null ? "" : $" ({WriteLockOwner})") +
                                      " : réessayez dans un instant.");
        try
        {
            return action(new Compta(new DbContextFactory(Target, readOnly: false), Session));
        }
        finally
        {
            if (!hadLock) ReleaseWriteLock();
        }
    }

    // ------------------------------------------------------------------ changement de base / redémarrage

    /// <summary>Relance le logiciel dans un nouveau processus puis ferme celui-ci (changement de base, de session…).</summary>
    public static void Restart(string? arguments = null)
    {
        ReleaseWriteLock();
        var exePath = Environment.ProcessPath;
        if (exePath is not null)
            Process.Start(new ProcessStartInfo { FileName = exePath, Arguments = arguments ?? string.Empty, UseShellExecute = true });
        Current.Dispatcher.Invoke(() => Current.Shutdown());
    }
}

/// <summary>Petites boîtes de dialogue communes.</summary>
public static class Ui
{
    public static void Info(string text, string? title = null) =>
        MessageBox.Show(text, title ?? Editions.AppName, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Error(string text, string? title = null) =>
        MessageBox.Show(text, title ?? Editions.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static bool Confirm(string text, string title = "Confirmation") =>
        MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Exécute une action d'écriture : les erreurs métier sont affichées sans planter. Retourne true si OK.</summary>
    public static bool Try(Action action)
    {
        try { action(); return true; }
        catch (ComptaException ex) { Error(ex.Message); return false; }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            var inner = ex.InnerException ?? ex;
            Error("La base a refusé l'opération :\n" + inner.Message);
            return false;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) { Error("La base a refusé l'opération :\n" + ex.Message); return false; }
    }
}

