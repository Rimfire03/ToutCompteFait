using System.Globalization;

namespace ToutCompteFait.Core;

/// <summary>Montants : toujours des centimes entiers (long) en base et dans tous les calculs, jamais de
/// double/float. Seul l'affichage et la saisie passent par un décimal.</summary>
public static class Money
{
    public static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>« 1 234,56 » (espace insécable comme séparateur de milliers, virgule décimale).</summary>
    public static string Format(long cents) => (cents / 100m).ToString("N2", Fr);

    /// <summary>Comme <see cref="Format"/> mais vide pour zéro (colonnes d'un tableau).</summary>
    public static string FormatOrBlank(long cents) => cents == 0 ? string.Empty : Format(cents);

    public static long FromDecimal(decimal value) =>
        (long)Math.Round(value * 100m, 0, MidpointRounding.AwayFromZero);

    /// <summary>Conversion d'un nombre lu dans Excel (double) : arrondi au centime le plus proche.</summary>
    public static long FromDouble(double value) => FromDecimal(Convert.ToDecimal(value));

    /// <summary>Lit un montant saisi : « 1 234,56 », « 1234.56 », « -12,5 », « 12 € ». Refuse tout ce qui a plus
    /// de 2 décimales ou n'est pas un nombre.</summary>
    public static bool TryParse(string? text, out long cents)
    {
        cents = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim().Replace("€", "").Replace(" ", "").Replace(" ", "").Replace(" ", "");
        if (s.Length == 0) return false;

        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            // Les deux présents : le dernier est le séparateur décimal, l'autre un séparateur de milliers.
            if (lastComma > lastDot) s = s.Replace(".", "").Replace(',', '.');
            else s = s.Replace(",", "");
        }
        else if (lastComma >= 0)
        {
            if (s.IndexOf(',') != lastComma) return false; // plusieurs virgules : ambigu
            s = s.Replace(',', '.');
        }

        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value)) return false;
        if (decimal.Round(value, 2) != value) return false; // plus de 2 décimales
        cents = FromDecimal(value);
        return true;
    }
}
