namespace GordonGeoSteer.Engine.Geology;

/// <summary>One interpreter geology-modification pick along the well path.</summary>
public sealed record GeoModPick(double Md, double[] DeltaTvd, double[] Slope, double FaultThrow);

/// <summary>
/// The interpreter's structural adjustments along the well path ("geomod"): for every modeled bed a
/// TVD shift as a function of MD, plus a fault shift.
///
/// Dip shift for bed k:
///   - before the first pick: the first pick's value (constant);
///   - between picks: cubic Hermite using each pick's value and slope (dDeltaTVD/dMD);
///   - after the last pick: the slope decays to zero over the decay distance D (see
///     <see cref="Hermite.DecayValue"/>), constant beyond D.
/// Fault shift: a pick with a non-zero throw F_i contributes a Hermite ramp from F_i at MD_i down to 0
/// at the next pick (zero end slopes) - by the next pick the throw has been folded into that pick's
/// bed shifts. After the last pick its throw stays at F_n (zero-slope tail). Before the first pick 0.
/// </summary>
public sealed class GeoModPath
{
    readonly GeoModPick[] _p;
    readonly double[] _md;

    public int BedCount { get; }
    public double DecayDistance { get; }
    public IReadOnlyList<GeoModPick> Picks => _p;

    public GeoModPath(IEnumerable<GeoModPick> picks, int bedCount, double decayDistance)
    {
        _p = picks.OrderBy(p => p.Md).ToArray();
        _md = _p.Select(p => p.Md).ToArray();
        BedCount = bedCount;
        DecayDistance = decayDistance > 0 ? decayDistance : 10;
        foreach (var p in _p)
            if (p.DeltaTvd.Length < bedCount || p.Slope.Length < bedCount)
                throw new ArgumentException($"geomod pick at MD {p.Md} has fewer than {bedCount} bed entries");
    }

    public static GeoModPath Empty(int bedCount) => new(Array.Empty<GeoModPick>(), bedCount, 500);

    public bool IsEmpty => _p.Length == 0;

    /// <summary>Dip (structural) TVD shift of bed <paramref name="bed"/> at MD.</summary>
    public double DipShift(int bed, double md)
    {
        if (_p.Length == 0 || bed < 0 || bed >= BedCount) return 0;
        if (md <= _md[0]) return _p[0].DeltaTvd[bed];
        var last = _p[^1];
        if (md >= last.Md) return Hermite.DecayValue(last.DeltaTvd[bed], last.Slope[bed], DecayDistance, md - last.Md);
        int i = Segment(md);
        var a = _p[i]; var b = _p[i + 1];
        return Hermite.Value(a.Md, a.DeltaTvd[bed], a.Slope[bed], b.Md, b.DeltaTvd[bed], b.Slope[bed], md);
    }

    /// <summary>d(dip shift)/dMD of bed at MD (consistent with <see cref="DipShift"/>, including the decay tail).</summary>
    public double DipSlope(int bed, double md)
    {
        if (_p.Length == 0 || bed < 0 || bed >= BedCount) return 0;
        if (md <= _md[0]) return 0;
        var last = _p[^1];
        if (md >= last.Md) return Hermite.DecaySlope(last.Slope[bed], DecayDistance, md - last.Md);
        int i = Segment(md);
        var a = _p[i]; var b = _p[i + 1];
        return Hermite.Slope(a.Md, a.DeltaTvd[bed], a.Slope[bed], b.Md, b.DeltaTvd[bed], b.Slope[bed], md);
    }

    /// <summary>Fault TVD shift at MD from the picks' fault throws.</summary>
    public double FaultShift(double md)
    {
        if (_p.Length == 0 || md < _md[0]) return 0;
        var last = _p[^1];
        if (md >= last.Md) return last.FaultThrow;
        int i = Segment(md);
        var a = _p[i]; var b = _p[i + 1];
        if (a.FaultThrow == 0) return 0;
        return Hermite.Value(a.Md, a.FaultThrow, 0, b.Md, 0, 0, md);
    }

    int Segment(double md)
    {
        int i = Array.BinarySearch(_md, md);
        if (i >= 0) return Math.Min(i, _md.Length - 2);
        return ~i - 1;
    }
}

/// <summary>
/// Grid fault table: a step function of distance across the fault strike,
/// d = (E - E0) sin(az) + (N - N0) cos(az). Rows are scanned last to first; the first row whose
/// distance is less than d gives the TVD shift, otherwise 0.
/// </summary>
public sealed class FaultTable
{
    readonly (double dist, double shift)[] _rows;
    readonly double _sin, _cos, _n0, _e0;

    public FaultTable(double azimuthDeg, double originN, double originE, IEnumerable<(double dist, double shift)> rows)
    {
        _rows = rows.ToArray();
        double a = azimuthDeg * Math.PI / 180;
        _sin = Math.Sin(a); _cos = Math.Cos(a); _n0 = originN; _e0 = originE;
    }

    public static readonly FaultTable None = new(0, 0, 0, Array.Empty<(double, double)>());
    public int Count => _rows.Length;

    public double DeltaTvd(double north, double east)
    {
        if (_rows.Length == 0) return 0;
        double d = (east - _e0) * _sin + (north - _n0) * _cos;
        for (int k = _rows.Length - 1; k >= 0; k--)
            if (_rows[k].dist < d) return _rows[k].shift;
        return 0;
    }
}
