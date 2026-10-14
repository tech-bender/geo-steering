using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GordonGeoSteer.Engine.Display;
using GordonGeoSteer.ViewModels.Steering;

namespace GordonGeoSteer.Controls.Steering;

/// <summary>Base for the steering-view controls: holds the view-model and redraws when it says so.</summary>
public abstract class SteeringElement : FrameworkElement
{
    public static readonly DependencyProperty SteeringProperty = DependencyProperty.Register(
        nameof(Steering), typeof(SteeringViewModel), typeof(SteeringElement),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSteeringChanged));

    public SteeringViewModel? Steering { get => (SteeringViewModel?)GetValue(SteeringProperty); set => SetValue(SteeringProperty, value); }

    static void OnSteeringChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var el = (SteeringElement)d;
        if (e.OldValue is SteeringViewModel o) o.Redraw -= el.OnRedraw;
        if (e.NewValue is SteeringViewModel n) n.Redraw += el.OnRedraw;
    }

    void OnRedraw(object? sender, EventArgs e) => InvalidateVisual();

    protected SteeringElement()
    {
        Focusable = true;
        ClipToBounds = true;
        Unloaded += (_, _) => { if (Steering != null) Steering.Redraw -= OnRedraw; };
        Loaded += (_, _) => { if (Steering != null) { Steering.Redraw -= OnRedraw; Steering.Redraw += OnRedraw; } };
    }

    protected double Dip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    // ---- drag helper shared by the controls
    protected Point DragStart;
    protected bool Dragging;
    protected string DragMode = "";
    protected double DragMin0, DragMax0, DragMin1, DragMax1;

    protected void BeginDrag(MouseButtonEventArgs e, string mode)
    {
        DragStart = e.GetPosition(this);
        Dragging = true;
        DragMode = mode;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (Dragging) { Dragging = false; ReleaseMouseCapture(); e.Handled = true; }
        base.OnMouseLeftButtonUp(e);
    }
}

/// <summary>
/// A log track. Vertical: TVT axis down the page (reference bed at TVT 0), bed boundaries across.
/// Horizontal: vertical-section axis shared with the curtain (same left/right margins, always aligned).
/// Wheel zooms the track's depth/position axis about the cursor, drag pans, double-click resets,
/// click selects the track for editing.
/// </summary>
public sealed class LogTrackControl : SteeringElement
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(TrackViewModel), typeof(LogTrackControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public TrackViewModel? Track { get => (TrackViewModel?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public const double HorizontalRowHeight = 26;

    Rect PlotRect()
    {
        var vm = Steering!;
        if (Track!.IsVertical)
        {
            double h = vm.VerticalHeaderHeight;
            return new Rect(0.5, h, Math.Max(1, ActualWidth - 1), Math.Max(1, ActualHeight - h - 1));
        }
        return new Rect(CurtainControl.LeftMargin, 3, Math.Max(1, ActualWidth - CurtainControl.LeftMargin - CurtainControl.RightMargin), Math.Max(1, ActualHeight - 6));
    }

    AxisRange Axis => Track!.IsVertical ? Steering!.TvtRange : Steering!.XRange;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var vm = Steering; var t = Track;
        if (vm == null || t == null || ActualWidth < 10 || ActualHeight < 10) return;
        var plot = PlotRect();
        var painter = new TrackPainter(vm, t, plot);
        double dip = Dip;

        // grid
        painter.PaintValueGrid(dc);
        if (t.IsVertical)
        {
            foreach (var tick in Draw.Ticks(vm.TvtRange.Min, vm.TvtRange.Max, (int)Math.Max(3, plot.Height / 45)))
            {
                double y = painter.Along(tick);
                dc.DrawLine(Math.Abs(tick) < 1e-9 ? Draw.FramePen : Draw.GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            }
        }
        else
        {
            foreach (var tick in Draw.Ticks(vm.XRange.Min, vm.XRange.Max, (int)Math.Max(3, plot.Width / 90)))
            {
                double x = painter.Along(tick);
                dc.DrawLine(Draw.GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            }
        }

        painter.Paint(dc);

        if (t.IsVertical) PaintBeds(dc, vm, plot, painter, dip);
        dc.DrawRectangle(null, vm.SelectedTrack == t ? Draw.SelectedPen : Draw.FramePen, plot);

        if (t.IsVertical) PaintVerticalHeader(dc, vm, t, dip);
        else PaintHorizontalHeader(dc, t, plot, dip);
    }

    static void PaintBeds(DrawingContext dc, SteeringViewModel vm, Rect plot, TrackPainter painter, double dip)
    {
        dc.PushClip(new RectangleGeometry(plot));
        double lastLabelY = double.NegativeInfinity;
        foreach (var b in vm.Beds.Where(b => b.Visible && !double.IsNaN(b.Tvt)).OrderBy(b => b.Tvt))
        {
            double y = painter.Along(b.Tvt);
            if (y < plot.Top - 2 || y > plot.Bottom + 2) continue;
            dc.DrawLine(Draw.Pen(b.Color, b.Thickness), new Point(plot.Left, y), new Point(plot.Right, y));
            if (b.ShowLabel && y - lastLabelY > 11)
            {
                Draw.Text(dc, b.Name.ToUpperInvariant(), plot.Left + 4, y - 13, 9.5, Draw.Brush(b.Color), dip, bold: true, background: Draw.LabelBack);
                lastLabelY = y;
            }
        }
        dc.Pop();
    }

    void PaintVerticalHeader(DrawingContext dc, SteeringViewModel vm, TrackViewModel t, double dip)
    {
        double w = ActualWidth;
        dc.DrawRectangle(Draw.Brush("#F4F6F9"), null, new Rect(0, 0, w, vm.VerticalHeaderHeight));
        Draw.Text(dc, t.Title, w / 2, 3, 11, Brushes.Black, dip, Draw.Align.Center, bold: true);
        var (a, b) = t.EffectiveScale();
        double y = 20;
        foreach (var c in t.Curves)
        {
            var brush = Draw.Brush(c.Color);
            dc.DrawLine(Draw.Pen(c.Color, Math.Max(1.5, c.Thickness), c.LineStyle), new Point(6, y + 22), new Point(w - 6, y + 22));
            Draw.Text(dc, Draw.Fmt(a), 4, y + 6, 9.5, brush, dip);
            Draw.Text(dc, Draw.Fmt(b), w - 4, y + 6, 9.5, brush, dip, Draw.Align.Right);
            string name = c.Display + (c.Unit != null ? $" [{c.Unit}]" : "") + (c.ShadeMode != Engine.Workspace.ShadingMode.None ? " *" : "");
            var ft = Draw.Format(name, 9.5, brush, dip, bold: true);
            double maxW = w - 70;
            if (ft.Width > maxW) ft.MaxTextWidth = Math.Max(20, maxW);
            ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(ft, new Point(w / 2 - Math.Min(ft.Width, maxW) / 2, y + 6));
            y += 30;
        }
        if (t.LogScale) Draw.Text(dc, "log", w - 4, 3, 9, Brushes.Gray, dip, Draw.Align.Right);
        dc.DrawLine(Draw.FramePen, new Point(0, vm.VerticalHeaderHeight - 0.5), new Point(w, vm.VerticalHeaderHeight - 0.5));
    }

    void PaintHorizontalHeader(DrawingContext dc, TrackViewModel t, Rect plot, double dip)
    {
        double w = CurtainControl.LeftMargin - 4;
        dc.DrawRectangle(Draw.Brush("#F4F6F9"), null, new Rect(0, 0, w, ActualHeight));
        Draw.Text(dc, t.Title, 4, 2, 10.5, Brushes.Black, dip, bold: true);
        var (a, b) = t.EffectiveScale();
        double y = 18;
        foreach (var c in t.Curves)
        {
            if (y + HorizontalRowHeight > ActualHeight) break;
            var brush = Draw.Brush(c.Color);
            var ft = Draw.Format(c.Display, 9, brush, dip, bold: true);
            ft.MaxTextWidth = w - 6; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(ft, new Point(4, y));
            dc.DrawLine(Draw.Pen(c.Color, Math.Max(1.5, c.Thickness), c.LineStyle), new Point(4, y + 14), new Point(w - 4, y + 14));
            Draw.Text(dc, Draw.Fmt(a) + " - " + Draw.Fmt(b) + (t.LogScale ? " log" : ""), 4, y + 14, 8.5, Brushes.DimGray, dip);
            y += HorizontalRowHeight;
        }
        // value scale at the plot's left edge
        Draw.Text(dc, Draw.Fmt(b), plot.Left - 2, plot.Top, 8.5, Brushes.DimGray, dip, Draw.Align.Right);
        Draw.Text(dc, Draw.Fmt(a), plot.Left - 2, plot.Bottom - 12, 8.5, Brushes.DimGray, dip, Draw.Align.Right);
    }

    // ------------------------------------------------------------------ interaction

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Steering == null || Track == null) return;
        var plot = PlotRect();
        var p = e.GetPosition(this);
        bool v = Track.IsVertical;
        double anchor = v ? Axis.FromPixel(p.Y, plot.Top, plot.Height) : Axis.FromPixel(p.X, plot.Left, plot.Width);
        Axis.ZoomAt(anchor, ZoomGestures.WheelFactor(e.Delta));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Steering == null || Track == null) return;
        Focus();
        Steering.SelectedTrack = Track;
        if (e.ClickCount == 2) { Axis.Reset(); e.Handled = true; return; }
        DragMin0 = Axis.Min; DragMax0 = Axis.Max;
        BeginDrag(e, "pan");
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Steering == null || Track == null) return;
        var plot = PlotRect();
        var p = e.GetPosition(this);
        if (Dragging)
        {
            double d = Track.IsVertical ? -(p.Y - DragStart.Y) * (DragMax0 - DragMin0) / plot.Height : -(p.X - DragStart.X) * (DragMax0 - DragMin0) / plot.Width;
            Axis.Set(DragMin0 + d, DragMax0 + d);
            return;
        }
        if (Track.IsVertical && plot.Contains(p))
            Steering.CursorText = $"TVT {Axis.FromPixel(p.Y, plot.Top, plot.Height):0.0} ft ({Steering.ReferenceBed} = 0)";
        else if (!Track.IsVertical && plot.Contains(p))
            Steering.CursorText = $"VS {Axis.FromPixel(p.X, plot.Left, plot.Width):0.0} ft";
    }
}

/// <summary>The TVT depth axis to the right of the vertical tracks (wheel zooms, drag pans).</summary>
public sealed class TvtAxisControl : SteeringElement
{
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Draw.Brush("#F4F6F9"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        var vm = Steering;
        if (vm == null) return;
        double top = vm.VerticalHeaderHeight, h = Math.Max(1, ActualHeight - top - 1);
        double dip = Dip;
        Draw.Text(dc, "TVT", ActualWidth / 2, 4, 11, Brushes.Black, dip, Draw.Align.Center, bold: true);
        Draw.Text(dc, "ft", ActualWidth / 2, 19, 9.5, Brushes.DimGray, dip, Draw.Align.Center);
        var ftRef = Draw.Format(vm.ReferenceBed, 8.5, Brushes.DimGray, dip);
        ftRef.MaxTextWidth = Math.Max(10, ActualWidth - 4); ftRef.MaxLineCount = 3;
        dc.DrawText(ftRef, new Point(2, 34));
        dc.DrawLine(Draw.FramePen, new Point(0, top - 0.5), new Point(ActualWidth, top - 0.5));
        foreach (var t in Draw.Ticks(vm.TvtRange.Min, vm.TvtRange.Max, (int)Math.Max(3, h / 45)))
        {
            double y = vm.TvtRange.ToPixel(t, top, h);
            dc.DrawLine(Draw.FramePen, new Point(0, y), new Point(6, y));
            Draw.Text(dc, Draw.Fmt(t), 9, y - 7, 10, Draw.AxisText, dip, bold: Math.Abs(t) < 1e-9);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var vm = Steering; if (vm == null) return;
        double top = vm.VerticalHeaderHeight, h = Math.Max(1, ActualHeight - top - 1);
        vm.TvtRange.ZoomAt(vm.TvtRange.FromPixel(e.GetPosition(this).Y, top, h), ZoomGestures.WheelFactor(e.Delta));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var vm = Steering; if (vm == null) return;
        if (e.ClickCount == 2) { vm.TvtRange.Reset(); return; }
        DragMin0 = vm.TvtRange.Min; DragMax0 = vm.TvtRange.Max;
        BeginDrag(e, "zoom");
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var vm = Steering; if (vm == null || !Dragging) return;
        // drag on the axis: up = zoom in, down = zoom out, about the drag start point
        double top = vm.VerticalHeaderHeight, h = Math.Max(1, ActualHeight - top - 1);
        double anchor = DragMin0 + (DragStart.Y - top) / h * (DragMax0 - DragMin0);
        double f = ZoomGestures.AxisDragFactor(DragStart.Y - e.GetPosition(this).Y);
        double span = (DragMax0 - DragMin0) / f;
        double frac = (anchor - DragMin0) / (DragMax0 - DragMin0);
        vm.TvtRange.Set(anchor - frac * span, anchor - frac * span + span);
    }
}
