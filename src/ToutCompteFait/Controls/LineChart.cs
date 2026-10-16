using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ToutCompteFait.Controls;

/// <summary>Graphique en ligne dessiné à la main (même esprit que <see cref="BarChart"/>) : une série = une courbe, une
/// catégorie = un point sur l'axe horizontal. S'adapte à la largeur disponible, sans défilement. Montants en centimes.</summary>
public sealed class LineChart : FrameworkElement
{
    private IReadOnlyList<string> _categories = Array.Empty<string>();
    private IReadOnlyList<BarChart.Series> _series = Array.Empty<BarChart.Series>();
    private string _title = "";

    public LineChart()
    {
        MinHeight = 260;
        SnapsToDevicePixels = true;
    }

    public void SetData(string title, IReadOnlyList<string> categories, IReadOnlyList<BarChart.Series> series)
    {
        _title = ToutCompteFait.Core.Wording.Adapt(title);
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

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
        var muted = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)), 1);
        var dark = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));

        if (_title.Length > 0) dc.DrawText(Text(_title, 13, dark, FontWeights.SemiBold), new Point(10, 6));
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
        var step = BarChart.NiceStep(max - min, 5);
        var top = Math.Ceiling(max / step) * step;
        var bottom = Math.Floor(min / step) * step;
        if (top == bottom) top = bottom + step;

        const double left = 74, right = 24, bottomPad = 34;
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

        var n = _categories.Count;
        double X(int i) => n == 1 ? plot.Left + plot.Width / 2 : plot.Left + plot.Width * i / (n - 1);
        // Étiquettes de l'axe : une sur k, pour ne jamais se chevaucher.
        var maxLabel = _categories.Max(c => Text(c, 11, dark).Width) + 10;
        var every = Math.Max(1, (int)Math.Ceiling(maxLabel / Math.Max(1, plot.Width / Math.Max(1, n))));
        for (var i = 0; i < n; i += every)
        {
            var cat = Text(_categories[i], 11, dark);
            dc.DrawText(cat, new Point(Math.Clamp(X(i) - cat.Width / 2, 0, Math.Max(0, w - cat.Width)), plot.Bottom + 8));
        }

        var showValues = _series.Count == 1 && n <= 14 && plot.Width / n >= 44;
        foreach (var s in _series)
        {
            var pen = new Pen(s.Brush, 2.5) { LineJoin = PenLineJoin.Round };
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (var i = 0; i < n; i++)
                {
                    var p = new Point(X(i), Y((i < s.Values.Count ? s.Values[i] : 0) / 100.0));
                    if (i == 0) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, true);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
            for (var i = 0; i < n; i++)
            {
                var cents = i < s.Values.Count ? s.Values[i] : 0;
                var p = new Point(X(i), Y(cents / 100.0));
                dc.DrawEllipse(Brushes.White, pen, p, 3.5, 3.5);
                if (!showValues) continue;
                var t = Text(Euros(cents), 10, dark);
                dc.DrawText(t, new Point(Math.Clamp(p.X - t.Width / 2, 0, Math.Max(0, w - t.Width)), p.Y - t.Height - 6));
            }
        }
    }
}
