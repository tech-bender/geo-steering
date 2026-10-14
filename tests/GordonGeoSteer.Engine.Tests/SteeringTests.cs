using GordonGeoSteer.Engine.Geology;
using GordonGeoSteer.Engine.Steering;
using GordonGeoSteer.Engine.Survey;
using Xunit;

namespace GordonGeoSteer.Engine.Tests;

/// <summary>Synthetic geosteering scenarios with known answers.</summary>
public class SteeringTests
{
    static BicubicSurface Plane(Func<double, double, double> f)
    {
        var e = Enumerable.Range(0, 8).Select(i => -5000 + i * 2000.0).ToArray();
        var n = Enumerable.Range(0, 8).Select(i => -5000 + i * 2000.0).ToArray();
        return new BicubicSurface(e, n, n.Select(nn => e.Select(ee => f(ee, nn)).ToArray()).ToArray());
    }

    /// <summary>Lateral heading north: vertical to 9000, build at 10 deg/100 ft to <paramref name="holdInc"/>, hold to 13000.</summary>
    static Wellpath Lateral(double holdInc)
    {
        var st = new List<SurveyInput> { new(0, 0, 0), new(9000, 0, 0) };
        double md = 9000, inc = 0;
        while (inc < holdInc - 1e-9) { md += 10; inc = Math.Min(holdInc, inc + 1); st.Add(new SurveyInput(md, inc, 0)); }
        st.Add(new SurveyInput(13000, holdInc, 0));
        return Wellpath.Compute(st);
    }

    static SteeringModel Model(Wellpath lateral, Func<double, double, double> bedTop, GeoModPath? gm = null, double offsetN = 0)
    {
        var top = Plane(bedTop);
        var bot = Plane((e, n) => bedTop(e, n) + 50);
        // offset well: vertical at (offsetN, 0); bed DIs = offset MDs of the beds there
        double topAtOffset = bedTop(0, offsetN);
        var sm = new StructureModel(new[]
        {
            new GridBed("Top", topAtOffset, topAtOffset, top, BedGeoType.Stretch, 0),
            new GridBed("Base", topAtOffset + 50, topAtOffset + 50, bot, BedGeoType.Stretch, 1),
        }, new[] { new MarkerBed("Mid", topAtOffset + 20, topAtOffset + 20) }, gm ?? GeoModPath.Empty(2));
        var off = new OffsetWell(Wellpath.Compute(new[] { new SurveyInput(0, 0, 0), new SurveyInput(12000, 0, 0) }, offsetN, 0, 0), 0, 12000);
        return new SteeringModel(lateral, off, sm, 0, 0, 0, "Top", 9000, 13000, 0.5);
    }

    static double[] LateralTvt(SteeringModel m, double fromMd, double toMd)
    {
        var md = Enumerable.Range(0, (int)((toMd - fromMd) / 5) + 1).Select(i => fromMd + i * 5).ToArray();
        var s = new DepthSeries(md, md.Select(_ => 1.0).ToArray());
        return m.LateralOnTvt(s).Axis;
    }

    [Fact]
    public void Down_structure_plots_downward_up_structure_upward_parallel_stacks()
    {
        // Flat beds at 10000. Hold at 89 deg = descending through the section; 91 = climbing; 90 = parallel.
        foreach (var (inc, expect) in new[] { (89.0, +1), (91.0, -1), (90.0, 0) })
        {
            var m = Model(Lateral(inc), (e, n) => 10000);
            var tvt = LateralTvt(m, 10000, 13000);   // all in the hold section
            for (int i = 1; i < tvt.Length; i++)
            {
                double d = tvt[i] - tvt[i - 1];
                if (expect > 0) Assert.True(d > 0, $"inc {inc}: TVT must increase (plot downward), got step {d}");
                if (expect < 0) Assert.True(d < 0, $"inc {inc}: TVT must decrease (plot upward), got step {d}");
                if (expect == 0) Assert.True(Math.Abs(d) < 1e-9, $"inc {inc}: TVT must stay constant (stack), got step {d}");
            }
        }
    }

    [Fact]
    public void Parallel_to_dipping_structure_stacks_on_itself()
    {
        // Bed rising 0.05 ft/ft to the north (dip 2.862 deg). A well heading north at 90 + 2.862 deg runs
        // parallel to the bed, so TVT is constant even though TVD changes by ~150 ft.
        double dipDeg = Math.Atan(0.05) * 180 / Math.PI;
        var m = Model(Lateral(90 + dipDeg), (e, n) => 10000 - 0.05 * n);
        var tvt = LateralTvt(m, 10500, 13000);
        double spread = tvt.Max() - tvt.Min();
        var tvdSpan = m.Lateral.At(10500).Tvd - m.Lateral.At(13000).Tvd;
        Assert.True(tvdSpan > 100, "test setup: the well must change TVD substantially");
        Assert.True(spread < 1e-6, $"TVT spread {spread} should be ~0");
    }

    [Fact]
    public void Geomod_dip_shift_moves_reference_and_TVT()
    {
        var gm = new GeoModPath(new[] { new GeoModPick(10000, new[] { 7.0, 7.0 }, new[] { 0.0, 0.0 }, 0) }, 2, 500);
        var m0 = Model(Lateral(90), (e, n) => 10000);
        var m1 = Model(Lateral(90), (e, n) => 10000, gm);
        double t0 = m0.PlotTvt(12000), t1 = m1.PlotTvt(12000);
        Assert.Equal(7, t0 - t1, 9);
        Assert.Equal(m0.Table.Interp(m0.Table.RefDepth, 12000) + 7, m1.Table.Interp(m1.Table.RefDepth, 12000), 9);
    }

    [Fact]
    public void Synthetic_is_offset_log_at_same_TVT()
    {
        var m = Model(Lateral(89.5), (e, n) => 10000 - 0.01 * n, offsetN: -200);
        // offset log: a function of offset MD (offset well vertical, so MD = TVD)
        Func<double, double> f = d => 80 + 50 * Math.Sin(d / 7.0);
        var md = Enumerable.Range(0, 20000).Select(i => 9000 + i * 0.25).ToArray();
        var off = new DepthSeries(md, md.Select(f).ToArray());
        var syn = m.Synthetic(off);
        double refOff = m.ReferenceOffsetTvd;
        Assert.Equal(10000 + 2, refOff, 9); // bed top at the offset location (N = -200)
        foreach (double lmd in new[] { 10300.0, 11000, 11777.5, 12900 })
        {
            double tvt = m.Table.Interp(m.Table.Tvt, lmd);
            double expected = off.ValueAt(refOff + tvt);
            Assert.Equal(expected, syn.ValueAt(lmd), 6);
        }
        // the offset log plotted on TVT puts the reference bed at 0
        var onTvt = m.OffsetOnTvt(off);
        int k = Array.FindIndex(md, d => Math.Abs(d - refOff) < 1e-9);
        Assert.Equal(0, onTvt.Axis[k], 9);
    }

    [Fact]
    public void Stretch_mode_uses_depth_index()
    {
        var m = Model(Lateral(90), (e, n) => 10000);
        m.Mode = CorrelationMode.DepthIndexStretch;
        // flat beds, same thickness as offset: DI = TVD shift of 0 relative to offset well
        double tvd = m.Lateral.At(12000).Tvd;
        Assert.Equal(tvd, m.CorrelatedOffsetMd(12000), 6);
        Assert.Equal(m.Table.Interp(m.Table.Tvt, 12000), m.PlotTvt(12000), 6);
    }

    [Fact]
    public void Marker_reference_bed_uses_DI_model()
    {
        var m = Model(Lateral(90), (e, n) => 10000);
        m.SetReferenceBed("Mid");
        Assert.Equal(10020, m.Table.Interp(m.Table.RefDepth, 12000), 6);
        Assert.Equal(-20, m.OffsetTvtOfBed("Top"), 9);
        Assert.Equal(30, m.OffsetTvtOfBed("Base"), 9);
    }

    [Fact]
    public void Curtain_profile_follows_beds()
    {
        var m = Model(Lateral(90), (e, n) => 10000 - 0.02 * n);
        var c = m.BuildCurtain(-500, 4000, 10);
        for (int i = 0; i < c.X.Length; i++)
        {
            Assert.Equal(c.RefDepth[i], c.Beds["Top"][i], 9);
            Assert.Equal(c.Beds["Top"][i] + 50, c.Beds["Base"][i], 6);
        }
        // the well heads north (azimuth 0) and VS azimuth is 0, so X = North beyond the build
        int j = Array.FindIndex(c.X, x => x >= 3000);
        Assert.Equal(10000 - 0.02 * c.X[j], c.Beds["Top"][j], 3);
    }
}
