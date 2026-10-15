using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ToutCompteFait.Controls;

/// <summary>Histogramme groupé dessiné à la main (aucune bibliothèque de graphiques) : une série = une couleur, une
/// catégorie = un groupe de barres. Gère les valeurs négatives ; montants en centimes, affichés en euros.</summary>
public sealed class BarChart : FrameworkElement
{
    public sealed record Series(string Name, Brush Brush, IReadOnlyList<long> Values);

    private IReadOnlyList<string> _categories = Array.Empty<string>();
    private IReadOnlyList<Series> _series = Array.Empty<Series>();
    private string _title = "";

    public BarChart()
    {
        MinHeight = 240;
        SnapsToDevicePixels = true;
    }

    public void SetData(string title, IReadOnlyList<string> categories, IReadOnlyList<Series> series)
    {
        _title = title;
        _categories = categories;
        _series = series;
        InvalidateVisual();
    }

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private FormattedText Text(string s, double size, Brush brush, FontWeight? weight = null) =>
        new(s, Fr, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string Euros(long cents) => (cents / 100m).ToString("N0", Fr) + " €";

    /// <summary>Pas « rond » de l'axe : 1, 2, 2,5 ou 5 × 10^k.</summary>
    internal static double NiceStep(double range, int ticks)
    {
        if (range <= 0) return 1;
        var raw = range / ticks;
        var pow = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var f = raw / pow;
        var m = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        return m * pow;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
        var muted = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)), 1);
        var dark = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));

        if (_title.Length > 0) dc.DrawText(Text(_title, 13, dark, FontWeights.SemiBold), new Point(10, 6));

        // Légende : passe à la ligne quand la largeur manque (aucun défilement horizontal).
        var lx = 10.0;
        var ly = 28.0;
        foreach (var s in _series)
        {
            var t = Text(s.Name, 11, muted);
            if (lx > 10 && lx + 14 + t.Width > w - 10) { lx = 10; ly += 16; }
            dc.DrawRectangle(s.Brush, null, new Rect(lx, ly + 3, 10, 10));
            dc.DrawText(t, new Point(lx + 14, ly));
            lx += 14 + t.Width + 16;
        }
        var legendBottom = ly + 16;

        if (_categories.Count == 0 || _series.Count == 0 || w < 120 || h < 120) return;

        var all = _series.SelectMany(s => s.Values).Select(v => v / 100.0).Concat(new[] { 0.0 }).ToList();
        var max = all.Max();
        var min = all.Min();
        var step = NiceStep(max - min, 5);
        var top = Math.Ceiling(max / step) * step;
        var bottom = Math.Floor(min / step) * step;
        if (top == bottom) top = bottom + step;

        const double left = 74, right = 12, bottomPad = 34;
        var topPad = Math.Max(56, legendBottom + 14);
        var plot = new Rect(left, topPad, Math.Max(10, w - left - right), Math.Max(10, h - topPad - bottomPad));
        double Y(double v) => plot.Bottom - (v - bottom) / (top - bottom) * plot.Height;

        for (var v = bottom; v <= top + step / 2; v += step)
        {
            var y = Y(v);
            dc.DrawLine(Math.Abs(v) < 1e-9 ? new Pen(muted, 1.2) : grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(((decimal)v).ToString("N0", Fr) + " €", 10, muted);
            dc.DrawText(label, new Point(plot.Left - 6 - label.Width, y - label.Height / 2));
        }

        var groupW = plot.Width / _categories.Count;
        var barW = Math.Min(56, groupW * 0.8 / _series.Count);
        var zero = Y(0);
        for (var c = 0; c < _categories.Count; c++)
        {
            var gx = plot.Left + c * groupW + (groupW - barW * _series.Count) / 2;
            for (var s = 0; s < _series.Count; s++)
            {
                var cents = c < _series[s].Values.Count ? _series[s].Values[c] : 0;
                var y = Y(cents / 100.0);
                var rect = new Rect(gx + s * barW + 1, Math.Min(y, zero), Math.Max(1, barW - 2), Math.Max(1, Math.Abs(zero - y)));
                dc.DrawRectangle(_series[s].Brush, null, rect);
                if (barW >= 34 || _series.Count == 1)
                {
                    var t = Text(Euros(cents), 10, dark);
                    var ty = cents >= 0 ? rect.Top - t.Height - 1 : rect.Bottom + 1;
                    dc.DrawText(t, new Point(rect.Left + rect.Width / 2 - t.Width / 2, ty));
                }
            }
            var cat = Text(_categories[c], 11, dark);
            cat.MaxTextWidth = Math.Max(10, groupW - 6);
            cat.MaxLineCount = 1;
            cat.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(cat, new Point(plot.Left + c * groupW + groupW / 2 - cat.Width / 2, plot.Bottom + 8));
        }
    }
}
