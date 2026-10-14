using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GordonGeoSteer.Engine.Workspace;

namespace GordonGeoSteer.Controls.Steering;

/// <summary>Shared drawing helpers for the steering view (pens, brushes, colour ramps, ticks, text).</summary>
public static class Draw
{
    public static readonly Typeface Face = new("Segoe UI");
    public static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    static readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<(string, double, LineStyleKind), Pen> _pens = new();

    public static Color ParseColor(string? s, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(s.Trim()); }
        catch { return fallback; }
    }

    public static SolidColorBrush Brush(string? hex, double opacity = 1)
    {
        string key = (hex ?? "") + "|" + opacity.ToString("0.###", CultureInfo.InvariantCulture);
        lock (_brushes)
        {
            if (_brushes.TryGetValue(key, out var b)) return b;
            var c = ParseColor(hex, Colors.Black);
            c.A = (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1) * c.A / 255.0);
            b = new SolidColorBrush(c);
            b.Freeze();
            return _brushes[key] = b;
        }
    }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Pen for a curve or bed line. Dash patterns are in units of pen thickness, with a floor so thin lines stay readable.</summary>
    public static Pen Pen(string? hex, double thickness, LineStyleKind style = LineStyleKind.Solid)
    {
        var key = (hex ?? "", Math.Round(thickness, 3), style);
        lock (_pens)
        {
            if (_pens.TryGetValue(key, out var p)) return p;
            double k = Math.Max(1.6, 3.2 / Math.Max(0.5, thickness)); // longer relative dashes for thin lines
            p = new Pen(Brush(hex), thickness) { LineJoin = PenLineJoin.Round };
            switch (style)
            {
                case LineStyleKind.Dashed: p.DashStyle = new DashStyle(new[] { 4.0 * k, 2.5 * k }, 0); p.DashCap = PenLineCap.Flat; break;
                case LineStyleKind.Dotted: p.DashStyle = new DashStyle(new[] { 0.0, Math.Max(2.5, 3.5 / Math.Max(0.5, thickness)) }, 0); p.DashCap = PenLineCap.Round; p.StartLineCap = p.EndLineCap = PenLineCap.Round; break;
                case LineStyleKind.DotDash: p.DashStyle = new DashStyle(new[] { 5.0 * k, 2.0 * k, 0.0, 2.0 * k }, 0); p.DashCap = PenLineCap.Round; break;
            }
            p.Freeze();
            return _pens[key] = p;
        }
    }

    public static Pen Pen(Color c, double thickness)
    {
        var p = new Pen(Brush(c), thickness);
        p.Freeze();
        return p;
    }

    // ------------------------------------------------------------------ colour ramps

    static readonly Dictionary<ColorRampKind, Color[]> RampStops = new()
    {
        // light sand -> tan -> brown -> dark brown (low GR = clean = light, high GR = shale = dark)
        [ColorRampKind.Earth] = new[] { C("#FFF8E6"), C("#F2D49B"), C("#D9A55B"), C("#A8692C"), C("#5C3311") },
        [ColorRampKind.Rainbow] = new[] { C("#3B4CC0"), C("#2CA3DB"), C("#43BF71"), C("#F2D03B"), C("#E8742C"), C("#C0282A") },
        [ColorRampKind.Grayscale] = new[] { C("#FFFFFF"), C("#202020") },
        [ColorRampKind.Heat] = new[] { C("#FFFFCC"), C("#FED976"), C("#FD8D3C"), C("#E31A1C"), C("#800026") },
        [ColorRampKind.Viridis] = new[] { C("#440154"), C("#3B528B"), C("#21918C"), C("#5EC962"), C("#FDE725") },
    };

    static Color C(string s) => (Color)ColorConverter.ConvertFromString(s);

    public static Color Ramp(ColorRampKind k, double f)
    {
        var st = RampStops[k];
        if (double.IsNaN(f)) f = 0;
        f = Math.Clamp(f, 0, 1) * (st.Length - 1);
        int i = Math.Min((int)f, st.Length - 2);
        double t = f - i;
        Color a = st[i], b = st[i + 1];
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    // ------------------------------------------------------------------ ticks / ranges

    public static IEnumerable<double> Ticks(double min, double max, int target)
    {
        double range = max - min;
        if (!(range > 0)) yield break;
        double raw = range / Math.Max(1, target);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
        for (double t = Math.Ceiling(min / step) * step; t <= max + step * 1e-9; t += step) yield return Math.Abs(t) < step * 1e-9 ? 0 : t;
    }

    public static double TickStep(double min, double max, int target)
    {
        var t = Ticks(min, max, target).Take(2).ToArray();
        return t.Length == 2 ? t[1] - t[0] : max - min;
    }

    public static (double min, double max) NiceRange(double lo, double hi)
    {
        if (!(hi > lo)) return (lo - 1, lo + 1);
        double step = TickStep(lo, hi, 5);
        return (Math.Floor(lo / step) * step, Math.Ceiling(hi / step) * step);
    }

    public static string Fmt(double v)
    {
        double a = Math.Abs(v);
        string f = a >= 1000 ? "#,0" : a >= 100 ? "0" : a >= 10 ? "0.#" : a >= 1 ? "0.##" : "0.###";
        return v.ToString(f, CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ text

    public enum Align { Left, Center, Right }

    public static FormattedText Format(string s, double size, Brush brush, double pixelsPerDip, bool bold = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, brush, pixelsPerDip);

    public static Size Text(DrawingContext dc, string s, double x, double y, double size, Brush brush, double pixelsPerDip,
                            Align align = Align.Left, bool bold = false, Brush? background = null)
    {
        var ft = Format(s, size, brush, pixelsPerDip, bold);
        if (align == Align.Center) x -= ft.Width / 2;
        else if (align == Align.Right) x -= ft.Width;
        if (background != null) dc.DrawRoundedRectangle(background, null, new Rect(x - 2, y - 1, ft.Width + 4, ft.Height + 2), 2, 2);
        dc.DrawText(ft, new Point(x, y));
        return new Size(ft.Width, ft.Height);
    }

    public static readonly SolidColorBrush LabelBack = Brush(Color.FromArgb(215, 255, 255, 255));
    public static readonly SolidColorBrush AxisText = Brush(Color.FromRgb(50, 50, 50));
    public static readonly Pen GridPen = Pen(Color.FromRgb(228, 228, 228), 1);
    public static readonly Pen GridPenMinor = Pen(Color.FromRgb(242, 242, 242), 1);
    public static readonly Pen FramePen = Pen(Color.FromRgb(140, 140, 140), 1);
    public static readonly Pen SelectedPen = Pen(Color.FromRgb(30, 120, 220), 2);
}
