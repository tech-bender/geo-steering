using System.Globalization;
using System.Text;
using GordonGeoSteer.Engine.Steering;
using GordonGeoSteer.Engine.Survey;

namespace GordonGeoSteer.Engine.Validation;

/// <summary>Summary statistics of a set of differences.</summary>
public readonly record struct ErrorStats(int N, double Mean, double MeanAbs, double MedianAbs, double P95Abs, double MaxAbs, double MaxAt)
{
    public static ErrorStats Of(IReadOnlyList<(double at, double err)> e)
    {
        if (e.Count == 0) return new ErrorStats(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        var abs = e.Select(x => Math.Abs(x.err)).OrderBy(x => x).ToArray();
        var worst = e.MaxBy(x => Math.Abs(x.err));
        return new ErrorStats(e.Count, e.Average(x => x.err), abs.Average(), abs[abs.Length / 2],
                              abs[Math.Min(abs.Length - 1, (int)(0.95 * abs.Length))], abs[^1], worst.at);
    }

    public override string ToString() => N == 0 ? "n=0" :
        string.Format(CultureInfo.InvariantCulture, "n={0} mean={1:0.####} meanAbs={2:0.####} medianAbs={3:0.####} p95Abs={4:0.####} maxAbs={5:0.####} (at {6:0.##})",
                      N, Mean, MeanAbs, MedianAbs, P95Abs, MaxAbs, MaxAt);
}

/// <summary>
/// Validates the engine against closed-form geometry and against PZS's own stored results in a real
/// project. Every number is computed, nothing is asserted here; callers (tests, self-test) decide
/// thresholds. Results go into <see cref="Metrics"/> and a human-readable <see cref="Text"/>.
/// </summary>
public sealed class EngineValidation
{
    public Dictionary<string, double> Metrics { get; } = new();
    readonly StringBuilder _sb = new();
    public string Text => _sb.ToString();

    void Line(string s) => _sb.AppendLine(s);
    void Metric(string k, double v) => Metrics[k] = v;

    // ------------------------------------------------------------------ 1. analytic arcs

    /// <summary>Survey along a constant-curvature arc; returns max position error of min-curvature stations and of interpolated points.</summary>
    public static (double stationErr, double interpErr) ArcError(double radius, double startIncDeg, double startAziDeg,
                                                                double turnPlaneDeg, double length, double stationStep)
    {
        // Circle through p0 with unit tangent t0 and principal normal n0 (perpendicular to t0).
        var t0 = MinimumCurvature.Tangent(startIncDeg * MinimumCurvature.Deg, startAziDeg * MinimumCurvature.Deg);
        // two unit vectors perpendicular to t0: "high side" and "right side"; rotate by the toolface (turnPlaneDeg).
        double i = startIncDeg * MinimumCurvature.Deg, a = startAziDeg * MinimumCurvature.Deg;
        var hs = (n: Math.Cos(i) * Math.Cos(a), e: Math.Cos(i) * Math.Sin(a), v: -Math.Sin(i));
        var rs = (n: -Math.Sin(a), e: Math.Cos(a), v: 0.0);
        double tf = turnPlaneDeg * MinimumCurvature.Deg;
        var n0 = (n: Math.Cos(tf) * hs.n + Math.Sin(tf) * rs.n, e: Math.Cos(tf) * hs.e + Math.Sin(tf) * rs.e, v: Math.Cos(tf) * hs.v + Math.Sin(tf) * rs.v);
        (double n, double e, double v) Pos(double s) =>
            (radius * Math.Sin(s / radius) * t0.n + radius * (1 - Math.Cos(s / radius)) * n0.n,
             radius * Math.Sin(s / radius) * t0.e + radius * (1 - Math.Cos(s / radius)) * n0.e,
             radius * Math.Sin(s / radius) * t0.v + radius * (1 - Math.Cos(s / radius)) * n0.v);
        (double inc, double azi) Dir(double s)
        {
            double c = Math.Cos(s / radius), sn = Math.Sin(s / radius);
            var d = MinimumCurvature.Direction(c * t0.n + sn * n0.n, c * t0.e + sn * n0.e, c * t0.v + sn * n0.v);
            return (d.inc / MinimumCurvature.Deg, d.azi / MinimumCurvature.Deg);
        }
        var st = new List<SurveyInput>();
        for (double s = 0; s <= length + 1e-9; s += stationStep)
        {
            var d = Dir(s);
            st.Add(new SurveyInput(1000 + s, d.inc, d.azi));
        }
        var wp = Wellpath.Compute(st);
        double errS = 0, errI = 0;
        foreach (var p in wp.Stations)
        {
            var x = Pos(p.Md - 1000);
            errS = Math.Max(errS, Dist(x, p));
        }
        double lastS = wp.LastMd - 1000; // only inside the surveyed arc (beyond it the path is extrapolated straight)
        for (double s = stationStep * 0.37; s < lastS; s += stationStep * 0.61)
        {
            var p = wp.At(1000 + s);
            errI = Math.Max(errI, Dist(Pos(s), p));
        }
        return (errS, errI);
    }

    static double Dist((double n, double e, double v) x, SurveyPoint p) =>
        Math.Sqrt((x.n - p.North) * (x.n - p.North) + (x.e - p.East) * (x.e - p.East) + (x.v - p.Tvd) * (x.v - p.Tvd));

    public void RunAnalytic()
    {
        Line("== Minimum curvature vs closed-form constant-curvature arcs ==");
        var cases = new (string name, double r, double inc, double azi, double tf, double len, double step)[]
        {
            ("2D build 0->90 deg, R=572.96 ft (10 deg/100ft), 30 ft stations", 572.9578, 0, 0, 0, 900, 30),
            ("3D arc from inc 30 azi 45, toolface 60, R=1145.9 ft (5 deg/100ft), 95 ft stations", 1145.916, 30, 45, 60, 1800, 95),
            ("3D arc from inc 85 azi 300, toolface 120 (turn+drop), R=2864.8 ft (2 deg/100ft), 100 ft stations", 2864.789, 85, 300, 120, 3000, 100),
        };
        int k = 0;
        foreach (var c in cases)
        {
            var (es, ei) = ArcError(c.r, c.inc, c.azi, c.tf, c.len, c.step);
            Line($"  {c.name}: max station error {es:E2} ft, max interpolated-point error {ei:E2} ft");
            Metric($"arc{k}.stationMaxErr", es);
            Metric($"arc{k}.interpMaxErr", ei);
            k++;
        }
    }

    // ------------------------------------------------------------------ 2..4 real data

    public void RunProject(SteeringProject sp)
    {
        var m = sp.Model;
        Line("");
        Line($"== Project: {sp.Document.Project?.Name} / model {sp.GeoModel.Name} ==");
        Line($"  lateral survey: {sp.ActualWell.Surveys?.StationCount} stations MD {m.Lateral.FirstMd}..{m.Lateral.LastMd}; offset survey: {m.Offset.Path.Stations.Count} stations");
        Line($"  reference bed: {m.ReferenceBed} (DI {m.ReferenceDi}, offset TVD {m.ReferenceOffsetTvd:0.###})");
        foreach (var n in sp.Notes) Line("  note: " + n);

        // ---- 2. min curvature vs PZS's stored TVD (realtime MTTV curve)
        var mttvCurve = sp.ActualWell.Curves.FirstOrDefault(c => c.Name == "MTTV");
        if (mttvCurve != null)
        {
            var mttv = sp.LoadColumns(mttvCurve).series;
            var atStations = new List<(double, double)>();
            var mds = new HashSet<double>(m.Lateral.Stations.Select(s => s.Md));
            var all = new List<(double, double)>();
            for (int i = 0; i < mttv.Count; i++)
            {
                if (double.IsNaN(mttv.Value[i])) continue;
                double md = mttv.Depth[i];
                if (md > m.Lateral.LastMd) continue;
                double err = m.Lateral.At(md).Tvd - mttv.Value[i];
                all.Add((md, err));
                if (mds.Contains(md)) atStations.Add((md, err));
            }
            var s1 = ErrorStats.Of(atStations); var s2 = ErrorStats.Of(all);
            Line("== Minimum curvature TVD vs PZS realtime MTTV (stored to 0.01 ft) ==");
            Line("  at survey-station MDs:      " + s1);
            Line("  at every MTTV sample (interpolated along the arc): " + s2);
            Metric("mttv.stations.n", s1.N); Metric("mttv.stations.maxAbs", s1.MaxAbs); Metric("mttv.stations.meanAbs", s1.MeanAbs);
            Metric("mttv.all.n", s2.N); Metric("mttv.all.maxAbs", s2.MaxAbs); Metric("mttv.all.meanAbs", s2.MeanAbs);
        }
        else Line("  (no MTTV curve in the realtime well - TVD check skipped)");

        // ---- 3. TVT / DI mapping vs PZS _M 3rd column
        var mCurve = sp.ModelWellDoc?.Curves.FirstOrDefault(c => c.ColumnCount >= 3);
        if (mCurve != null)
        {
            var (_, cols) = sp.LoadColumns(mCurve);
            var key3 = cols.Keys.FirstOrDefault(k => k != "md" && k != "value");
            if (key3 != null)
            {
                var md = cols["md"]; var c3 = cols[key3];
                var saved = m.Mode;
                var firstPick = m.Structure.GeoMod.Picks.Count > 0 ? m.Structure.GeoMod.Picks[0].Md : double.NaN;
                var diAll = new List<(double, double)>(); var diLat = new List<(double, double)>(); var diBuild = new List<(double, double)>();
                var parLat = new List<(double, double)>();
                double topBedMd = double.NaN;
                for (int i = 0; i < md.Length; i++)
                {
                    if (DepthSeries.IsNull(c3[i]) || md[i] > m.Lateral.LastMd + 20) continue;
                    double di = m.Table.Interp(m.Table.Di, md[i]);
                    if (double.IsNaN(di)) continue;
                    double e = di - c3[i];
                    diAll.Add((md[i], e));
                    double tvt = m.Table.Interp(m.Table.Tvt, md[i]);
                    // "lateral" = the part of the hole within 250 ft TVT of the reference bed (landing + lateral)
                    if (tvt > -250) diLat.Add((md[i], e)); else diBuild.Add((md[i], e));
                    if (tvt > -250)
                    {
                        double pm = m.Offset.MdAtTvd(m.ReferenceOffsetTvd + tvt);
                        parLat.Add((md[i], pm - c3[i]));
                    }
                    if (double.IsNaN(topBedMd) && tvt > -250) topBedMd = md[i];
                }
                m.Mode = saved;
                var sAll = ErrorStats.Of(diAll); var sLat = ErrorStats.Of(diLat); var sBuild = ErrorStats.Of(diBuild); var sPar = ErrorStats.Of(parLat);
                Line($"== Depth index (correlated offset MD) vs PZS {mCurve.Name} column 3 ('{key3}') ==");
                Line("  DI-stretch model, all samples:                      " + sAll);
                Line($"  DI-stretch model, within 250 ft above ref bed and below (MD >= {topBedMd:0.#}): " + sLat);
                Line("  DI-stretch model, build section (> 250 ft above ref bed): " + sBuild);
                Line("  Parallel-TVT mapping (spec TVT -> offset MD at same TVT), same lateral samples: " + sPar);
                Line($"  first geomod pick MD {firstPick:0.##}, decay distance {m.Structure.GeoMod.DecayDistance}");
                Metric("m3.di.all.medianAbs", sAll.MedianAbs); Metric("m3.di.all.maxAbs", sAll.MaxAbs);
                Metric("m3.di.lateral.n", sLat.N); Metric("m3.di.lateral.medianAbs", sLat.MedianAbs); Metric("m3.di.lateral.p95Abs", sLat.P95Abs); Metric("m3.di.lateral.maxAbs", sLat.MaxAbs);
                Metric("m3.di.build.n", sBuild.N); Metric("m3.di.build.medianAbs", sBuild.MedianAbs); Metric("m3.di.build.maxAbs", sBuild.MaxAbs);
                Metric("m3.parallel.lateral.medianAbs", sPar.MedianAbs); Metric("m3.parallel.lateral.maxAbs", sPar.MaxAbs); Metric("m3.parallel.lateral.mean", sPar.Mean);

                // Build-section diagnostic: PZS col3 compared with the DI computed WITHOUT geomod shifts.
                var noShift = new List<(double, double)>();
                for (int i = 0; i < md.Length; i += 1)
                {
                    if (DepthSeries.IsNull(c3[i])) continue;
                    double tvt = m.Table.Interp(m.Table.Tvt, md[i]);
                    if (!(tvt <= -250)) continue;
                    var p = m.Lateral.At(md[i]);
                    var col = m.Structure.At(p.North, p.East, double.NegativeInfinity, p.AziDeg); // before first pick -> first pick's shift
                    // remove each bed's (constant, pre-first-pick) geomod shift explicitly
                    for (int b = 0; b < col.Z.Length; b++)
                        col.Z[b] -= m.Structure.GeoMod.DipShift(m.Structure.Beds[b].GeoModIndex, double.NegativeInfinity);
                    noShift.Add((md[i], m.Structure.DiFromTvd(col, p.Tvd) - c3[i]));
                }
                var sNo = ErrorStats.Of(noShift);
                Line("  build section, DI computed with NO geomod shift: " + sNo);
                Metric("m3.build.noShift.medianAbs", sNo.MedianAbs);
            }
        }
        else Line("  (no 3-column _M curve in the model DB - TVT check skipped)");

        // ---- 4. realtime GR vs synthetic GR
        var rtGr = sp.ActualWell.Curves.FirstOrDefault(c => c.Name is "GRCA" or "GR" or "GRC" or "GAMMA");
        var offGr = sp.OffsetWellDoc.Curves.FirstOrDefault(c => c.Name is "GR" or "GRC");
        if (rtGr != null && offGr != null)
        {
            var rt = sp.LoadColumns(rtGr).series;
            var off = sp.LoadColumns(offGr).series;
            Line($"== Realtime {rtGr.Name} vs synthetic {offGr.Name} along the lateral (inclination > 80 deg) ==");
            var saved = m.Mode;
            foreach (var mode in new[] { CorrelationMode.ParallelToReference, CorrelationMode.DepthIndexStretch })
            {
                m.Mode = mode;
                var syn = m.Synthetic(off);
                var (a, b, mdUsed) = Paired(rt, syn, m, 0);
                double r = Pearson(a, b);
                // baselines
                var rng = new Random(12345);
                double shuf = 0; int K = 20;
                for (int k = 0; k < K; k++) shuf += Math.Abs(Pearson(a, Shuffle(b, rng)));
                shuf /= K;
                var lagged = new List<string>();
                double bestLag = double.NegativeInfinity;
                foreach (double lag in new[] { -1000.0, -300, 300, 1000 })
                {
                    var (a2, b2, _) = Paired(rt, syn, m, lag);
                    double rl = Pearson(a2, b2);
                    bestLag = Math.Max(bestLag, rl);
                    lagged.Add($"{lag:+0;-0} ft: {rl:0.000}");
                }
                var tvtShift = new List<string>();
                foreach (double dz in new[] { -10.0, -5, 5, 10 })
                {
                    var b3 = new double[a.Length];
                    for (int i = 0; i < mdUsed.Length; i++)
                    {
                        double om = m.CorrelatedOffsetMd(mdUsed[i]);
                        double ot = m.Offset.TvdAtMd(om) + dz;
                        b3[i] = off.ValueAt(m.Offset.MdAtTvd(ot));
                    }
                    tvtShift.Add($"{dz:+0;-0} ft: {Pearson(a, b3):0.000}");
                }
                Line($"  mode {mode}: r = {r:0.000} over {a.Length} samples; shuffled-synthetic |r| (mean of {K}) = {shuf:0.000}");
                Line($"     synthetic lagged along MD: {string.Join(", ", lagged)}");
                Line($"     offset log shifted in TVT: {string.Join(", ", tvtShift)}");
                string key = mode == CorrelationMode.ParallelToReference ? "parallel" : "stretch";
                Metric($"gr.{key}.r", r); Metric($"gr.{key}.n", a.Length); Metric($"gr.{key}.shuffledAbsR", shuf); Metric($"gr.{key}.bestLagR", bestLag);
            }
            // parallel mode with each gridded bed as the reference
            string savedRef = m.ReferenceBed;
            m.Mode = CorrelationMode.ParallelToReference;
            foreach (var bed in m.Structure.Beds)
            {
                m.SetReferenceBed(bed.Name);
                var (a, b, _) = Paired(rt, m.Synthetic(off), m, 0);
                double r = Pearson(a, b);
                Line($"  parallel mode with reference bed '{bed.Name}': r = {r:0.000}");
                Metric($"gr.parallel.ref[{bed.Name}].r", r);
            }
            m.SetReferenceBed(savedRef);
            m.Mode = saved;
        }
        else Line("  (no realtime/offset GR pair found - correlation check skipped)");

        // ---- 5. stacking sanity on the real lateral: count sign changes of dTVT/dMD
        var t = m.Table;
        int up = 0, down = 0, flat = 0;
        for (int i = 1; i < t.Count; i++)
        {
            if (t.Inc[i] < 80) continue;
            double d = t.Tvt[i] - t.Tvt[i - 1];
            if (d > 1e-4) down++; else if (d < -1e-4) up++; else flat++;
        }
        Line($"== Lateral TVT direction (inc > 80): moving down-structure {down} steps, up-structure {up}, parallel {flat} (0.5 ft MD steps) ==");
    }

    static (double[] a, double[] b, double[] md) Paired(DepthSeries rt, DepthSeries syn, SteeringModel m, double lag)
    {
        var a = new List<double>(); var b = new List<double>(); var mdl = new List<double>();
        for (int i = 0; i < rt.Count; i++)
        {
            double md = rt.Depth[i];
            double inc = m.Table.Interp(m.Table.Inc, md);
            if (!(inc > 80)) continue;
            double v = rt.Value[i];
            double s = syn.ValueAt(md + lag);
            if (double.IsNaN(v) || double.IsNaN(s)) continue;
            a.Add(v); b.Add(s); mdl.Add(md);
        }
        return (a.ToArray(), b.ToArray(), mdl.ToArray());
    }

    static double[] Shuffle(double[] x, Random r)
    {
        var y = (double[])x.Clone();
        for (int i = y.Length - 1; i > 0; i--) { int j = r.Next(i + 1); (y[i], y[j]) = (y[j], y[i]); }
        return y;
    }

    public static double Pearson(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        int n = 0; double sa = 0, sb = 0;
        for (int i = 0; i < a.Count; i++) { if (double.IsNaN(a[i]) || double.IsNaN(b[i])) continue; sa += a[i]; sb += b[i]; n++; }
        if (n < 3) return double.NaN;
        double ma = sa / n, mb = sb / n, cov = 0, va = 0, vb = 0;
        for (int i = 0; i < a.Count; i++)
        {
            if (double.IsNaN(a[i]) || double.IsNaN(b[i])) continue;
            double x = a[i] - ma, y = b[i] - mb;
            cov += x * y; va += x * x; vb += y * y;
        }
        return cov / Math.Sqrt(va * vb);
    }

    /// <summary>Runs everything; returns the validation object (Text + Metrics).</summary>
    public static EngineValidation Run(SteeringProject? sp)
    {
        var v = new EngineValidation();
        v.RunAnalytic();
        if (sp != null) v.RunProject(sp);
        return v;
    }
}
