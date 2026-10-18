using System.Diagnostics;

namespace ToutCompteFait.Services;

/// <summary>Accès direct au site officiel de déclaration du chiffre d'affaires des micro-entrepreneurs.</summary>
public static class UrssafSite
{
    public const string Address = "https://www.autoentrepreneur.urssaf.fr/";

    /// <summary>Ouvre le site dans le navigateur par défaut ; en cas d'échec, copie l'adresse dans le presse-papiers.</summary>
    public static void Open()
    {
        try { Process.Start(new ProcessStartInfo(Address) { UseShellExecute = true }); }
        catch (Exception)
        {
            try { System.Windows.Clipboard.SetText(Address); } catch { /* presse-papiers indisponible */ }
            Ui.Info("Impossible d'ouvrir le navigateur. L'adresse a été copiée dans le presse-papiers :\n" + Address);
        }
    }
}
