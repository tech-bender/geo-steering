using GordonGeoSteer.Engine.Steering;
using GordonGeoSteer.Engine.Validation;
using Xunit;
using Xunit.Abstractions;

namespace GordonGeoSteer.Engine.Tests;

/// <summary>
/// Validation against a real imported PZS project (no real data is stored in the repo). Point
/// GG_PROJECT_JSON at an importer output project.json, or run selftest.cmd first so that
/// selftest-output\import\project.json exists. Without data these tests pass trivially and say so.
/// </summary>
public class RealDataValidationTests
{
    readonly ITestOutputHelper _out;
    public RealDataValidationTests(ITestOutputHelper o) { _out = o; }

    static string? FindProject()
    {
        var env = Environment.GetEnvironmentVariable("GG_PROJECT_JSON");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GordonGeoSteer.sln"))) dir = dir.Parent;
        if (dir == null) return null;
        var p = Path.Combine(dir.FullName, "selftest-output", "import", "project.json");
        return File.Exists(p) ? p : null;
    }

    [Fact]
    public void Bakken_engine_validation()
    {
        var path = FindProject();
        if (path == null) { _out.WriteLine("No imported project found (set GG_PROJECT_JSON) - real-data validation NOT run."); return; }
        var sp = SteeringProject.Load(path);
        var v = EngineValidation.Run(sp);
        _out.WriteLine(v.Text);
        var outDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!, "validation");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "engine-validation.txt"), v.Text);

        // Thresholds are deliberately tight where the comparison is like-for-like.
        Assert.True(v.Metrics["arc0.stationMaxErr"] < 1e-8);
        if (v.Metrics.TryGetValue("mttv.stations.maxAbs", out var mt))
            Assert.True(mt < 0.01, $"MTTV at stations max error {mt} (MTTV is stored to 0.01 ft)");
        if (v.Metrics.TryGetValue("m3.di.lateral.medianAbs", out var med))
            Assert.True(med < 0.05, $"DI vs _M column 3 median error {med}");
        // Realtime GR vs synthetic GR must be clearly above the shuffled baseline in both modes.
        if (v.Metrics.TryGetValue("gr.parallel.r", out var r))
            Assert.True(r > v.Metrics["gr.parallel.shuffledAbsR"] + 0.2, $"GR correlation {r} not clearly above shuffled baseline");
        if (v.Metrics.TryGetValue("gr.stretch.r", out var rs))
            Assert.True(rs > v.Metrics["gr.stretch.shuffledAbsR"] + 0.4, $"GR correlation (stretch) {rs} not clearly above shuffled baseline");
    }

    [Fact]
    public void Bakken_bicubic_matches_independent_scipy_value()
    {
        var path = FindProject();
        if (path == null) { _out.WriteLine("No imported project found - skipped."); return; }
        var sp = SteeringProject.Load(path);
        var bed = sp.Model.Structure.Beds.FirstOrDefault(b => b.Name == "Middle Member Thickness");
        if (bed == null) { _out.WriteLine("Not the Bakken sample - skipped."); return; }
        // Independent reference: scipy.interpolate.CubicSpline(bc_type='natural') along North per East
        // column, then along East, at (E, N) = (0, 0) on the same imported grid.
        Assert.Equal(9971.414972091827, bed.Surface.Evaluate(0, 0), 6);
    }
}
