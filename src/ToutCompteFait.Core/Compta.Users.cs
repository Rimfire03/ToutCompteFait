using System.Text.RegularExpressions;
using ToutCompteFait.Core.Data;
using ToutCompteFait.Core.Security;
using ToutCompteFait.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

/// <summary>Connexion : identification d'un utilisateur avant toute ouverture de session comptable. Fonctionne sur une
/// base ouverte en lecture seule (la dernière connexion n'est alors simplement pas enregistrée).</summary>
public sealed class AuthService
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(2);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (int Fails, DateTime Until)> Throttle = new();

    // Hachage factice comparé quand l'identifiant n'existe pas : le temps de réponse ne révèle pas l'existence du compte.
    private static readonly string DummyHash = PasswordHasher.Hash("dummy-password-0");

    private readonly DbContextFactory _factory;

    public AuthService(DbContextFactory factory) => _factory = factory;

    public bool HasUsers()
    {
        using var ctx = _factory.Create();
        return ctx.Users.AsNoTracking().Any();
    }

    /// <summary>Crée le premier compte, nécessairement administrateur, quand aucun utilisateur n'existe encore.</summary>
    public AppUser CreateFirstAdmin(string userName, string displayName, string password)
    {
        if (_factory.ReadOnly)
            throw new ComptaException("La base est ouverte en lecture seule : impossible de créer le premier compte maintenant.");
        userName = UserRules.NormalizeUserName(userName);
        var pwdError = PasswordPolicy.Validate(password, userName);
        if (pwdError is not null) throw new ComptaException(pwdError);
        using var ctx = _factory.Create();
        using var tx = ctx.Database.BeginTransaction();
        if (ctx.Users.AsNoTracking().Any())
            throw new ComptaException("Des comptes existent déjà : seul un administrateur peut en créer.");
        var e = new UserEntity
        {
            UserName = userName,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            Role = RoleInfo.ToDb(Role.Admin),
            Active = true,
            MustChangePassword = false,
            CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            CreatedBy = "installation"
        };
        ctx.Users.Add(e);
        ctx.SaveChanges();
        ctx.Audit.Add(new AuditEntity
        {
            At = e.CreatedAt, UserName = userName, Action = "Premier administrateur créé", Detail = userName
        });
        ctx.SaveChanges();
        tx.Commit();
        return UserRules.ToUser(e);
    }

    /// <summary>Vérifie l'identifiant et le mot de passe. Retourne l'utilisateur, ou null avec le message à afficher.
    /// Après 5 échecs consécutifs sur un identifiant, il est bloqué 2 minutes.</summary>
    public AppUser? TryLogin(string userName, string password, out string? error)
    {
        error = null;
        var key = (userName ?? "").Trim().ToLowerInvariant();
        lock (Gate)
        {
            if (Throttle.TryGetValue(key, out var t) && t.Until > DateTime.UtcNow)
            {
                var wait = (int)Math.Ceiling((t.Until - DateTime.UtcNow).TotalSeconds);
                error = $"Trop de tentatives. Réessayez dans {wait} s.";
                return null;
            }
        }

        UserEntity? u;
        using (var ctx = _factory.Create())
            u = ctx.Users.AsNoTracking().AsEnumerable()
                .FirstOrDefault(x => string.Equals(x.UserName, key, StringComparison.OrdinalIgnoreCase));

        var ok = PasswordHasher.Verify(password ?? "", u?.PasswordHash ?? DummyHash) && u is not null;
        if (!ok)
        {
            lock (Gate)
            {
                var fails = Throttle.TryGetValue(key, out var t) ? t.Fails + 1 : 1;
                Throttle[key] = fails >= MaxFailures ? (0, DateTime.UtcNow + LockDuration) : (fails, DateTime.MinValue);
            }
            error = "Identifiant ou mot de passe incorrect.";
            return null;
        }
        lock (Gate) Throttle.Remove(key);

        if (!u!.Active)
        {
            error = "Ce compte est désactivé. Contactez un administrateur.";
            return null;
        }

        if (!_factory.ReadOnly)
        {
            try
            {
                using var ctx = _factory.Create();
                var tracked = ctx.Users.FirstOrDefault(x => x.Id == u.Id);
                if (tracked is not null)
                {
                    tracked.LastLoginAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                    ctx.SaveChanges();
                }
            }
            catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                // Information de confort : n'empêche jamais la connexion.
            }
        }
        return UserRules.ToUser(u);
    }
}

internal static class UserRules
{
    private static readonly Regex NamePattern = new(@"^[\p{L}\p{N}][\p{L}\p{N}._\-]{2,49}$", RegexOptions.Compiled);

    public static string NormalizeUserName(string? name)
    {
        var n = (name ?? "").Trim();
        if (!NamePattern.IsMatch(n))
            throw new ComptaException("L'identifiant doit faire 3 à 50 caractères : lettres, chiffres, point, tiret ou souligné (sans espace).");
        return n;
    }

    public static AppUser ToUser(UserEntity u) => new(u.Id, u.UserName, u.DisplayName, RoleInfo.FromDb(u.Role), u.Active,
        u.MustChangePassword,
        u.LastLoginAt is null ? null : DateTime.Parse(u.LastLoginAt, System.Globalization.CultureInfo.InvariantCulture),
        DateTime.Parse(u.CreatedAt, System.Globalization.CultureInfo.InvariantCulture), u.CreatedBy, u.IsDeveloper);
}

public sealed partial class Compta
{
    // ------------------------------------------------------------------ gestion des comptes (administrateur)

    public List<AppUser> Users()
    {
        Require(Permission.ManageUsers);
        using var l = Open();
        return l.Ctx.Users.AsNoTracking().OrderBy(u => u.UserName).AsEnumerable().Select(UserRules.ToUser).ToList();
    }

    /// <summary>Règle du statut Développeur : seul un administrateur peut l'attribuer ou le retirer. Le statut
    /// s'ajoute au rôle du compte (lecteur, trésorier ou administrateur).</summary>
    private void CheckDeveloperRule(AppDbContext ctx, bool newIsDeveloper, UserEntity? target)
    {
        var changes = target is null ? newIsDeveloper : newIsDeveloper != target.IsDeveloper;
        if (changes && Session.Role != Role.Admin)
            throw new ComptaException("Seul un administrateur peut attribuer ou retirer le statut Développeur.");
    }

    /// <summary>Crée un compte. Seul un administrateur le peut. Le mot de passe initial est provisoire : l'utilisateur
    /// devra le changer à sa première connexion.</summary>
    public AppUser CreateUser(string userName, string displayName, Role role, string initialPassword, bool isDeveloper = false)
    {
        Require(Permission.ManageUsers);
        RequireWritable();
        userName = UserRules.NormalizeUserName(userName);
        var pwdError = PasswordPolicy.Validate(initialPassword, userName);
        if (pwdError is not null) throw new ComptaException(pwdError);
        using var tx = Begin();
        var ctx = Open().Ctx;
        if (ctx.Users.AsNoTracking().AsEnumerable().Any(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase)))
            throw new ComptaException($"L'identifiant « {userName} » existe déjà.");
        CheckDeveloperRule(ctx, isDeveloper, null);
        var e = new UserEntity
        {
            UserName = userName,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName.Trim(),
            PasswordHash = PasswordHasher.Hash(initialPassword),
            Role = RoleInfo.ToDb(role),
            IsDeveloper = isDeveloper,
            Active = true,
            MustChangePassword = true,
            CreatedAt = Now(),
            CreatedBy = UserName
        };
        ctx.Users.Add(e);
        Save(ctx);
        Log(ctx, "Compte utilisateur créé", $"{userName} ({RoleInfo.DisplayName(role)}{(isDeveloper ? " + Développeur" : "")})");
        tx.Commit();
        return UserRules.ToUser(e);
    }

    /// <summary>Modifie le nom affiché, le rôle ou l'état (actif / désactivé). Le dernier administrateur actif ne peut
    /// être ni rétrogradé ni désactivé.</summary>
    /// <param name="isDeveloper">Statut Développeur ; <c>null</c> = inchangé.</param>
    public void UpdateUser(long userId, string displayName, Role role, bool active, bool? isDeveloper = null)
    {
        Require(Permission.ManageUsers);
        RequireWritable();
        using var tx = Begin();
        var ctx = Open().Ctx;
        var u = ctx.Users.FirstOrDefault(x => x.Id == userId) ?? throw new ComptaException("Utilisateur introuvable.");
        var newDeveloper = isDeveloper ?? u.IsDeveloper;
        CheckDeveloperRule(ctx, newDeveloper, u);
        var wasAdmin = RoleInfo.FromDb(u.Role) == Role.Admin && u.Active;
        var willBeAdmin = role == Role.Admin && active;
        if (wasAdmin && !willBeAdmin)
        {
            var others = ctx.Users.AsNoTracking().Count(x => x.Id != userId && x.Role == "ADMIN" && x.Active);
            if (others == 0)
                throw new ComptaException("C'est le dernier administrateur actif : il ne peut être ni rétrogradé ni désactivé.");
        }
        var before = $"{RoleInfo.DisplayName(RoleInfo.FromDb(u.Role))}, {(u.Active ? "actif" : "désactivé")}";
        u.DisplayName = string.IsNullOrWhiteSpace(displayName) ? u.UserName : displayName.Trim();
        u.Role = RoleInfo.ToDb(role);
        u.IsDeveloper = newDeveloper;
        u.Active = active;
        Save(ctx);
        Log(ctx, "Compte utilisateur modifié", $"{u.UserName} : {before} → {RoleInfo.DisplayName(role)}, {(active ? "actif" : "désactivé")}");
        tx.Commit();
    }

    /// <summary>Réinitialise le mot de passe d'un compte (mot de passe provisoire à changer à la prochaine connexion).</summary>
    public void ResetPassword(long userId, string newPassword)
    {
        Require(Permission.ManageUsers);
        RequireWritable();
        using var tx = Begin();
        var ctx = Open().Ctx;
        var u = ctx.Users.FirstOrDefault(x => x.Id == userId) ?? throw new ComptaException("Utilisateur introuvable.");
        var err = PasswordPolicy.Validate(newPassword, u.UserName);
        if (err is not null) throw new ComptaException(err);
        u.PasswordHash = PasswordHasher.Hash(newPassword);
        u.MustChangePassword = true;
        Save(ctx);
        Log(ctx, "Mot de passe réinitialisé", u.UserName);
        tx.Commit();
    }

    /// <summary>Enregistre l'heure de connexion de l'utilisateur de la session (sans effet en lecture seule).</summary>
    public void RecordLogin()
    {
        if (_factory.ReadOnly || Session.UserId == 0) return;
        try
        {
            using var l = Open();
            var u = l.Ctx.Users.FirstOrDefault(x => x.Id == Session.UserId);
            if (u is null) return;
            u.LastLoginAt = Now();
            Save(l.Ctx);
        }
        catch (DbUpdateException)
        {
            // information de confort : n'empêche jamais le travail
        }
    }

    /// <summary>Change son propre mot de passe (tous les rôles). Nécessite que la base soit inscriptible.</summary>
    public void ChangeOwnPassword(string currentPassword, string newPassword)
    {
        RequireWritable();
        if (Session.UserId == 0) throw new ComptaException("La session interne n'a pas de mot de passe.");
        using var tx = Begin();
        var ctx = Open().Ctx;
        var u = ctx.Users.FirstOrDefault(x => x.Id == Session.UserId) ?? throw new ComptaException("Utilisateur introuvable.");
        if (!PasswordHasher.Verify(currentPassword ?? "", u.PasswordHash))
            throw new ComptaException("Le mot de passe actuel est incorrect.");
        var err = PasswordPolicy.Validate(newPassword, u.UserName);
        if (err is not null) throw new ComptaException(err);
        if (PasswordHasher.Verify(newPassword, u.PasswordHash))
            throw new ComptaException("Le nouveau mot de passe doit être différent de l'actuel.");
        u.PasswordHash = PasswordHasher.Hash(newPassword);
        u.MustChangePassword = false;
        Save(ctx);
        Log(ctx, "Mot de passe changé", u.UserName);
        tx.Commit();
    }
}

