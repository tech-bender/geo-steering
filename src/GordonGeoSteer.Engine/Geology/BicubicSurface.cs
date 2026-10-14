namespace GordonGeoSteer.Engine.Geology;

/// <summary>
/// A gridded bed surface z(East, North) evaluated with a natural bicubic spline: for every East grid
/// column a natural spline along North is evaluated at the requested North, then a natural spline
/// across East through those values is evaluated at the requested East. Points outside the grid are
/// clamped to the grid edge (the surface is flat-extended, not extrapolated).
/// </summary>
public sealed class BicubicSurface
{
    readonly double[] _east, _north;
    readonly double[][] _colZ;   // [iEast][iNorth]
    readonly double[][] _colY2;  // natural-spline 2nd derivatives along North, per East column

    public double MinEast => _east[0];
    public double MaxEast => _east[^1];
    public double MinNorth => _north[0];
    public double MaxNorth => _north[^1];

    /// <param name="eastAxis">strictly increasing East coordinates (nE)</param>
    /// <param name="northAxis">strictly increasing North coordinates (nN)</param>
    /// <param name="z">z[iNorth][iEast]</param>
    public BicubicSurface(double[] eastAxis, double[] northAxis, double[][] z)
    {
        if (eastAxis.Length < 1 || northAxis.Length < 1) throw new ArgumentException("empty grid axes");
        if (z.Length != northAxis.Length || z.Any(r => r.Length != eastAxis.Length))
            throw new ArgumentException($"grid shape mismatch: expected {northAxis.Length} rows of {eastAxis.Length}");
        for (int i = 1; i < eastAxis.Length; i++) if (!(eastAxis[i] > eastAxis[i - 1])) throw new ArgumentException("East axis not increasing");
        for (int i = 1; i < northAxis.Length; i++) if (!(northAxis[i] > northAxis[i - 1])) throw new ArgumentException("North axis not increasing");
        _east = eastAxis; _north = northAxis;
        _colZ = new double[eastAxis.Length][];
        _colY2 = new double[eastAxis.Length][];
        for (int j = 0; j < eastAxis.Length; j++)
        {
            _colZ[j] = new double[northAxis.Length];
            for (int i = 0; i < northAxis.Length; i++) _colZ[j][i] = z[i][j];
            _colY2[j] = NaturalSpline.SecondDerivatives(_north, _colZ[j]);
        }
    }

    public double Evaluate(double east, double north)
    {
        east = Math.Clamp(east, _east[0], _east[^1]);
        north = Math.Clamp(north, _north[0], _north[^1]);
        int nE = _east.Length;
        var arr = new double[nE];
        for (int j = 0; j < nE; j++) arr[j] = NaturalSpline.Evaluate(_north, _colZ[j], _colY2[j], north);
        if (nE == 1) return arr[0];
        var y2 = NaturalSpline.SecondDerivatives(_east, arr);
        return NaturalSpline.Evaluate(_east, arr, y2, east);
    }

    /// <summary>
    /// Surface gradient (dz/dEast, dz/dNorth) by central differences. The step starts at 8 length units
    /// and is halved until the implied dip angle changes by less than 0.01 rad (at most 8 halvings).
    /// </summary>
    public (double gE, double gN) Gradient(double east, double north)
    {
        double h = 8;
        (double gE, double gN) g = Diff(east, north, h);
        double dip = Math.Atan(Math.Sqrt(g.gE * g.gE + g.gN * g.gN));
        for (int it = 0; it < 8; it++)
        {
            h /= 2;
            var g2 = Diff(east, north, h);
            double dip2 = Math.Atan(Math.Sqrt(g2.gE * g2.gE + g2.gN * g2.gN));
            g = g2;
            if (Math.Abs(dip2 - dip) < 0.01) break;
            dip = dip2;
        }
        return g;
    }

    (double gE, double gN) Diff(double e, double n, double h) =>
        ((Evaluate(e + h, n) - Evaluate(e - h, n)) / (2 * h), (Evaluate(e, n + h) - Evaluate(e, n - h)) / (2 * h));
}
