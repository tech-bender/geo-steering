using GordonGeoSteer.Engine.Display;
using GordonGeoSteer.Engine.Workspace;
using Xunit;

namespace GordonGeoSteer.Engine.Tests;

public class DisplayAndWorkspaceTests
{
    [Fact]
    public void ZoomAt_keeps_anchor_fixed()
    {
        var a = new AxisRange(0, 100);
        double before = a.ToPixel(30, 0, 500);
        a.ZoomAt(30, 2);
        Assert.Equal(50, a.Span, 9);
        Assert.Equal(before, a.ToPixel(30, 0, 500), 9);
        a.ZoomAt(30, 0.5);
        Assert.Equal(0, a.Min, 9);
        Assert.Equal(100, a.Max, 9);
    }

    [Fact]
    public void Wheel_gestures_zoom_axes_independently()
    {
        var x = new AxisRange(0, 1000); var y = new AxisRange(9900, 10100);
        ZoomGestures.ApplyWheel(x, y, 500, 10000, 120, ctrl: true, shift: false);   // horizontal only
        Assert.Equal(1000 / ZoomGestures.WheelStep, x.Span, 9);
        Assert.Equal(200, y.Span, 9);
        ZoomGestures.ApplyWheel(x, y, 500, 10000, 120, ctrl: false, shift: true);   // vertical only
        Assert.Equal(1000 / ZoomGestures.WheelStep, x.Span, 9);
        Assert.Equal(200 / ZoomGestures.WheelStep, y.Span, 9);
        ZoomGestures.ApplyWheel(x, y, 500, 10000, -240, ctrl: false, shift: false); // both, out
        Assert.Equal(1000 * ZoomGestures.WheelStep, x.Span, 6);
        Assert.Equal(200 * ZoomGestures.WheelStep, y.Span, 6);
    }

    [Fact]
    public void Pixel_mapping_inverted_axis()
    {
        var a = new AxisRange(10000, 10100);
        Assert.Equal(400, a.ToPixel(10000, 0, 400, inverted: true), 9);
        Assert.Equal(10025, a.FromPixel(300, 0, 400, inverted: true), 9);
        a.Pan(a.PixelsToData(40, 400));
        Assert.Equal(10010, a.Min, 9);
    }

    [Fact]
    public void Decimator_keeps_extremes_and_order()
    {
        var rnd = new Random(7);
        int n = 20000;
        var along = Enumerable.Range(0, n).Select(i => i * 100.0 / n).ToArray();   // 100 pixels
        var across = Enumerable.Range(0, n).Select(_ => rnd.NextDouble() * 300).ToArray();
        across[12345] = double.NaN;
        var segs = Decimator.Decimate(along, across);
        Assert.Equal(2, segs.Count);
        int total = segs.Sum(s => s.Count);
        Assert.True(total <= 4 * 101 + 4, $"too many points: {total}");
        for (int px = 0; px < 100; px++)
        {
            var inPx = Enumerable.Range(0, n).Where(i => (int)Math.Floor(along[i]) == px && !double.IsNaN(across[i])).ToList();
            double mx = inPx.Max(i => across[i]), mn = inPx.Min(i => across[i]);
            var outPx = segs.SelectMany(s => s).Where(p => (int)Math.Floor(p.Along) == px).ToList();
            Assert.Equal(mx, outPx.Max(p => p.Across));
            Assert.Equal(mn, outPx.Min(p => p.Across));
        }
        // order preserved within each segment
        foreach (var s in segs)
            for (int i = 1; i < s.Count; i++) Assert.True(s[i].Along >= s[i - 1].Along);
    }

    [Fact]
    public void Decimator_handles_non_monotone_axis()
    {
        // goes down, back up, down again (a lateral crossing the same TVT several times)
        var along = new[] { 0.0, 1, 2, 3, 2, 1, 0, 1, 2 };
        var across = new[] { 5.0, 6, 7, 8, 9, 10, 11, 12, 13 };
        var segs = Decimator.Decimate(along, across);
        Assert.Single(segs);
        Assert.Equal(along.Length, segs[0].Count);
    }

    [Fact]
    public void Workspace_round_trip()
    {
        var w = new WorkspaceDocument
        {
            ProjectName = "p", ReferenceBed = "Middle", CorrelationMode = "DepthIndexStretch",
            Tracks =
            {
                new TrackSpec
                {
                    Title = "GR", Orientation = TrackOrientation.Horizontal, ScaleMin = 0, ScaleMax = 200, Size = 150,
                    Curves =
                    {
                        new CurveSpec { Source = "rt:GRCA", Color = "#00AA00", LineStyle = LineStyleKind.DotDash, Thickness = 2,
                                        Shading = new ShadingSpec { Mode = ShadingMode.Gradient, Ramp = ColorRampKind.Heat } },
                        new CurveSpec { Source = "syn:GR", Shading = new ShadingSpec { Mode = ShadingMode.ToCurve, OtherCurve = "rt:GRCA" } },
                    },
                },
            },
            Beds = { new BedBoundarySpec { Name = "Target top", Offset = 12.5, Color = "#FF0000", Thickness = 3 } },
            View = new ViewSpec { XMin = -100, XMax = 5000, TvtMin = -40, TvtMax = 80 },
        };
        string path = Path.Combine(Path.GetTempPath(), "gg-ws-test-" + Guid.NewGuid() + ".json");
        try
        {
            w.Save(path);
            var r = WorkspaceDocument.Load(path);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(w, WorkspaceDocument.Json),
                         System.Text.Json.JsonSerializer.Serialize(r, WorkspaceDocument.Json));
            Assert.Equal(LineStyleKind.DotDash, r.Tracks[0].Curves[0].LineStyle);
            Assert.Equal(12.5, r.Beds[0].Offset);
        }
        finally { File.Delete(path); }
    }
}
