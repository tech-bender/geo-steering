namespace GordonGeoSteer.Engine.Display;

/// <summary>
/// One zoomable/pannable axis (data range shown across a pixel extent). Each axis of each plot is a
/// separate instance, which is what makes horizontal and vertical zoom independent; plots that must
/// stay aligned (curtain x and horizontal-track x) share one instance.
/// </summary>
public sealed class AxisRange
{
    double _min, _max;

    public AxisRange(double min, double max) { _min = min; _max = max; HomeMin = min; HomeMax = max; }

    public double Min => _min;
    public double Max => _max;
    public double Span => _max - _min;
    public double HomeMin { get; private set; }
    public double HomeMax { get; private set; }

    /// <summary>Smallest allowed span (prevents zooming into numerical noise).</summary>
    public double MinSpan { get; set; } = 1e-3;
    /// <summary>Largest allowed span.</summary>
    public double MaxSpan { get; set; } = 1e7;

    public event EventHandler? Changed;

    public void Set(double min, double max)
    {
        if (double.IsNaN(min) || double.IsNaN(max)) return;
        if (max < min) (min, max) = (max, min);
        double span = Math.Clamp(max - min, MinSpan, MaxSpan);
        double c = (min + max) / 2;
        if (max - min < MinSpan || max - min > MaxSpan) { min = c - span / 2; max = c + span / 2; }
        if (min == _min && max == _max) return;
        _min = min; _max = max;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the "home" (reset) range and optionally applies it.</summary>
    public void SetHome(double min, double max, bool apply = true)
    {
        HomeMin = Math.Min(min, max); HomeMax = Math.Max(min, max);
        if (apply) Set(HomeMin, HomeMax);
    }

    public void Reset() => Set(HomeMin, HomeMax);

    /// <summary>Zooms by <paramref name="factor"/> (&gt;1 zooms in) keeping the data value <paramref name="anchor"/> fixed on screen.</summary>
    public void ZoomAt(double anchor, double factor)
    {
        if (factor <= 0 || double.IsNaN(anchor)) return;
        double newSpan = Math.Clamp(Span / factor, MinSpan, MaxSpan);
        double f = Span == 0 ? 0.5 : (anchor - _min) / Span;
        Set(anchor - f * newSpan, anchor - f * newSpan + newSpan);
    }

    /// <summary>Zooms about the centre of the range.</summary>
    public void Zoom(double factor) => ZoomAt((_min + _max) / 2, factor);

    public void Pan(double deltaData) => Set(_min + deltaData, _max + deltaData);

    /// <summary>Pixel position of a value along an extent of <paramref name="pixels"/> starting at <paramref name="origin"/>; inverted = value grows toward smaller pixels.</summary>
    public double ToPixel(double v, double origin, double pixels, bool inverted = false)
    {
        double f = (v - _min) / (Span == 0 ? 1 : Span);
        return inverted ? origin + pixels - f * pixels : origin + f * pixels;
    }

    public double FromPixel(double p, double origin, double pixels, bool inverted = false)
    {
        double f = (p - origin) / (pixels == 0 ? 1 : pixels);
        if (inverted) f = 1 - f;
        return _min + f * Span;
    }

    /// <summary>Converts a pixel delta to a data delta (sign follows the axis direction).</summary>
    public double PixelsToData(double dp, double pixels, bool inverted = false) => (inverted ? -dp : dp) * Span / (pixels == 0 ? 1 : pixels);

    public override string ToString() => $"[{_min:0.###} .. {_max:0.###}]";
}

/// <summary>Which axes a mouse-wheel gesture zooms (the curtain's gesture map).</summary>
public static class ZoomGestures
{
    /// <summary>Wheel = both axes about the cursor; Ctrl+wheel = horizontal only; Shift+wheel = vertical only.</summary>
    public static (bool x, bool y) WheelAxes(bool ctrl, bool shift) => (ctrl, shift) switch
    {
        (true, false) => (true, false),
        (false, true) => (false, true),
        _ => (true, true),
    };

    /// <summary>Zoom factor per wheel notch (120 units).</summary>
    public const double WheelStep = 1.2;

    public static double WheelFactor(int delta) => Math.Pow(WheelStep, delta / 120.0);

    /// <summary>Applies a wheel gesture at data point (ax, ay) to the two axes.</summary>
    public static void ApplyWheel(AxisRange x, AxisRange y, double ax, double ay, int delta, bool ctrl, bool shift)
    {
        var (zx, zy) = WheelAxes(ctrl, shift);
        double f = WheelFactor(delta);
        if (zx) x.ZoomAt(ax, f);
        if (zy) y.ZoomAt(ay, f);
    }

    /// <summary>Drag along an axis band zooms that axis: dragging by +pixels (right / up) zooms in, exponentially.</summary>
    public static double AxisDragFactor(double pixels) => Math.Exp(pixels / 150.0);
}
