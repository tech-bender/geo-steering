namespace GordonGeoSteer.Engine.Geology;

public enum BedGeoType { Stretch = 0, TruncateUp = 1, TruncateDown = 2 }

/// <summary>A gridded model bed: its surface, its depth index (DI) and its geomod column.</summary>
public sealed record GridBed(string Name, double DepthIndex, double SourceWellMd, BicubicSurface Surface, BedGeoType GeoType, int GeoModIndex);

/// <summary>A marker horizon picked on the offset well: no grid of its own, placed through the DI model.</summary>
public sealed record MarkerBed(string Name, double DepthIndex, double SourceWellMd);

/// <summary>
/// Structural model: gridded beds (ordered by depth index) + geomod shifts + faults, and the
/// stratigraphic "depth index" (DI) model that maps any DI to a TVD at a location and back.
/// The DI of a bed is its MD in the offset (source) well, so the DI of a lateral sample is the offset
/// well MD it correlates to.
///
/// At a lateral location (N, E, MD) let z_i = bed i grid depth + bed i geomod dip shift (no faults)
/// and dip_i the local bed dip. Then
///   above the top bed:   TVD = z_0 - (DI_0 - di)/cos(dip_0)
///   below the last bed:  TVD = z_L + (di - DI_L)/cos(dip_L)
///   between i and i+1:   bed i TruncateDown: z_i + (di - DI_i)/cos(dip_i), capped at z_{i+1}
///                        else bed i+1 TruncateUp: z_{i+1} - (DI_{i+1} - di)/cos(dip_{i+1}), floored at z_i
///                        else (Stretch): linear in DI between z_i and z_{i+1}.
/// The total fault shift (grid fault table + geomod fault ramp) is added to the result.
/// Bed depths are forced non-decreasing with DI (a bed is never shallower than the bed above it).
/// </summary>
public sealed class StructureModel
{
    readonly GridBed[] _beds;

    public IReadOnlyList<GridBed> Beds => _beds;
    public IReadOnlyList<MarkerBed> Markers { get; }
    public GeoModPath GeoMod { get; }
    public FaultTable Faults { get; }

    public StructureModel(IEnumerable<GridBed> beds, IEnumerable<MarkerBed> markers, GeoModPath geoMod, FaultTable? faults = null)
    {
        _beds = beds.OrderBy(b => b.DepthIndex).ToArray();
        if (_beds.Length == 0) throw new ArgumentException("structure model needs at least one gridded bed");
        Markers = markers.OrderBy(m => m.DepthIndex).ToList();
        GeoMod = geoMod;
        Faults = faults ?? FaultTable.None;
    }

    /// <summary>Per-location state needed by the DI model (computed once per location, reused for many DIs).</summary>
    public readonly struct Column
    {
        public readonly double[] Z;      // dip-shifted bed depths (no faults), non-decreasing
        public readonly double[] CosDip; // cos of local dip per bed
        public readonly double Fault;    // total fault shift
        internal Column(double[] z, double[] c, double f) { Z = z; CosDip = c; Fault = f; }
    }

    /// <summary>
    /// Evaluates bed depths, dips and fault shift at a well-path location. <paramref name="aziDeg"/>
    /// is the along-hole azimuth used to add the geomod slope (dShift/dMD treated as a horizontal
    /// gradient along the hole, a near-horizontal-well approximation) to the grid gradient.
    /// Set <paramref name="withDips"/> false to skip dip evaluation (cos = 1).
    /// </summary>
    public Column At(double north, double east, double md, double aziDeg = 0, bool withDips = true)
    {
        int n = _beds.Length;
        var z = new double[n];
        var c = new double[n];
        double sa = Math.Sin(aziDeg * Math.PI / 180), ca = Math.Cos(aziDeg * Math.PI / 180);
        for (int i = 0; i < n; i++)
        {
            var b = _beds[i];
            z[i] = b.Surface.Evaluate(east, north) + GeoMod.DipShift(b.GeoModIndex, md);
            if (i > 0 && z[i] < z[i - 1]) z[i] = z[i - 1];
            if (withDips)
            {
                var (gE, gN) = b.Surface.Gradient(east, north);
                double s = GeoMod.DipSlope(b.GeoModIndex, md);
                gE += s * sa; gN += s * ca;
                c[i] = 1.0 / Math.Sqrt(1 + gE * gE + gN * gN);
            }
            else c[i] = 1;
        }
        double fault = Faults.DeltaTvd(north, east) + GeoMod.FaultShift(md);
        return new Column(z, c, fault);
    }

    /// <summary>TVD (fault shift included) of depth index <paramref name="di"/> in a column.</summary>
    public double TvdFromDi(in Column col, double di) => TvdFromDiNoFault(col, di) + col.Fault;

    public double TvdFromDiNoFault(in Column col, double di)
    {
        var z = col.Z; var c = col.CosDip;
        int L = _beds.Length - 1;
        if (di <= _beds[0].DepthIndex) return z[0] - (_beds[0].DepthIndex - di) / c[0];
        if (di >= _beds[L].DepthIndex) return z[L] + (di - _beds[L].DepthIndex) / c[L];
        int i = 0;
        while (i < L - 1 && di >= _beds[i + 1].DepthIndex) i++;
        var bi = _beds[i]; var bj = _beds[i + 1];
        double span = bj.DepthIndex - bi.DepthIndex;
        if (span <= 1e-12) return 0.5 * (z[i] + z[i + 1]);
        if (bi.GeoType == BedGeoType.TruncateDown) return Math.Min(z[i] + (di - bi.DepthIndex) / c[i], z[i + 1]);
        if (bj.GeoType == BedGeoType.TruncateUp) return Math.Max(z[i + 1] - (bj.DepthIndex - di) / c[i + 1], z[i]);
        return z[i] + (di - bi.DepthIndex) / span * (z[i + 1] - z[i]);
    }

    /// <summary>Depth index of a TVD in a column (inverse of <see cref="TvdFromDi"/>; the fault shift is removed first).</summary>
    public double DiFromTvd(in Column col, double tvd)
    {
        double t = tvd - col.Fault;
        var z = col.Z; var c = col.CosDip;
        int L = _beds.Length - 1;
        if (t <= z[0]) return _beds[0].DepthIndex - (z[0] - t) * c[0];
        if (t >= z[L]) return _beds[L].DepthIndex + (t - z[L]) * c[L];
        int i = 0;
        while (i < L - 1 && t >= z[i + 1]) i++;
        var bi = _beds[i]; var bj = _beds[i + 1];
        double thick = z[i + 1] - z[i];
        if (bi.GeoType == BedGeoType.TruncateDown)
            return Math.Min(bi.DepthIndex + (t - z[i]) * c[i], bj.DepthIndex);
        if (bj.GeoType == BedGeoType.TruncateUp)
            return Math.Max(bj.DepthIndex - (z[i + 1] - t) * c[i + 1], bi.DepthIndex);
        if (thick <= 1e-12) return 0.5 * (bi.DepthIndex + bj.DepthIndex);
        return bi.DepthIndex + (t - z[i]) / thick * (bj.DepthIndex - bi.DepthIndex);
    }

    /// <summary>Finds a grid bed or marker by name (case-insensitive) and returns its DI, or null.</summary>
    public double? DepthIndexOf(string name)
    {
        var b = _beds.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (b != null) return b.DepthIndex;
        var m = Markers.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return m?.DepthIndex;
    }
}
