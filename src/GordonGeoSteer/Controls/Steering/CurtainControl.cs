using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GordonGeoSteer.Engine.Display;
using GordonGeoSteer.Engine.Steering;
using GordonGeoSteer.ViewModels.Steering;

namespace GordonGeoSteer.Controls.Steering;

/// <summary>
/// Curtain section: x = vertical section (shared with the horizontal tracks), y = TVD. Draws imported and
/// user bed boundaries along the well's structure, the wellbore and survey stations, geomod picks.
///
/// Mouse: wheel = zoom both axes at the cursor; Ctrl+wheel = horizontal only; Shift+wheel = vertical
/// only; drag in the plot = pan; drag on the left (TVD) axis = zoom TVD; drag on the bottom (VS) axis =
/// zoom VS; double-click = reset. The x and y axes are independent <see cref="AxisRange"/> objects.
/// </summary>
public sealed class CurtainControl : SteeringElement
{
    public const double LeftMargin = 92, RightMargin = 12, TopMargin = 18, BottomMargin = 34;

    public Rect PlotRect => new(LeftMargin, TopMargin, Math.Max(1, ActualWidth - LeftMargin - RightMargin), Math.Max(1, ActualHeight - TopMargin - BottomMargin));

    static readonly Pen WellPen = Draw.Pen("#16206B", 2.6);
    static readonly Pen WellHalo = Draw.Pen(Color.FromArgb(160, 255, 255, 255), 5);
    static readonly SolidColorBrush StationBrush = Draw.Brush("#16206B");
    static readonly SolidColorBrush Background = Draw.Brush("#FBF8F1");

    double X(double vs) => Steering!.XRange.ToPixel(vs, PlotRect.Left, PlotRect.Width);
    double Y(double tvd) => Steering!.TvdRange.ToPixel(tvd, PlotRect.Top, PlotRect.Height);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var vm = Steering;
        if (vm == null || ActualWidth < 50 || ActualHeight < 50) return;
        var plot = PlotRect;
        double dip = Dip;
        dc.DrawRectangle(Background, null, plot);

        // ---- axis grid
        var xt = Draw.Ticks(vm.XRange.Min, vm.XRange.Max, (int)Math.Max(3, plot.Width / 90)).ToList();
        var yt = Draw.Ticks(vm.TvdRange.Min, vm.TvdRange.Max, (int)Math.Max(3, plot.Height / 40)).ToList();
        foreach (var tx in xt) dc.DrawLine(Draw.GridPen, new Point(X(tx), plot.Top), new Point(X(tx), plot.Bottom));
        foreach (var ty in yt) dc.DrawLine(Draw.GridPen, new Point(plot.Left, Y(ty)), new Point(plot.Right, Y(ty)));

        dc.PushClip(new RectangleGeometry(plot));
        var curtain = vm.Curtain;
        var (i0, i1) = VisibleRange(curtain.X, vm.XRange.Min, vm.XRange.Max);
        int stride = Math.Max(1, (int)((i1 - i0) / Math.Max(1, plot.Width / 2)));

        // ---- bed lines (imported along the DI model, user beds parallel to the reference bed)
        var lines = new List<(BedBoundaryViewModel bed, double[] depth)>();
        foreach (var b in vm.Beds.Where(b => b.Visible))
        {
            double[]? d = b.IsUser ? vm.UserBedDepths(b) : curtain.Beds.GetValueOrDefault(b.Name);
            if (d != null) lines.Add((b, d));
        }
        int mid = (i0 + i1) / 2;
        lines.Sort((a, b) => a.depth[mid].CompareTo(b.depth[mid]));

        // light fills between consecutive grid beds / user beds give the "layered" look
        for (int k = 0; k + 1 < lines.Count; k++)
        {
            var top = lines[k]; var bot = lines[k + 1];
            if (!(top.bed.IsUser || top.bed.IsGridBed)) continue;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(X(curtain.X[i0]), Y(top.depth[i0])), true, true);
                for (int i = i0; i <= i1; i += stride) ctx.LineTo(new Point(X(curtain.X[i]), Y(top.depth[i])), false, false);
                ctx.LineTo(new Point(X(curtain.X[i1]), Y(top.depth[i1])), false, false);
                ctx.LineTo(new Point(X(curtain.X[i1]), Y(bot.depth[i1])), false, false);
                for (int i = i1; i >= i0; i -= stride) ctx.LineTo(new Point(X(curtain.X[i]), Y(bot.depth[i])), false, false);
                ctx.LineTo(new Point(X(curtain.X[i0]), Y(bot.depth[i0])), false, false);
            }
            g.Freeze();
            dc.DrawGeometry(Draw.Brush(top.bed.Color, 0.10), null, g);
        }
        foreach (var (bed, depth) in lines)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(X(curtain.X[i0]), Y(depth[i0])), false, false);
                for (int i = i0 + stride; i <= i1; i += stride) ctx.LineTo(new Point(X(curtain.X[i]), Y(depth[i])), true, false);
                ctx.LineTo(new Point(X(curtain.X[i1]), Y(depth[i1])), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, Draw.Pen(bed.Color, bed.Thickness), g);
        }

        // ---- wellbore
        var t = vm.Model.Table;
        var wx = new double[t.Count]; var wy = new double[t.Count];
        for (int i = 0; i < t.Count; i++)
        {
            bool ok = t.Md[i] <= vm.Model.Lateral.LastMd + 1e-6;
            wx[i] = ok ? X(t.Vs[i]) : double.NaN;
            wy[i] = ok ? Y(t.Tvd[i]) : double.NaN;
        }
        var segs = Decimator.Decimate(wx, wy);
        var wg = new StreamGeometry();
        using (var ctx = wg.Open())
            foreach (var s in segs)
            {
                if (s.Count < 2) continue;
                ctx.BeginFigure(new Point(s[0].Along, s[0].Across), false, false);
                for (int i = 1; i < s.Count; i++) ctx.LineTo(new Point(s[i].Along, s[i].Across), true, true);
            }
        wg.Freeze();
        dc.DrawGeometry(null, WellHalo, wg);
        dc.DrawGeometry(null, WellPen, wg);
        double lastX = double.NegativeInfinity;
        foreach (var st in vm.Model.Lateral.Stations)
        {
            double x = X(vm.Model.Vs(st.North, st.East)), y = Y(st.Tvd);
            if (x - lastX < 6 || x < plot.Left - 5 || x > plot.Right + 5) continue;
            dc.DrawEllipse(StationBrush, null, new Point(x, y), 2.2, 2.2);
            lastX = x;
        }

        // ---- bed labels (left side of the visible window, staggered to avoid overlaps)
        var used = new List<Rect>();
        foreach (var (bed, depth) in lines)
        {
            if (!bed.ShowLabel) continue;
            int li = Math.Clamp(i0 + (int)((i1 - i0) * 0.02), i0, i1);
            double y = Y(depth[li]);
            if (y < plot.Top + 2 || y > plot.Bottom - 2) continue;
            var ft = Draw.Format(bed.Name, 9.5, Draw.Brush(bed.Color), dip, bold: true);
            var r = new Rect(plot.Left + 6, y - ft.Height - 1, ft.Width + 6, ft.Height + 2);
            if (used.Any(u => u.IntersectsWith(r))) continue;
            used.Add(r);
            dc.DrawRoundedRectangle(Draw.LabelBack, Draw.Pen(bed.Color, 0.8), r, 3, 3);
            dc.DrawText(ft, new Point(r.Left + 3, r.Top + 1));
        }
        dc.Pop();

        // ---- geomod picks along the top edge
        foreach (var p in vm.Model.Structure.GeoMod.Picks)
        {
            double x = X(vm.Model.Table.Interp(vm.Model.Table.Vs, p.Md));
            if (double.IsNaN(x) || x < plot.Left || x > plot.Right) continue;
            var tri = new StreamGeometry();
            using (var ctx = tri.Open())
            {
                ctx.BeginFigure(new Point(x - 4, plot.Top - 7), true, true);
                ctx.LineTo(new Point(x + 4, plot.Top - 7), false, false);
                ctx.LineTo(new Point(x, plot.Top - 1), false, false);
            }
            tri.Freeze();
            dc.DrawGeometry(Draw.Brush("#C0392B"), null, tri);
        }

        // ---- axes
        dc.DrawRectangle(null, Draw.FramePen, plot);
        foreach (var tk in yt)
            Draw.Text(dc, tk.ToString("#,0.##"), plot.Left - 5, Y(tk) - 7, 10, Draw.AxisText, dip, Draw.Align.Right);
        foreach (var tk in xt)
            Draw.Text(dc, tk.ToString("#,0.##"), X(tk), plot.Bottom + 3, 10, Draw.AxisText, dip, Draw.Align.Center);
        Draw.Text(dc, $"Vertical section (ft), azimuth {vm.Model.VsAzimuthDeg:0.##} deg", plot.Left + plot.Width / 2, plot.Bottom + 17, 10.5, Brushes.Black, dip, Draw.Align.Center, bold: true);
        dc.PushTransform(new RotateTransform(-90, 12, plot.Top + plot.Height / 2));
        Draw.Text(dc, "TVD (ft)", 12, plot.Top + plot.Height / 2 - 8, 10.5, Brushes.Black, dip, Draw.Align.Center, bold: true);
        dc.Pop();
        Draw.Text(dc, $"Ref: {vm.ReferenceBed}   Mode: {vm.Mode}", plot.Right - 4, 2, 9.5, Brushes.DimGray, dip, Draw.Align.Right);
        Draw.Text(dc, "geomod picks", plot.Left, 2, 9, Draw.Brush("#C0392B"), dip);
    }

    static (int i0, int i1) VisibleRange(double[] x, double lo, double hi)
    {
        int n = x.Length;
        int i0 = Array.BinarySearch(x, lo); if (i0 < 0) i0 = ~i0; i0 = Math.Clamp(i0 - 1, 0, n - 1);
        int i1 = Array.BinarySearch(x, hi); if (i1 < 0) i1 = ~i1; i1 = Math.Clamp(i1, 0, n - 1);
        if (i1 <= i0) i1 = Math.Min(n - 1, i0 + 1);
        return (i0, i1);
    }

    // ------------------------------------------------------------------ interaction

    /// <summary>Wheel gesture at a point (public so the self-test can drive it): wheel = both, Ctrl = x only, Shift = y only.</summary>
    public void WheelAt(Point p, int delta, bool ctrl, bool shift)
    {
        var vm = Steering; if (vm == null) return;
        var plot = PlotRect;
        ZoomGestures.ApplyWheel(vm.XRange, vm.TvdRange,
            vm.XRange.FromPixel(p.X, plot.Left, plot.Width), vm.TvdRange.FromPixel(p.Y, plot.Top, plot.Height), delta, ctrl, shift);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var m = Keyboard.Modifiers;
        WheelAt(e.GetPosition(this), e.Delta, m.HasFlag(ModifierKeys.Control), m.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var vm = Steering; if (vm == null) return;
        Focus();
        if (e.ClickCount == 2) { vm.XRange.Reset(); vm.TvdRange.Reset(); e.Handled = true; return; }
        var p = e.GetPosition(this);
        var plot = PlotRect;
        DragMin0 = vm.XRange.Min; DragMax0 = vm.XRange.Max; DragMin1 = vm.TvdRange.Min; DragMax1 = vm.TvdRange.Max;
        string mode = p.X < plot.Left ? "zoomY" : p.Y > plot.Bottom ? "zoomX" : "pan";
        BeginDrag(e, mode);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var vm = Steering; if (vm == null) return;
        var plot = PlotRect;
        var p = e.GetPosition(this);
        if (Dragging)
        {
            if (DragMode == "pan")
            {
                double dx = -(p.X - DragStart.X) * (DragMax0 - DragMin0) / plot.Width;
                double dy = -(p.Y - DragStart.Y) * (DragMax1 - DragMin1) / plot.Height;
                vm.XRange.Set(DragMin0 + dx, DragMax0 + dx);
                vm.TvdRange.Set(DragMin1 + dy, DragMax1 + dy);
            }
            else if (DragMode == "zoomX")
            {
                double anchor = DragMin0 + (DragStart.X - plot.Left) / plot.Width * (DragMax0 - DragMin0);
                ZoomFrom(vm.XRange, DragMin0, DragMax0, anchor, ZoomGestures.AxisDragFactor(p.X - DragStart.X));
            }
            else if (DragMode == "zoomY")
            {
                double anchor = DragMin1 + (DragStart.Y - plot.Top) / plot.Height * (DragMax1 - DragMin1);
                ZoomFrom(vm.TvdRange, DragMin1, DragMax1, anchor, ZoomGestures.AxisDragFactor(DragStart.Y - p.Y));
            }
            return;
        }
        if (plot.Contains(p))
        {
            double vs = vm.XRange.FromPixel(p.X, plot.Left, plot.Width), tvd = vm.TvdRange.FromPixel(p.Y, plot.Top, plot.Height);
            var c = vm.Curtain;
            int i = Array.BinarySearch(c.X, vs); if (i < 0) i = ~i; i = Math.Clamp(i, 0, c.X.Length - 1);
            double md = c.Md[i];
            Cursor = Cursors.Hand;
            vm.CursorText = $"VS {vs:0.0} ft   TVD {tvd:0.00} ft   TVT {tvd - c.RefDepth[i]:0.00} ft below {vm.ReferenceBed}   (structure from MD {md:0})";
        }
        else Cursor = p.X < plot.Left ? Cursors.SizeNS : p.Y > plot.Bottom ? Cursors.SizeWE : Cursors.Arrow;
    }

    static void ZoomFrom(AxisRange r, double min0, double max0, double anchor, double factor)
    {
        double span = (max0 - min0) / factor;
        double frac = (anchor - min0) / (max0 - min0);
        r.Set(anchor - frac * span, anchor - frac * span + span);
    }
}
