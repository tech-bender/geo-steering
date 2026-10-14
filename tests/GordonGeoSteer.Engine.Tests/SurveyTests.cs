using GordonGeoSteer.Engine.Survey;
using GordonGeoSteer.Engine.Validation;
using Xunit;

namespace GordonGeoSteer.Engine.Tests;

public class SurveyTests
{
    [Fact]
    public void RatioFactor_series_is_continuous_with_closed_form_at_cutoff()
    {
        double c = MinimumCurvature.SeriesCutoff;
        double closed = Math.Tan(c / 2) / (c / 2);
        double series = MinimumCurvature.RatioFactor(c * (1 - 1e-12));
        Assert.True(Math.Abs(closed - series) < 1e-15, $"closed {closed:R} series {series:R}");
    }

    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.5e-3)]
    [InlineData(1e-5)]
    public void RatioFactor_series_matches_tan_over_x(double beta)
    {
        // At these angles tan(x)/x in double precision is still accurate to ~1e-15 relative.
        double exact = Math.Tan(beta / 2) / (beta / 2);
        Assert.True(Math.Abs(MinimumCurvature.RatioFactor(beta) - exact) < 2e-15);
    }

    [Fact]
    public void RatioFactor_series_uses_exact_coefficients()
    {
        // At beta = 0.1 the truncated series must match tan(x)/x to ~1e-15 (next term ~ 1e-16). A wrong
        // b^3 coefficient (e.g. 1/20160 instead of 17/20160) would be off by ~8e-10.
        double beta = 0.1;
        double exact = Math.Tan(beta / 2) / (beta / 2);
        Assert.True(Math.Abs(MinimumCurvature.RatioFactorSeries(beta) - exact) < 1e-14);
        double wrong = 1 + beta * beta / 12 + Math.Pow(beta, 4) / 120 + Math.Pow(beta, 6) / 20160;
        Assert.True(Math.Abs(wrong - exact) > 1e-10);
    }

    [Fact]
    public void Dogleg_matches_acos_form()
    {
        var rnd = new Random(1);
        for (int k = 0; k < 200; k++)
        {
            double i1 = rnd.NextDouble() * Math.PI, i2 = rnd.NextDouble() * Math.PI;
            double a1 = rnd.NextDouble() * 2 * Math.PI, a2 = rnd.NextDouble() * 2 * Math.PI;
            double acos = Math.Acos(Math.Clamp(Math.Cos(i2 - i1) - Math.Sin(i1) * Math.Sin(i2) * (1 - Math.Cos(a2 - a1)), -1, 1));
            Assert.True(Math.Abs(MinimumCurvature.Dogleg(i1, a1, i2, a2) - acos) < 1e-7);
        }
    }

    [Theory]
    [InlineData(572.9578, 0, 0, 0, 900, 30)]
    [InlineData(1145.916, 30, 45, 60, 1800, 95)]
    [InlineData(2864.789, 85, 300, 120, 3000, 100)]
    [InlineData(400, 89, 10, -90, 600, 7)]
    public void Minimum_curvature_reproduces_constant_curvature_arc(double r, double inc, double azi, double tf, double len, double step)
    {
        var (es, ei) = EngineValidation.ArcError(r, inc, azi, tf, len, step);
        Assert.True(es < 1e-8, $"station error {es}");
        Assert.True(ei < 1e-8, $"interpolation error {ei}");
    }

    [Fact]
    public void Vertical_then_straight_extrapolation()
    {
        var wp = Wellpath.Compute(new[] { new SurveyInput(0, 0, 0), new SurveyInput(1000, 0, 0) }, 10, 20, 5);
        var p = wp.At(500);
        Assert.Equal(505, p.Tvd, 9);
        Assert.Equal(10, p.North, 9);
        var below = wp.At(1500);
        Assert.Equal(1505, below.Tvd, 9);

        var h = Wellpath.Compute(new[] { new SurveyInput(100, 90, 90), new SurveyInput(200, 90, 90) });
        var q = h.At(300);
        Assert.Equal(200, q.East, 9);
        Assert.Equal(0, q.Tvd, 9);
    }

    [Fact]
    public void Interpolated_direction_is_great_circle_midpoint()
    {
        var wp = Wellpath.Compute(new[] { new SurveyInput(0, 0, 0), new SurveyInput(100, 20, 0) });
        var p = wp.At(50);
        Assert.Equal(10, p.IncDeg, 9);
    }

    [Fact]
    public void Vertical_section_projection()
    {
        Assert.Equal(Math.Sqrt(2) * 100, Wellpath.VerticalSection(100, 100, 45), 9);
        Assert.Equal(0, Wellpath.VerticalSection(100, -100, 45), 9);
        Assert.Equal(-50, Wellpath.VerticalSection(0, 0, 0, 50, 0), 9);
    }

    [Fact]
    public void Dls_per_100()
    {
        var wp = Wellpath.Compute(new[] { new SurveyInput(1000, 0, 0), new SurveyInput(1100, 3, 0) });
        Assert.Equal(3, wp.Stations[1].DlsPer100, 9);
    }
}
