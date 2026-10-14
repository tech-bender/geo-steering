namespace GordonGeoSteer.Engine.Geology;

/// <summary>
/// Natural cubic spline through (x[i], y[i]) with x strictly increasing: second derivatives are found
/// by the standard tridiagonal solve with y'' = 0 at both ends. Evaluation outside [x0, xn] is the
/// caller's responsibility (the bed-grid code clamps before evaluating).
/// </summary>
public sealed class NaturalSpline
{
    readonly double[] _x, _y, _y2;

    public NaturalSpline(double[] x, double[] y)
    {
        if (x.Length != y.Length) throw new ArgumentException("x and y lengths differ");
        if (x.Length == 0) throw new ArgumentException("empty spline");
        _x = x; _y = y;
        _y2 = SecondDerivatives(x, y);
    }

    /// <summary>Natural-spline second derivatives (y''=0 at both ends).</summary>
    public static double[] SecondDerivatives(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        int n = x.Count;
        var y2 = new double[n];
        if (n < 3) return y2;
        var u = new double[n];
        for (int i = 1; i < n - 1; i++)
        {
            double sig = (x[i] - x[i - 1]) / (x[i + 1] - x[i - 1]);
            double p = sig * y2[i - 1] + 2.0;
            y2[i] = (sig - 1.0) / p;
            double d = (y[i + 1] - y[i]) / (x[i + 1] - x[i]) - (y[i] - y[i - 1]) / (x[i] - x[i - 1]);
            u[i] = (6.0 * d / (x[i + 1] - x[i - 1]) - sig * u[i - 1]) / p;
        }
        y2[n - 1] = 0;
        for (int k = n - 2; k >= 0; k--) y2[k] = y2[k] * y2[k + 1] + u[k];
        return y2;
    }

    public double Evaluate(double xv) => Evaluate(_x, _y, _y2, xv);

    /// <summary>Cubic-spline value at xv given precomputed second derivatives (binary search for the interval).</summary>
    public static double Evaluate(IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<double> y2, double xv)
    {
        int n = x.Count;
        if (n == 1) return y[0];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int k = (hi + lo) >> 1;
            if (x[k] > xv) hi = k; else lo = k;
        }
        double h = x[hi] - x[lo];
        if (h == 0) return y[lo];
        double a = (x[hi] - xv) / h, b = (xv - x[lo]) / h;
        return a * y[lo] + b * y[hi] + ((a * a * a - a) * y2[lo] + (b * b * b - b) * y2[hi]) * (h * h) / 6.0;
    }
}

/// <summary>
/// Cubic Hermite segment between (x0, y0, slope d0) and (x1, y1, slope d1).
/// With h = x1 - x0, s = (x1 - x)/h, t = (x - x0)/h:
///   y = s^2(1+2t) y0 + t s^2 h d0 + t^2(1+2s) y1 - s t^2 h d1
/// </summary>
public static class Hermite
{
    public static double Value(double x0, double y0, double d0, double x1, double y1, double d1, double x)
    {
        double h = x1 - x0;
        if (Math.Abs(h) < 1e-12) return y0;
        double s = (x1 - x) / h, t = (x - x0) / h;
        return s * s * (1 + 2 * t) * y0 + t * s * s * h * d0 + t * t * (1 + 2 * s) * y1 - s * t * t * h * d1;
    }

    /// <summary>dy/dx of the same segment.</summary>
    public static double Slope(double x0, double y0, double d0, double x1, double y1, double d1, double x)
    {
        double h = x1 - x0;
        if (Math.Abs(h) < 1e-12) return d0;
        double t = (x - x0) / h;
        // In terms of t: y = h00 y0 + h10 h d0 + h01 y1 + h11 h d1 (standard basis), derivative / h.
        double dh00 = 6 * t * t - 6 * t, dh10 = 3 * t * t - 4 * t + 1, dh01 = -6 * t * t + 6 * t, dh11 = 3 * t * t - 2 * t;
        return (dh00 * y0 + dh01 * y1) / h + dh10 * d0 + dh11 * d1;
    }

    /// <summary>
    /// Tail beyond the last pick: starting at (y, slope) the slope decays to zero over distance D with
    /// zero curvature at D: y(d) = y + s d - (s/D) d^2 + (s/(3 D^2)) d^3 for 0 &lt;= d &lt;= D, constant after.
    /// </summary>
    public static double DecayValue(double y, double slope, double decay, double d)
    {
        if (d <= 0) return y;
        if (d > decay) d = decay;
        return y + slope * d - slope / decay * d * d + slope / (3 * decay * decay) * d * d * d;
    }

    public static double DecaySlope(double slope, double decay, double d)
    {
        if (d <= 0) return slope;
        if (d >= decay) return 0;
        return slope - 2 * slope / decay * d + slope / (decay * decay) * d * d;
    }
}
