using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GordonGeoSteer.Model;

namespace GordonGeoSteer.Controls;

/// <summary>
/// Color-mapped plan view of a bed grid: East to the right, North up. Each grid node is drawn as a
/// cell centred on the node; color = TVD (shallow = yellow, deep = dark blue), with a legend.
/// </summary>
public sealed class GridHeatmap : FrameworkElement
{
    public static readonly DependencyProperty GridProperty =
        DependencyProperty.Register(nameof(Grid), typeof(BedGrid), typeof(GridHeatmap),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public BedGrid? Grid { get => (BedGrid?)GetValue(GridProperty); set => SetValue(GridProperty, value); }

    static readonly Typeface Face = new("Segoe UI");
    const double ML = 64, MR = 90, MT = 10, MB = 40;

    // simple viridis-like ramp (t=0 -> dark purple/blue, t=1 -> yellow)
    static readonly Color[] Ramp =
    {
        Color.FromRgb(68, 1, 84), Color.FromRgb(59, 82, 139), Color.FromRgb(33, 145, 140),
        Color.FromRgb(94, 201, 98), Color.FromRgb(253, 231, 37),
    };

    public static Color ColorAt(double t)
    {
        t = Math.Clamp(double.IsNaN(t) ? 0 : t, 0, 1) * (Ramp.Length - 1);
        int i = Math.Min((int)t, Ramp.Length - 2);
        double f = t - i;
        Color a = Ramp[i], b = Ramp[i + 1];
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
        var g = Grid;
        if (g?.Tvd == null || g.EastAxis == null || g.NorthAxis == null || g.EastAxis.Length == 0 || g.NorthAxis.Length == 0)
        {
            Text(dc, "No grid for the selected bed", w / 2, h / 2, 12, center: true);
            return;
        }
        int nE = g.EastAxis.Length, nN = g.NorthAxis.Length;
        double vmin = double.MaxValue, vmax = double.MinValue;
        foreach (var row in g.Tvd) foreach (var v in row) if (!double.IsNaN(v)) { vmin = Math.Min(vmin, v); vmax = Math.Max(vmax, v); }
        if (vmin > vmax) { Text(dc, "Grid has no values", w / 2, h / 2, 12, center: true); return; }
        double vr = Math.Max(vmax - vmin, 1e-9);

        double pw = w - ML - MR, ph = h - MT - MB;
        if (pw < 20 || ph < 20) return;
        // keep cells square in world units
        double e0 = g.EastAxis[0], e1 = g.EastAxis[^1], n0 = g.NorthAxis[0], n1 = g.NorthAxis[^1];
        double dE = nE > 1 ? (e1 - e0) / (nE - 1) : 1, dN = nN > 1 ? (n1 - n0) / (nN - 1) : 1;
        double worldW = (e1 - e0) + dE, worldH = (n1 - n0) + dN;
        double scale = Math.Min(pw / worldW, ph / worldH);
        double ox = ML, oy = MT + (ph - worldH * scale);
        double X(double e) => ox + (e - (e0 - dE / 2)) * scale;
        double Y(double nn) => oy + ((n1 + dN / 2) - nn) * scale;

        for (int r = 0; r < nN && r < g.Tvd.Length; r++)
            for (int c = 0; c < nE && c < g.Tvd[r].Length; c++)
            {
                double v = g.Tvd[r][c];
                // shallow (small TVD) = bright, deep = dark
                var brush = double.IsNaN(v) ? Brushes.LightGray : new SolidColorBrush(ColorAt(1 - (v - vmin) / vr));
                double x0 = X(g.EastAxis[c] - dE / 2), x1 = X(g.EastAxis[c] + dE / 2);
                double y0 = Y(g.NorthAxis[r] + dN / 2), y1 = Y(g.NorthAxis[r] - dN / 2);
                dc.DrawRectangle(brush, new Pen(Brushes.White, 0.5), new Rect(x0, y0, x1 - x0, y1 - y0));
                if (x1 - x0 > 46 && y1 - y0 > 16)
                    Text(dc, v.ToString("0.0", CultureInfo.InvariantCulture), (x0 + x1) / 2, (y0 + y1) / 2 - 7, 9,
                        center: true, brush: (1 - (v - vmin) / vr) > 0.55 ? Brushes.Black : Brushes.White);
            }

        // axes labels (node coordinates)
        for (int c = 0; c < nE; c++)
            Text(dc, g.EastAxis[c].ToString("0", CultureInfo.InvariantCulture), X(g.EastAxis[c]), Y(n0 - dN / 2) + 3, 9, center: true);
        for (int r = 0; r < nN; r++)
            Text(dc, g.NorthAxis[r].ToString("0", CultureInfo.InvariantCulture), ox - 4, Y(g.NorthAxis[r]) - 6, 9, right: true);
        Text(dc, "East", X((e0 + e1) / 2), Y(n0 - dN / 2) + 17, 11, center: true);
        Text(dc, "North", 4, oy - 2, 11);

        // legend
        double lx = ML + worldW * scale + 20, ly = oy, lh = Math.Min(worldH * scale, 220), lw = 16;
        for (int i = 0; i < 50; i++)
        {
            double t = i / 49.0;
            dc.DrawRectangle(new SolidColorBrush(ColorAt(1 - t)), null, new Rect(lx, ly + t * lh, lw, lh / 49.0 + 0.5));
        }
        dc.DrawRectangle(null, new Pen(Brushes.Gray, 1), new Rect(lx, ly, lw, lh));
        Text(dc, "TVD", lx, ly - 2 - 12, 10);
        Text(dc, vmin.ToString("0.0", CultureInfo.InvariantCulture), lx + lw + 4, ly - 6, 10);
        Text(dc, vmax.ToString("0.0", CultureInfo.InvariantCulture), lx + lw + 4, ly + lh - 8, 10);
    }

    void Text(DrawingContext dc, string s, double x, double y, double size, bool center = false, bool right = false, Brush? brush = null)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush ?? Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (center) x -= ft.Width / 2;
        else if (right) x -= ft.Width;
        dc.DrawText(ft, new Point(x, y));
    }
}
