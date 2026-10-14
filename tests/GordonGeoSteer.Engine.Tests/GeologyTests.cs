using GordonGeoSteer.Engine.Geology;
using Xunit;

namespace GordonGeoSteer.Engine.Tests;

public class GeologyTests
{
    static BicubicSurface Plane(Func<double, double, double> f, int nE = 8, int nN = 8)
    {
        var e = Enumerable.Range(0, nE).Select(i => -5000 + i * 13000.0 / (nE - 1)).ToArray();
        var n = Enumerable.Range(0, nN).Select(i => -5000 + i * 11000.0 / (nN - 1)).ToArray();
        var z = n.Select(nn => e.Select(ee => f(ee, nn)).ToArray()).ToArray();
        return new BicubicSurface(e, n, z);
    }

    [Fact]
    public void NaturalSpline_interpolates_nodes_and_reproduces_lines()
    {
        var x = new[] { 0.0, 1, 2.5, 4, 7 };
        var y = x.Select(v => 3 - 2 * v).ToArray();
        var s = new NaturalSpline(x, y);
        for (double t = 0; t <= 7; t += 0.1) Assert.Equal(3 - 2 * t, s.Evaluate(t), 10);
        var y2 = new[] { 1.0, -2, 5, 0, 3 };
        var s2 = new NaturalSpline(x, y2);
        for (int i = 0; i < x.Length; i++) Assert.Equal(y2[i], s2.Evaluate(x[i]), 12);
    }

    [Fact]
    public void NaturalSpline_has_zero_end_curvature()
    {
        var y2 = NaturalSpline.SecondDerivatives(new[] { 0.0, 1, 2, 3 }, new[] { 0.0, 1, 0, 1 });
        Assert.Equal(0, y2[0]);
        Assert.Equal(0, y2[^1]);
    }

    [Fact]
    public void Bicubic_reproduces_plane_and_clamps_outside_grid()
    {
        var s = Plane((e, n) => 10000 - 0.01 * e + 0.02 * n);
        Assert.Equal(10000 - 0.01 * 123 + 0.02 * -456, s.Evaluate(123, -456), 8);
        Assert.Equal(s.Evaluate(8000, 6000), s.Evaluate(20000, 9000), 12);
        Assert.Equal(s.Evaluate(-5000, 0), s.Evaluate(-9000, 0), 12);
        var (gE, gN) = s.Gradient(0, 0);
        Assert.Equal(-0.01, gE, 8);
        Assert.Equal(0.02, gN, 8);
    }

    [Fact]
    public void Bicubic_hits_grid_nodes()
    {
        var rnd = new Random(3);
        var e = new[] { 0.0, 10, 20, 30 }; var n = new[] { 0.0, 5, 15 };
        var z = n.Select(_ => e.Select(_ => rnd.NextDouble() * 100).ToArray()).ToArray();
        var s = new BicubicSurface(e, n, z);
        for (int i = 0; i < n.Length; i++)
            for (int j = 0; j < e.Length; j++)
                Assert.Equal(z[i][j], s.Evaluate(e[j], n[i]), 9);
    }

    [Fact]
    public void Hermite_endpoints_and_slopes()
    {
        Assert.Equal(1, Hermite.Value(0, 1, 0.5, 10, 4, -0.2, 0), 12);
        Assert.Equal(4, Hermite.Value(0, 1, 0.5, 10, 4, -0.2, 10), 12);
        Assert.Equal(0.5, Hermite.Slope(0, 1, 0.5, 10, 4, -0.2, 0), 12);
        Assert.Equal(-0.2, Hermite.Slope(0, 1, 0.5, 10, 4, -0.2, 10), 12);
        // numeric derivative agrees
        double x = 3.7, h = 1e-6;
        double num = (Hermite.Value(0, 1, 0.5, 10, 4, -0.2, x + h) - Hermite.Value(0, 1, 0.5, 10, 4, -0.2, x - h)) / (2 * h);
        Assert.Equal(num, Hermite.Slope(0, 1, 0.5, 10, 4, -0.2, x), 6);
    }

    [Fact]
    public void GeoMod_dip_shift_rules()
    {
        var g = new GeoModPath(new[]
        {
            new GeoModPick(1000, new[] { 10.0 }, new[] { 0.0 }, 0),
            new GeoModPick(2000, new[] { 20.0 }, new[] { 0.01 }, 0),
        }, 1, 500);
        Assert.Equal(10, g.DipShift(0, 0), 12);         // before first pick: constant
        Assert.Equal(10, g.DipShift(0, 1000), 12);
        Assert.Equal(20, g.DipShift(0, 2000), 12);
        Assert.Equal(15 + 1000.0 / 8 * (0 - 0.01), g.DipShift(0, 1500), 9); // Hermite midpoint: mean + h/8 (d0 - d1)
        // tail: slope decays from 0.01 to 0 over 500, then constant at y + s*D/3
        Assert.Equal(20 + 0.01 * 500 / 3, g.DipShift(0, 2500), 9);
        Assert.Equal(20 + 0.01 * 500 / 3, g.DipShift(0, 9000), 9);
        Assert.Equal(0.01, g.DipSlope(0, 2000 + 1e-9), 6);
        Assert.Equal(0, g.DipSlope(0, 2500), 9);
        // continuity of value and slope at the last pick
        Assert.Equal(g.DipShift(0, 2000 - 1e-7), g.DipShift(0, 2000 + 1e-7), 6);
        Assert.Equal(g.DipSlope(0, 2000 - 1e-7), g.DipSlope(0, 2000 + 1e-7), 5);
    }

    [Fact]
    public void GeoMod_fault_ramp()
    {
        var g = new GeoModPath(new[]
        {
            new GeoModPick(1000, new[] { 0.0 }, new[] { 0.0 }, 0),
            new GeoModPick(1500, new[] { 0.0 }, new[] { 0.0 }, 5),
            new GeoModPick(2000, new[] { 5.0 }, new[] { 0.0 }, 0),
        }, 1, 500);
        Assert.Equal(0, g.FaultShift(900));
        Assert.Equal(0, g.FaultShift(1200));
        Assert.Equal(5, g.FaultShift(1500), 12);
        Assert.Equal(2.5, g.FaultShift(1750), 12);
        Assert.Equal(0, g.FaultShift(2000), 12);
        // dip + fault is continuous through the ramp (the throw is folded into the next pick)
        Assert.Equal(5, g.DipShift(0, 2000) + g.FaultShift(2000), 12);
    }

    [Fact]
    public void Fault_table_steps()
    {
        var f = new FaultTable(90, 0, 0, new[] { (0.0, 10.0), (100.0, 20.0) });
        Assert.Equal(0, f.DeltaTvd(0, -5));
        Assert.Equal(10, f.DeltaTvd(0, 50));
        Assert.Equal(20, f.DeltaTvd(999, 150));
    }

    [Fact]
    public void DepthIndex_model_round_trips_and_stretches()
    {
        var top = Plane((e, n) => 10000);
        var bot = Plane((e, n) => 10050);
        var sm = new StructureModel(new[]
        {
            new GridBed("Top", 10000, 10000, top, BedGeoType.Stretch, -1),
            new GridBed("Base", 10060, 10060, bot, BedGeoType.Stretch, -1),
        }, new[] { new MarkerBed("Mid", 10030, 10030) }, GeoModPath.Empty(0));
        var col = sm.At(0, 0, 0);
        Assert.Equal(10025, sm.TvdFromDi(col, 10030), 9);       // stretch: 30/60 of 50 ft
        Assert.Equal(10030, sm.DiFromTvd(col, 10025), 9);
        Assert.Equal(9990, sm.TvdFromDi(col, 9990), 9);          // above top, flat (cos = 1)
        Assert.Equal(10070, sm.TvdFromDi(col, 10080), 9);        // below base
        foreach (double di in new[] { 9800.0, 10000, 10012.3, 10059.9, 10200 })
            Assert.Equal(di, sm.DiFromTvd(col, sm.TvdFromDi(col, di)), 9);
    }

    [Fact]
    public void DepthIndex_truncation_rules()
    {
        var top = Plane((e, n) => 10000);
        var bot = Plane((e, n) => 10050);
        var down = new StructureModel(new[]
        {
            new GridBed("Top", 10000, 10000, top, BedGeoType.TruncateDown, -1),
            new GridBed("Base", 10060, 10060, bot, BedGeoType.Stretch, -1),
        }, Array.Empty<MarkerBed>(), GeoModPath.Empty(0));
        var c = down.At(0, 0, 0);
        Assert.Equal(10030, down.TvdFromDi(c, 10030), 9);  // parallel to top
        Assert.Equal(10050, down.TvdFromDi(c, 10055), 9);  // capped at base
        var up = new StructureModel(new[]
        {
            new GridBed("Top", 10000, 10000, top, BedGeoType.Stretch, -1),
            new GridBed("Base", 10060, 10060, bot, BedGeoType.TruncateUp, -1),
        }, Array.Empty<MarkerBed>(), GeoModPath.Empty(0));
        c = up.At(0, 0, 0);
        Assert.Equal(10020, up.TvdFromDi(c, 10030), 9);    // parallel to base
        Assert.Equal(10000, up.TvdFromDi(c, 10005), 9);    // floored at top
    }

    [Fact]
    public void Dipping_bed_cosine_correction()
    {
        // bed rising 0.1 ft/ft to the north: dip = atan(0.1)
        var top = Plane((e, n) => 10000 - 0.1 * n);
        var sm = new StructureModel(new[] { new GridBed("Top", 10000, 10000, top, BedGeoType.Stretch, -1) },
                                    Array.Empty<MarkerBed>(), GeoModPath.Empty(0));
        var c = sm.At(0, 0, 0);
        double cos = 1 / Math.Sqrt(1.01);
        Assert.Equal(cos, c.CosDip[0], 6);
        Assert.Equal(10000 + 10 / cos, sm.TvdFromDi(c, 10010), 5);
    }
}
