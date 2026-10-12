using ToutCompteFait.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace ToutCompteFait.Core;

public sealed partial class Compta
{
    /// <summary>Consigne au journal d'audit qu'un administrateur a repris l'écriture de force.</summary>
    public void RecordForcedTakeover(string? previousOwner)
    {
        Require(Permission.ManageUsers);
        using var tx = Begin();
        var ctx = Open().Ctx;
        Log(ctx, "Reprise d'écriture forcée", previousOwner is null ? "poste précédent inconnu" : $"écriture reprise à {previousOwner}");
        Save(ctx);
        tx.Commit();
    }

    // ------------------------------------------------------------------ déconnexion automatique

    private const string IdleTimeoutKey = "security.idle_minutes";

    /// <summary>Durée maximale d'inactivité des utilisateurs : 24 h.</summary>
    public const int MaxIdleTimeoutMinutes = 1440;

    /// <summary>Délai d'inactivité (en minutes) après lequel le logiciel déconnecte l'utilisateur ; 0 = jamais.
    /// Réglage de la base : défini par un administrateur, il s'applique à tous les utilisateurs et à tous les postes.</summary>
    public int GetIdleTimeoutMinutes()
    {
        using var l = Open();
        var value = l.Ctx.BusinessSettings.AsNoTracking().Where(s => s.Key == IdleTimeoutKey).Select(s => s.Value).FirstOrDefault();
        return int.TryParse(value, out var minutes) && minutes is > 0 and <= MaxIdleTimeoutMinutes ? minutes : 0;
    }

    /// <summary>Définit le délai d'inactivité (0 = désactivé). Administrateur uniquement ; tracé au journal d'audit.</summary>
    public void SetIdleTimeoutMinutes(int minutes)
    {
        Require(Permission.ManageUsers);
        RequireWritable();
        if (minutes is < 0 or > MaxIdleTimeoutMinutes)
            throw new ComptaException($"Le délai doit être compris entre 0 (désactivé) et {MaxIdleTimeoutMinutes} minutes.");
        using var tx = Begin();
        var ctx = Open().Ctx;
        WriteBusinessSettings(ctx, new Dictionary<string, string?> { [IdleTimeoutKey] = minutes == 0 ? null : minutes.ToString() });
        Save(ctx);
        Log(ctx, "Déconnexion automatique", minutes == 0 ? "désactivée" : $"après {minutes} minute(s) d'inactivité");
        tx.Commit();
    }
}
