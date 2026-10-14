using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GordonGeoSteer.Controls;

/// <summary>
/// Minimal WPF-native MD-vs-value plot: MD on the horizontal axis, value on the vertical axis
/// (optionally log10), null samples (NaN / -999.25) break the line. Drawn with a single StreamGeometry.
/// </summary>
public sealed class CurvePlot : FrameworkElement
{
    public static readonly DependencyProperty XValuesProperty = Reg<IReadOnlyList<double>?>(nameof(XValues), null);
    public static readonly DependencyProperty YValuesProperty = Reg<IReadOnlyList<double>?>(nameof(YValues), null);
    public static readonly DependencyProperty LogScaleProperty = Reg(nameof(LogScale), false);
    public static readonly DependencyProperty XLabelProperty = Reg(nameof(XLabel), "MD");
    public static readonly DependencyProperty YLabelProperty = Reg(nameof(YLabel), "Value");

    static DependencyProperty Reg<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(CurvePlot),
            new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? XValues { get => (IReadOnlyList<double>?)GetValue(XValuesProperty); set => SetValue(XValuesProperty, value); }
    public IReadOnlyList<double>? YValues { get => (IReadOnlyList<double>?)GetValue(YValuesProperty); set => SetValue(YValuesProperty, value); }
    public bool LogScale { get => (bool)GetValue(LogScaleProperty); set => SetValue(LogScaleProperty, value); }
    public string XLabel { get => (string)GetValue(XLabelProperty); set => SetValue(XLabelProperty, value); }
    public string YLabel { get => (string)GetValue(YLabelProperty); set => SetValue(YLabelProperty, value); }

    static readonly Typeface Face = new("Segoe UI");
    static readonly Pen AxisPen = Frozen(new Pen(Brushes.Black, 1));
    static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(225, 225, 225)), 1));
    static readonly Pen LinePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 90, 170)), 1));
    static Pen Frozen(Pen p) { p.Freeze(); return p; }

    const double ML = 70, MR = 16, MT = 12, MB = 46;

    static bool IsNull(double v) => double.IsNaN(v) || double.IsInfinity(v) || v == -999.25;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
        var xs = XValues; var ys = YValues;
        double pw = w - ML - MR, ph = h - MT - MB;
        if (xs == null || ys == null || xs.Count == 0 || pw < 20 || ph < 20)
        {
            Text(dc, "No data", w / 2, h / 2, 12, center: true);
            return;
        }
        bool log = LogScale;
        double ty(double v) => log ? Math.Log10(v) : v;

        double xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
        int n = Math.Min(xs.Count, ys.Count);
        for (int i = 0; i < n; i++)
        {
            double x = xs[i], y = ys[i];
            if (IsNull(x) || IsNull(y) || (log && y <= 0)) continue;
            double yy = ty(y);
            if (x < xmin) xmin = x; if (x > xmax) xmax = x;
            if (yy < ymin) ymin = yy; if (yy > ymax) ymax = yy;
        }
        if (xmin > xmax) { Text(dc, "All samples are null", w / 2, h / 2, 12, center: true); return; }
        if (xmax - xmin < 1e-9) { xmin -= 1; xmax += 1; }
        if (ymax - ymin < 1e-9) { ymin -= 1; ymax += 1; }
        double pad = (ymax - ymin) * 0.04; ymin -= pad; ymax += pad;

        double X(double x) => ML + (x - xmin) / (xmax - xmin) * pw;
        double Y(double yy) => MT + ph - (yy - ymin) / (ymax - ymin) * ph;

        // grid + ticks
        foreach (double t in Ticks(xmin, xmax, 8))
        {
            double px = X(t);
            dc.DrawLine(GridPen, new Point(px, MT), new Point(px, MT + ph));
            Text(dc, t.ToString("0.##", CultureInfo.InvariantCulture), px, MT + ph + 4, 10, center: true);
        }
        IEnumerable<double> yt = log ? LogTicks(ymin, ymax) : Ticks(ymin, ymax, 6);
        foreach (double t in yt)
        {
            double py = Y(t);
            dc.DrawLine(GridPen, new Point(ML, py), new Point(ML + pw, py));
            double label = log ? Math.Pow(10, t) : t;
            Text(dc, label.ToString(Math.Abs(label) < 1 && label != 0 ? "0.###" : "0.##", CultureInfo.InvariantCulture), ML - 4, py - 7, 10, right: true);
        }
        dc.DrawRectangle(null, AxisPen, new Rect(ML, MT, pw, ph));

        // data
        dc.PushClip(new RectangleGeometry(new Rect(ML, MT, pw, ph)));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            bool open = false;
            for (int i = 0; i < n; i++)
            {
                double x = xs[i], y = ys[i];
                if (IsNull(x) || IsNull(y) || (log && y <= 0)) { open = false; continue; }
                var p = new Point(X(x), Y(ty(y)));
                if (!open) { ctx.BeginFigure(p, false, false); open = true; }
                else ctx.LineTo(p, true, false);
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, LinePen, g);
        dc.Pop();

        Text(dc, XLabel, ML + pw / 2, h - 18, 11, center: true);
        dc.PushTransform(new RotateTransform(-90, 12, MT + ph / 2));
        Text(dc, YLabel + (log ? " (log)" : ""), 12, MT + ph / 2 - 8, 11, center: true);
        dc.Pop();
    }

    void Text(DrawingContext dc, string s, double x, double y, double size, bool center = false, bool right = false)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (center) x -= ft.Width / 2;
        else if (right) x -= ft.Width;
        dc.DrawText(ft, new Point(x, y));
    }

    public static IEnumerable<double> Ticks(double min, double max, int target)
    {
        double range = max - min;
        double raw = range / Math.Max(1, target);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
        for (double t = Math.Ceiling(min / step) * step; t <= max + step * 1e-9; t += step) yield return t;
    }

    static IEnumerable<double> LogTicks(double min, double max)
    {
        for (double d = Math.Floor(min); d <= Math.Ceiling(max); d++)
            foreach (double m in new[] { 1.0, 2.0, 5.0 })
            {
                double t = d + Math.Log10(m);
                if (t >= min && t <= max) yield return t;
            }
    }
}
