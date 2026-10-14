using System.Windows;
using System.Windows.Media;
using GordonGeoSteer.Engine.Display;
using GordonGeoSteer.Engine.Steering;
using GordonGeoSteer.Engine.Workspace;
using GordonGeoSteer.ViewModels.Steering;

namespace GordonGeoSteer.Controls.Steering;

/// <summary>
/// Draws the curves of one track (vertical: TVT down the page, value across; horizontal: vertical
/// section across, value up) with line styles and the four shading modes. Works in screen space:
/// samples are mapped to pixels, culled to the visible window, then min/max decimated per pixel.
/// </summary>
public sealed class TrackPainter
{
    readonly SteeringViewModel _vm;
    readonly TrackViewModel _track;
    readonly Rect _plot;
    readonly bool _vertical;
    readonly AxisRange _axis;

    public TrackPainter(SteeringViewModel vm, TrackViewModel track, Rect plot)
    {
        _vm = vm; _track = track; _plot = plot; _vertical = track.IsVertical;
        _axis = _vertical ? vm.TvtRange : vm.XRange;
    }

    double AlongOrigin => _vertical ? _plot.Top : _plot.Left;
    double AlongExtent => _vertical ? _plot.Height : _plot.Width;

    /// <summary>Pixel along the depth/position axis.</summary>
    public double Along(double a) => _axis.ToPixel(a, AlongOrigin, AlongExtent);
    public double AxisAt(double pixel) => _axis.FromPixel(pixel, AlongOrigin, AlongExtent);

    /// <summary>Pixel across the value axis (vertical tracks: left = scale min; horizontal: bottom = scale min).</summary>
    public double Across(double v)
    {
        double f = _track.Fraction(v);
        if (double.IsNaN(f)) return double.NaN;
        return _vertical ? _plot.Left + f * _plot.Width : _plot.Bottom - f * _plot.Height;
    }

    double AcrossMin => _vertical ? _plot.Left : _plot.Bottom;
    double AcrossMax => _vertical ? _plot.Right : _plot.Top;

    Point Pt(double along, double across) => _vertical ? new Point(across, along) : new Point(along, across);

    /// <summary>Screen-space, culled and decimated segments of a series.</summary>
    public List<List<Decimator.P>> Segments(AxisSeries s)
    {
        int n = s.Count;
        var al = new double[n];
        var ac = new double[n];
        double lo = AlongOrigin - 40, hi = AlongOrigin + AlongExtent + 40;
        // values far outside the track are clamped so lines leave the track cleanly instead of overflowing geometry
        double acLo = Math.Min(AcrossMin, AcrossMax) - 4000, acHi = Math.Max(AcrossMin, AcrossMax) + 4000;
        for (int i = 0; i < n; i++)
        {
            al[i] = Along(s.Axis[i]);
            double c = Across(s.Value[i]);
            ac[i] = double.IsNaN(c) ? double.NaN : Math.Clamp(c, acLo, acHi);
        }
        // cull: keep a sample if it or a neighbour is inside the window (so lines enter/leave the edge correctly)
        var keep = new bool[n];
        for (int i = 0; i < n; i++)
        {
            bool inside = al[i] >= lo && al[i] <= hi;
            if (inside) { keep[i] = true; if (i > 0) keep[i - 1] = true; if (i + 1 < n) keep[i + 1] = true; }
        }
        for (int i = 0; i < n; i++) if (!keep[i]) al[i] = double.NaN;
        return Decimator.Decimate(al, ac);
    }

    public void Paint(DrawingContext dc)
    {
        dc.PushClip(new RectangleGeometry(_plot));
        var visible = _track.Curves.Where(c => c.Visible && !c.Missing).ToList();
        var cache = new Dictionary<TrackCurveViewModel, (AxisSeries s, List<List<Decimator.P>> seg)>();
        foreach (var c in visible)
        {
            var s = _vm.GetSeries(c.Source, _vertical);
            if (s != null) cache[c] = (s, Segments(s));
        }
        // shading first, curves on top
        foreach (var c in visible)
        {
            if (!cache.TryGetValue(c, out var d) || c.ShadeMode == ShadingMode.None) continue;
            switch (c.ShadeMode)
            {
                case ShadingMode.ToTrackMin: FillToEdge(dc, d.seg, AcrossMin, Draw.Brush(c.ShadeColor, c.ShadeOpacity)); break;
                case ShadingMode.ToTrackMax: FillToEdge(dc, d.seg, AcrossMax, Draw.Brush(c.ShadeColor, c.ShadeOpacity)); break;
                case ShadingMode.Gradient: FillGradient(dc, d.seg, c.Ramp, c.ShadeOpacity); break;
                case ShadingMode.ToCurve:
                    var other = _track.Curves.FirstOrDefault(o => o.Source == c.ShadeOther && o != c);
                    var os = other == null ? null : _vm.GetSeries(other.Source, _vertical);
                    if (os != null) FillToCurve(dc, d.seg, os, Draw.Brush(c.ShadeColor, c.ShadeOpacity));
                    break;
            }
        }
        foreach (var c in visible)
        {
            if (!cache.TryGetValue(c, out var d)) continue;
            var pen = Draw.Pen(c.Color, c.Thickness, c.LineStyle);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                foreach (var seg in d.seg)
                {
                    if (seg.Count == 0) continue;
                    ctx.BeginFigure(Pt(seg[0].Along, seg[0].Across), false, false);
                    if (seg.Count == 1) { ctx.LineTo(Pt(seg[0].Along + 0.01, seg[0].Across), true, false); continue; }
                    for (int i = 1; i < seg.Count; i++) ctx.LineTo(Pt(seg[i].Along, seg[i].Across), true, false);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
        dc.Pop();
    }

    void FillToEdge(DrawingContext dc, List<List<Decimator.P>> segs, double edge, Brush brush)
    {
        var g = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = g.Open())
        {
            foreach (var seg in segs)
            {
                if (seg.Count < 2) continue;
                ctx.BeginFigure(Pt(seg[0].Along, edge), true, true);
                foreach (var p in seg) ctx.LineTo(Pt(p.Along, p.Across), false, false);
                ctx.LineTo(Pt(seg[^1].Along, edge), false, false);
            }
        }
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
    }

    /// <summary>Colour-ramp fill from the track minimum to the curve; the colour of each slice follows the curve value across the track scale.</summary>
    void FillGradient(DrawingContext dc, List<List<Decimator.P>> segs, ColorRampKind ramp, double opacity)
    {
        const int Bins = 48;
        var geoms = new StreamGeometry[Bins];
        var ctxs = new StreamGeometryContext?[Bins];
        double edge = AcrossMin;
        double span = AcrossMax - AcrossMin;
        try
        {
            foreach (var seg in segs)
            {
                for (int i = 0; i + 1 < seg.Count; i++)
                {
                    var a = seg[i]; var b = seg[i + 1];
                    double f = ((a.Across + b.Across) / 2 - edge) / span;
                    int bin = (int)Math.Clamp(Math.Floor(Math.Clamp(f, 0, 1) * Bins), 0, Bins - 1);
                    if (ctxs[bin] == null) { geoms[bin] = new StreamGeometry(); ctxs[bin] = geoms[bin].Open(); }
                    var ctx = ctxs[bin]!;
                    // widen by half a pixel so adjacent slices overlap (no hairline gaps)
                    double a0 = a.Along, a1 = b.Along;
                    if (Math.Abs(a1 - a0) < 1) { double m = (a0 + a1) / 2; a0 = m - 0.5; a1 = m + 0.5; }
                    ctx.BeginFigure(Pt(a0, edge), true, true);
                    ctx.LineTo(Pt(a0, a.Across), false, false);
                    ctx.LineTo(Pt(a1, b.Across), false, false);
                    ctx.LineTo(Pt(a1, edge), false, false);
                }
            }
        }
        finally
        {
            foreach (var c in ctxs) c?.Close();
        }
        for (int k = 0; k < Bins; k++)
        {
            if (geoms[k] == null) continue;
            geoms[k].Freeze();
            var col = Draw.Ramp(ramp, (k + 0.5) / Bins);
            col.A = (byte)(255 * Math.Clamp(opacity, 0, 1));
            dc.DrawGeometry(Draw.Brush(col), null, geoms[k]);
        }
    }

    /// <summary>Fill between this curve and another curve in the track, evaluated at this curve's (decimated) sample positions.</summary>
    void FillToCurve(DrawingContext dc, List<List<Decimator.P>> segs, AxisSeries other, Brush brush)
    {
        var g = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = g.Open())
        {
            foreach (var seg in segs)
            {
                // split the segment wherever the other curve has no value
                var run = new List<(Decimator.P a, double b)>();
                void Flush()
                {
                    if (run.Count >= 2)
                    {
                        ctx.BeginFigure(Pt(run[0].a.Along, run[0].a.Across), true, true);
                        for (int i = 1; i < run.Count; i++) ctx.LineTo(Pt(run[i].a.Along, run[i].a.Across), false, false);
                        for (int i = run.Count - 1; i >= 0; i--) ctx.LineTo(Pt(run[i].a.Along, run[i].b), false, false);
                    }
                    run.Clear();
                }
                foreach (var p in seg)
                {
                    double ob = Across(other.ValueAt(AxisAt(p.Along)));
                    if (double.IsNaN(ob)) { Flush(); continue; }
                    run.Add((p, ob));
                }
                Flush();
            }
        }
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
    }

    /// <summary>Scale grid lines across the value axis (linear: 4 divisions; log: decades and 2/5).</summary>
    public void PaintValueGrid(DrawingContext dc)
    {
        var (a, b) = _track.EffectiveScale();
        IEnumerable<double> vals;
        if (_track.LogScale)
        {
            var l = new List<double>();
            for (double d = Math.Floor(Math.Log10(a)); d <= Math.Ceiling(Math.Log10(b)); d++)
                foreach (var m in new[] { 1.0, 2, 5 }) { double v = m * Math.Pow(10, d); if (v > a && v < b) l.Add(v); }
            vals = l;
        }
        else vals = Enumerable.Range(1, 3).Select(i => a + i * (b - a) / 4);
        foreach (var v in vals)
        {
            double p = Across(v);
            bool major = !_track.LogScale || Math.Abs(Math.Log10(v) - Math.Round(Math.Log10(v))) < 1e-9;
            var pen = major ? Draw.GridPen : Draw.GridPenMinor;
            if (_vertical) dc.DrawLine(pen, new Point(p, _plot.Top), new Point(p, _plot.Bottom));
            else dc.DrawLine(pen, new Point(_plot.Left, p), new Point(_plot.Right, p));
        }
    }
}
