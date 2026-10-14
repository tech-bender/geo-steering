namespace GordonGeoSteer.Engine.Steering;

/// <summary>
/// A log curve as (depth, value) sorted by increasing depth. Null samples (NaN, +/-Inf, -999.25) are
/// stored as NaN and break interpolation.
/// </summary>
public sealed class DepthSeries
{
    public const double LasNull = -999.25;

    public double[] Depth { get; }
    public double[] Value { get; }
    public int Count => Depth.Length;

    public DepthSeries(double[] depth, double[] value)
    {
        Depth = depth; Value = value;
    }

    public static bool IsNull(double v) => double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v - LasNull) < 1e-9;

    /// <summary>Builds a sorted series from raw columns (any order; rows with a null depth are dropped).</summary>
    public static DepthSeries FromColumns(IReadOnlyList<double> depth, IReadOnlyList<double> value)
    {
        int n = Math.Min(depth.Count, value.Count);
        var idx = Enumerable.Range(0, n).Where(i => !IsNull(depth[i])).ToArray();
        bool sorted = true;
        for (int k = 1; k < idx.Length; k++) if (depth[idx[k]] < depth[idx[k - 1]]) { sorted = false; break; }
        if (!sorted) idx = idx.OrderBy(i => depth[i]).ToArray();
        var d = new double[idx.Length];
        var v = new double[idx.Length];
        for (int k = 0; k < idx.Length; k++)
        {
            d[k] = depth[idx[k]];
            double x = value[idx[k]];
            v[k] = IsNull(x) ? double.NaN : x;
        }
        return new DepthSeries(d, v);
    }

    public double MinDepth => Count == 0 ? double.NaN : Depth[0];
    public double MaxDepth => Count == 0 ? double.NaN : Depth[^1];

    /// <summary>Linear interpolation at depth d; NaN outside the range or next to a null sample.</summary>
    public double ValueAt(double d)
    {
        int n = Depth.Length;
        if (n == 0 || double.IsNaN(d) || d < Depth[0] || d > Depth[n - 1]) return double.NaN;
        int i = Array.BinarySearch(Depth, d);
        if (i >= 0) return Value[i];
        i = ~i;
        double d0 = Depth[i - 1], d1 = Depth[i];
        double v0 = Value[i - 1], v1 = Value[i];
        if (double.IsNaN(v0) || double.IsNaN(v1)) return double.NaN;
        if (d1 - d0 <= 0) return v0;
        return v0 + (v1 - v0) * (d - d0) / (d1 - d0);
    }

    public (double min, double max) ValueRange()
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var v in Value) if (!double.IsNaN(v)) { if (v < lo) lo = v; if (v > hi) hi = v; }
        return lo <= hi ? (lo, hi) : (double.NaN, double.NaN);
    }
}

/// <summary>
/// A curve positioned along some axis (TVT for vertical tracks, vertical section for horizontal tracks).
/// Axis is in sample order (NOT necessarily monotone: realtime data plotted against TVT can go up,
/// down or stack on itself). NaN in either array breaks the line.
/// </summary>
public sealed class AxisSeries
{
    public double[] Axis { get; }
    public double[] Value { get; }
    /// <summary>True when Axis is non-decreasing (lets curve-to-curve shading interpolate directly).</summary>
    public bool AxisMonotone { get; }

    public AxisSeries(double[] axis, double[] value)
    {
        Axis = axis; Value = value;
        bool mono = true;
        double prev = double.NegativeInfinity;
        foreach (var a in axis)
        {
            if (double.IsNaN(a)) continue;
            if (a < prev) { mono = false; break; }
            prev = a;
        }
        AxisMonotone = mono;
    }

    public int Count => Axis.Length;

    /// <summary>Value at an axis position (linear interpolation over a by-axis sorted copy). NaN if unavailable.</summary>
    public double ValueAt(double a)
    {
        var s = Sorted();
        return s.ValueAt(a);
    }

    DepthSeries? _sorted;
    public DepthSeries Sorted()
    {
        if (_sorted != null) return _sorted;
        var ok = Enumerable.Range(0, Axis.Length).Where(i => !double.IsNaN(Axis[i])).ToArray();
        return _sorted = DepthSeries.FromColumns(ok.Select(i => Axis[i]).ToArray(), ok.Select(i => Value[i]).ToArray());
    }
}
