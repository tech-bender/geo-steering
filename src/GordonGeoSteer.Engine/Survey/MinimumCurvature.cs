namespace GordonGeoSteer.Engine.Survey;

/// <summary>
/// Minimum-curvature survey math (textbook method): dogleg between two stations, the ratio factor
/// RF(beta) = tan(beta/2)/(beta/2), and the position step. Angles in radians unless named *Deg.
/// Coordinates: North, East, TVD (positive down).
/// </summary>
public static class MinimumCurvature
{
    public const double Deg = Math.PI / 180.0;

    /// <summary>Below this dogleg (radians) the ratio factor uses its Taylor series instead of tan(x)/x.</summary>
    public const double SeriesCutoff = 0.002;

    /// <summary>Dogleg angle (radians) between directions (i1,a1) and (i2,a2), haversine form (accurate for small angles).</summary>
    public static double Dogleg(double i1, double a1, double i2, double a2)
    {
        double si = Math.Sin((i2 - i1) / 2), sa = Math.Sin((a2 - a1) / 2);
        double h = si * si + Math.Sin(i1) * Math.Sin(i2) * sa * sa;
        if (double.IsNaN(h)) return 0;
        if (h > 1) h = 1;
        if (h < 0) h = 0;
        return 2 * Math.Asin(Math.Sqrt(h));
    }

    /// <summary>
    /// Ratio factor tan(beta/2)/(beta/2). For small beta the exact Maclaurin series of tan(x)/x with
    /// x = beta/2, written in b = beta^2: 1 + b/12 + b^2/120 + 17 b^3/20160 + 31 b^4/362880.
    /// </summary>
    public static double RatioFactor(double beta)
    {
        beta = Math.Abs(beta);
        if (beta >= SeriesCutoff)
        {
            double x = beta / 2;
            return Math.Tan(x) / x;
        }
        return RatioFactorSeries(beta);
    }

    /// <summary>The small-angle series of <see cref="RatioFactor"/> (exposed so its coefficients can be tested at larger angles).</summary>
    public static double RatioFactorSeries(double beta)
    {
        double b = beta * beta;
        return 1 + b * (1.0 / 12 + b * (1.0 / 120 + b * (17.0 / 20160 + b * (31.0 / 362880))));
    }

    /// <summary>Position change over a course of length dMd from direction (i1,a1) to (i2,a2) (radians).</summary>
    public static (double dN, double dE, double dTvd, double dogleg) Step(double i1, double a1, double i2, double a2, double dMd)
    {
        double beta = Dogleg(i1, a1, i2, a2);
        double h = RatioFactor(beta) * dMd / 2;
        double s1 = Math.Sin(i1), s2 = Math.Sin(i2);
        return (h * (s1 * Math.Cos(a1) + s2 * Math.Cos(a2)),
                h * (s1 * Math.Sin(a1) + s2 * Math.Sin(a2)),
                h * (Math.Cos(i1) + Math.Cos(i2)),
                beta);
    }

    /// <summary>Unit tangent (N, E, V) for inclination/azimuth in radians.</summary>
    public static (double n, double e, double v) Tangent(double inc, double azi) =>
        (Math.Sin(inc) * Math.Cos(azi), Math.Sin(inc) * Math.Sin(azi), Math.Cos(inc));

    /// <summary>Inclination/azimuth (radians, azimuth in [0, 2pi)) of a direction vector (N, E, V).</summary>
    public static (double inc, double azi) Direction(double n, double e, double v)
    {
        double inc = Math.Atan2(Math.Sqrt(n * n + e * e), v);
        double azi = Math.Atan2(e, n);
        if (azi < 0) azi += 2 * Math.PI;
        if (azi >= 2 * Math.PI) azi -= 2 * Math.PI;
        return (inc, azi);
    }
}

/// <summary>One surveyed (or interpolated) point with its computed position.</summary>
public readonly record struct SurveyPoint(double Md, double IncDeg, double AziDeg, double North, double East, double Tvd,
                                          double DoglegDeg, double DlsPer100)
{
    public override string ToString() => $"MD {Md:0.##} Inc {IncDeg:0.###} Azi {AziDeg:0.###} N {North:0.###} E {East:0.###} TVD {Tvd:0.###}";
}

/// <summary>Raw survey station input (MD, inclination and azimuth in degrees).</summary>
public readonly record struct SurveyInput(double Md, double IncDeg, double AziDeg);

/// <summary>
/// A wellpath computed by minimum curvature from a station list and a tie-in position (the first
/// station sits at the tie-in). Supports position/direction at an arbitrary MD: inside a course the
/// unit tangent is interpolated along the great circle (constant curvature, which is what minimum
/// curvature assumes), above the first and below the last station the path is extended straight
/// along the end tangent.
/// </summary>
public sealed class Wellpath
{
    readonly SurveyPoint[] _st;
    readonly double[] _md;

    public IReadOnlyList<SurveyPoint> Stations => _st;
    public double FirstMd => _md[0];
    public double LastMd => _md[^1];

    Wellpath(SurveyPoint[] st)
    {
        _st = st;
        _md = st.Select(s => s.Md).ToArray();
    }

    /// <summary>
    /// Builds the path. Stations are sorted by MD; a station within 0.001 of the previous MD is
    /// dropped (zero-length course). The first station is placed at (tieN, tieE, tieTvd).
    /// </summary>
    public static Wellpath Compute(IEnumerable<SurveyInput> stations, double tieN = 0, double tieE = 0, double tieTvd = 0)
    {
        var input = stations.Where(s => !double.IsNaN(s.Md) && !double.IsNaN(s.IncDeg) && !double.IsNaN(s.AziDeg))
                            .OrderBy(s => s.Md).ToList();
        if (input.Count == 0) throw new ArgumentException("Wellpath needs at least one survey station.");
        var pts = new List<SurveyPoint> { new(input[0].Md, input[0].IncDeg, input[0].AziDeg, tieN, tieE, tieTvd, 0, 0) };
        for (int k = 1; k < input.Count; k++)
        {
            var p = pts[^1];
            var s = input[k];
            double dMd = s.Md - p.Md;
            if (dMd < 0.001) continue;
            var (dn, de, dt, beta) = MinimumCurvature.Step(p.IncDeg * MinimumCurvature.Deg, p.AziDeg * MinimumCurvature.Deg,
                                                           s.IncDeg * MinimumCurvature.Deg, s.AziDeg * MinimumCurvature.Deg, dMd);
            double betaDeg = beta / MinimumCurvature.Deg;
            pts.Add(new SurveyPoint(s.Md, s.IncDeg, s.AziDeg, p.North + dn, p.East + de, p.Tvd + dt, betaDeg, betaDeg / dMd * 100));
        }
        return new Wellpath(pts.ToArray());
    }

    /// <summary>Position and direction at any MD (see class remarks for extrapolation).</summary>
    public SurveyPoint At(double md)
    {
        if (md <= _md[0] || _st.Length == 1)
            return Straight(_st[0], md - _st[0].Md);
        if (md >= _md[^1])
            return Straight(_st[^1], md - _st[^1].Md);

        int i = Array.BinarySearch(_md, md);
        if (i >= 0) return _st[i];
        i = ~i - 1; // segment [i, i+1]
        var a = _st[i];
        var b = _st[i + 1];
        double i1 = a.IncDeg * MinimumCurvature.Deg, a1 = a.AziDeg * MinimumCurvature.Deg;
        double i2 = b.IncDeg * MinimumCurvature.Deg, a2 = b.AziDeg * MinimumCurvature.Deg;
        double beta = MinimumCurvature.Dogleg(i1, a1, i2, a2);
        double frac = (md - a.Md) / (b.Md - a.Md);
        double inc, azi;
        if (beta < 1e-9)
        {
            inc = i1; azi = a1;
        }
        else
        {
            // Great-circle (slerp) interpolation of the unit tangent.
            double th = frac * beta, sb = Math.Sin(beta);
            var t1 = MinimumCurvature.Tangent(i1, a1);
            var t2 = MinimumCurvature.Tangent(i2, a2);
            double w1 = Math.Sin(beta - th) / sb, w2 = Math.Sin(th) / sb;
            (inc, azi) = MinimumCurvature.Direction(w1 * t1.n + w2 * t2.n, w1 * t1.e + w2 * t2.e, w1 * t1.v + w2 * t2.v);
        }
        // Position: minimum-curvature step from station a to the interpolated point. Because the
        // interpolated point lies on the same circular arc, this reproduces the arc exactly.
        var (dn, de, dt, sub) = MinimumCurvature.Step(i1, a1, inc, azi, md - a.Md);
        return new SurveyPoint(md, inc / MinimumCurvature.Deg, azi / MinimumCurvature.Deg,
                               a.North + dn, a.East + de, a.Tvd + dt, b.DoglegDeg, b.DlsPer100);
    }

    static SurveyPoint Straight(SurveyPoint s, double dMd)
    {
        var (n, e, v) = MinimumCurvature.Tangent(s.IncDeg * MinimumCurvature.Deg, s.AziDeg * MinimumCurvature.Deg);
        return new SurveyPoint(s.Md + dMd, s.IncDeg, s.AziDeg, s.North + n * dMd, s.East + e * dMd, s.Tvd + v * dMd, 0, 0);
    }

    /// <summary>Vertical-section distance of (north, east) for a section azimuth (deg) and origin.</summary>
    public static double VerticalSection(double north, double east, double azimuthDeg, double originN = 0, double originE = 0)
    {
        double a = azimuthDeg * MinimumCurvature.Deg;
        return (north - originN) * Math.Cos(a) + (east - originE) * Math.Sin(a);
    }
}
