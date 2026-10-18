using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ToutCompteFait.Controls;

/// <summary>Camembert dessiné à la main (même esprit que <see cref="BarChart"/>) : répartition d'un total entre éléments,
/// avec légende (montant et pourcentage). Seules les valeurs positives sont représentées.</summary>
public sealed class PieChart : FrameworkElement
{
    public sealed record Slice(string Name, Brush Brush, long Cents);

    private IReadOnlyList<Slice> _slices = Array.Empty<Slice>();
    private string _title = "";

    public PieChart()
    {
        MinHeight = 260;
        SnapsToDevicePixels = true;
    }

    public void SetData(string title, IReadOnlyList<Slice> slices)
    {
        _title = ToutCompteFait.Core.Wording.Adapt(title);
        _slices = slices.Where(s => s.Cents > 0).ToList();
        Height = 34 + 210 + _slices.Count * 18 + 28;
        InvalidateVisual();
    }

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private FormattedText Text(string s, double size, Brush brush, FontWeight? weight = null) =>
        new(s, Fr, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
        var muted = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        var dark = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
        if (_title.Length > 0) dc.DrawText(Text(_title, 13, dark, FontWeights.SemiBold), new Point(10, 6));
        var total = _slices.Sum(s => s.Cents);
        if (total <= 0 || w < 120 || h < 120)
        {
            if (total <= 0 && w >= 120 && h >= 120)
            {
                // État vide : anneau pâle et message, plutôt qu'un cadre blanc.
                var d = Math.Max(60, Math.Min(w - 48, h - 90));
                var c = new Point(w / 2, 44 + d / 2);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)), d * 0.18), c, d / 2 - d * 0.09, d / 2 - d * 0.09);
                var msg = Text("Aucun montant à répartir", 12, muted);
                dc.DrawText(msg, new Point(c.X - msg.Width / 2, c.Y - msg.Height / 2));
            }
            return;
        }

        // Disque en haut, légende dessous : la largeur disponible suffit, sans défilement.
        const double top = 34;
        var legendRows = _slices.Count;
        var legendH = legendRows * 18 + 8;
        var diameter = Math.Max(60, Math.Min(w - 24, h - top - legendH - 8));
        var center = new Point(w / 2, top + diameter / 2);
        var radius = diameter / 2;

        var angle = -90.0;
        foreach (var s in _slices)
        {
            var sweep = 360.0 * s.Cents / total;
            if (sweep >= 359.99)
            {
                dc.DrawEllipse(s.Brush, new Pen(Brushes.White, 1.5), center, radius, radius);
            }
            else
            {
                Point P(double deg) => new(center.X + radius * Math.Cos(deg * Math.PI / 180), center.Y + radius * Math.Sin(deg * Math.PI / 180));
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(center, true, true);
                    ctx.LineTo(P(angle), true, false);
                    ctx.ArcTo(P(angle + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(s.Brush, new Pen(Brushes.White, 1.5), geo);
            }
            angle += sweep;
        }

        var y = top + diameter + 12;
        foreach (var s in _slices)
        {
            var pct = 100.0 * s.Cents / total;
            dc.DrawRectangle(s.Brush, null, new Rect(12, y + 3, 10, 10));
            var amount = Text($"{(s.Cents / 100m).ToString("N0", Fr)} €  ·  {pct.ToString("N1", Fr)} %", 11, muted);
            var name = Text(s.Name, 11, dark);
            name.MaxTextWidth = Math.Max(20, w - 24 - 14 - amount.Width - 12);
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(name, new Point(28, y));
            dc.DrawText(amount, new Point(w - 12 - amount.Width, y));
            y += 18;
        }
    }
}
