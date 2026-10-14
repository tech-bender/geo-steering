using GordonGeoSteer.Engine.Geology;
using GordonGeoSteer.Engine.Survey;

namespace GordonGeoSteer.Engine.Steering;

/// <summary>How lateral samples are correlated to the offset well.</summary>
public enum CorrelationMode
{
    /// <summary>
    /// TVT parallel to the reference bed: TVT = TVD - reference bed depth (incl. geomod dip and fault
    /// shifts). The offset log is looked up at the offset depth with the same TVT below the reference
    /// bed. This is the definition in the steering-view spec (default).
    /// </summary>
    ParallelToReference,

    /// <summary>
    /// Depth-index stretch: the lateral sample's depth index (= correlated offset-well MD) comes from
    /// the DI model, which stretches/squeezes the interval between gridded beds. TVT is then expressed in
    /// the offset well's frame (offset TVD at that DI minus offset TVD of the reference bed).
    /// This reproduces the 3rd column of PZS's _M model curves.
    /// </summary>
    DepthIndexStretch,
}

/// <summary>The offset (source/pilot) well: its path (TVD vs MD) and its location in project coordinates.</summary>
public sealed class OffsetWell
{
    /// <summary>Offset wellpath in project coordinates (location and tie-in already applied).</summary>
    public Wellpath Path { get; }

    readonly double _md0, _step;
    readonly double[] _tvd; // project-frame TVD on a regular MD grid
    readonly bool _tvdMonotone;

    public OffsetWell(Wellpath path, double mdMin, double mdMax, double step = 0.25)
    {
        Path = path;
        _md0 = Math.Min(mdMin, path.FirstMd);
        double mdEnd = Math.Max(mdMax, path.LastMd);
        _step = step;
        int n = (int)Math.Ceiling((mdEnd - _md0) / step) + 1;
        _tvd = new double[n];
        bool mono = true;
        for (int i = 0; i < n; i++)
        {
            _tvd[i] = path.At(_md0 + i * step).Tvd;
            if (i > 0 && _tvd[i] < _tvd[i - 1]) mono = false;
        }
        _tvdMonotone = mono;
    }

    public double TvdAtMd(double md)
    {
        double f = (md - _md0) / _step;
        if (f < 0 || f > _tvd.Length - 1) return Path.At(md).Tvd;
        int i = Math.Min((int)f, _tvd.Length - 2);
        double t = f - i;
        return _tvd[i] + t * (_tvd[i + 1] - _tvd[i]);
    }

    /// <summary>MD at which the offset well reaches a TVD (offset wells are near-vertical, so TVD is monotone in MD).</summary>
    public double MdAtTvd(double tvd)
    {
        if (!_tvdMonotone) throw new InvalidOperationException("Offset well TVD is not monotone in MD; TVD->MD inversion is ambiguous.");
        int n = _tvd.Length;
        if (tvd <= _tvd[0]) return _md0 - (_tvd[0] - tvd); // above the table: treat as vertical
        if (tvd >= _tvd[n - 1]) return _md0 + (n - 1) * _step + (tvd - _tvd[n - 1]);
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int k = (lo + hi) >> 1;
            if (_tvd[k] > tvd) hi = k; else lo = k;
        }
        double d = _tvd[hi] - _tvd[lo];
        double t = d <= 0 ? 0 : (tvd - _tvd[lo]) / d;
        return _md0 + (lo + t) * _step;
    }
}

/// <summary>Lateral (actual well) quantities on a regular MD grid, for one reference bed.</summary>
public sealed class LateralTable
{
    public double Md0 { get; init; }
    public double Step { get; init; }
    public double[] Md { get; init; } = Array.Empty<double>();
    public double[] North { get; init; } = Array.Empty<double>();
    public double[] East { get; init; } = Array.Empty<double>();
    public double[] Tvd { get; init; } = Array.Empty<double>();
    public double[] Vs { get; init; } = Array.Empty<double>();
    public double[] Inc { get; init; } = Array.Empty<double>();
    /// <summary>Reference bed TVD at the sample (grid + geomod dip shift + faults, or via DI for markers).</summary>
    public double[] RefDepth { get; init; } = Array.Empty<double>();
    /// <summary>TVD - RefDepth (spec definition), positive down.</summary>
    public double[] Tvt { get; init; } = Array.Empty<double>();
    /// <summary>Depth index (correlated offset-well MD) from the DI model.</summary>
    public double[] Di { get; init; } = Array.Empty<double>();
    /// <summary>TVT in the offset well's frame for the DI-stretch mode.</summary>
    public double[] TvtStretch { get; init; } = Array.Empty<double>();

    public int Count => Md.Length;

    /// <summary>Linear interpolation of one of the arrays at an MD (NaN outside the table).</summary>
    public double Interp(double[] arr, double md)
    {
        double f = (md - Md0) / Step;
        if (double.IsNaN(f) || f < 0 || f > Md.Length - 1) return double.NaN;
        int i = Math.Min((int)f, Md.Length - 2);
        if (i < 0) return arr[0];
        double t = f - i;
        return arr[i] + t * (arr[i + 1] - arr[i]);
    }
}

/// <summary>Bed depths across the curtain (vertical-section) axis.</summary>
public sealed class CurtainProfile
{
    public double[] X { get; init; } = Array.Empty<double>();
    public double[] Md { get; init; } = Array.Empty<double>();
    public double[] RefDepth { get; init; } = Array.Empty<double>();
    /// <summary>Imported bed (grid bed or marker) name -> TVD per X.</summary>
    public Dictionary<string, double[]> Beds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The geosteering model used by the steering view: lateral wellpath, offset well, structure, vertical
/// section definition, reference bed and correlation mode. Computes TVT, depth index, synthetic
/// (modeled) logs and curtain bed profiles.
/// </summary>
public sealed class SteeringModel
{
    public Wellpath Lateral { get; }
    public OffsetWell Offset { get; }
    public StructureModel Structure { get; }
    public double VsAzimuthDeg { get; }
    public double VsOriginN { get; }
    public double VsOriginE { get; }

    public string ReferenceBed { get; private set; }
    public double ReferenceDi { get; private set; }
    public double ReferenceOffsetMd { get; private set; }
    public double ReferenceOffsetTvd { get; private set; }
    public CorrelationMode Mode { get; set; } = CorrelationMode.ParallelToReference;

    public double TableStep { get; }
    readonly double _tableMd0, _tableMd1;
    LateralTable? _table;

    public SteeringModel(Wellpath lateral, OffsetWell offset, StructureModel structure, double vsAzimuthDeg, double vsOriginN, double vsOriginE,
                         string referenceBed, double tableMdMin, double tableMdMax, double tableStep = 0.5)
    {
        Lateral = lateral; Offset = offset; Structure = structure;
        VsAzimuthDeg = vsAzimuthDeg; VsOriginN = vsOriginN; VsOriginE = vsOriginE;
        TableStep = tableStep;
        _tableMd0 = tableMdMin; _tableMd1 = Math.Max(tableMdMax, tableMdMin + tableStep);
        ReferenceBed = "";
        SetReferenceBed(referenceBed);
    }

    public IEnumerable<string> BedNames =>
        Structure.Beds.Select(b => b.Name).Concat(Structure.Markers.Select(m => m.Name));

    /// <summary>All imported beds (grid beds and markers) with their DI and offset-well MD, ordered by DI.</summary>
    public IReadOnlyList<(string name, double di, double offsetMd, bool isGrid)> ImportedBeds =>
        Structure.Beds.Select(b => (b.Name, b.DepthIndex, b.SourceWellMd, true))
            .Concat(Structure.Markers.Select(m => (m.Name, m.DepthIndex, m.SourceWellMd, false)))
            .OrderBy(t => t.Item2).ToList();

    public void SetReferenceBed(string name)
    {
        var bed = ImportedBeds.FirstOrDefault(b => string.Equals(b.name, name, StringComparison.OrdinalIgnoreCase));
        if (bed.name == null) bed = ImportedBeds.First(b => b.isGrid);
        ReferenceBed = bed.name;
        ReferenceDi = bed.di;
        ReferenceOffsetMd = bed.offsetMd;
        ReferenceOffsetTvd = Offset.TvdAtMd(bed.offsetMd);
        _table = null;
    }

    public double Vs(double north, double east) => Wellpath.VerticalSection(north, east, VsAzimuthDeg, VsOriginN, VsOriginE);

    /// <summary>TVT of an imported bed in the offset-well frame (offset TVD of the bed minus offset TVD of the reference).</summary>
    public double OffsetTvtOfBed(string name)
    {
        var bed = ImportedBeds.FirstOrDefault(b => string.Equals(b.name, name, StringComparison.OrdinalIgnoreCase));
        if (bed.name == null) return double.NaN;
        return Offset.TvdAtMd(bed.offsetMd) - ReferenceOffsetTvd;
    }

    /// <summary>TVT of an offset-well MD (offset log depth -> TVT axis).</summary>
    public double OffsetTvt(double offsetMd) => Offset.TvdAtMd(offsetMd) - ReferenceOffsetTvd;

    public LateralTable Table => _table ??= BuildTable();

    LateralTable BuildTable()
    {
        int n = (int)Math.Ceiling((_tableMd1 - _tableMd0) / TableStep) + 1;
        var md = new double[n]; var N = new double[n]; var E = new double[n]; var tvd = new double[n]; var vs = new double[n];
        var inc = new double[n]; var refd = new double[n]; var tvt = new double[n]; var di = new double[n]; var tvts = new double[n];
        Parallel.For(0, n, i =>
        {
            double m = _tableMd0 + i * TableStep;
            var p = Lateral.At(m);
            md[i] = m; N[i] = p.North; E[i] = p.East; tvd[i] = p.Tvd; inc[i] = p.IncDeg;
            vs[i] = Vs(p.North, p.East);
            var col = Structure.At(p.North, p.East, m, p.AziDeg);
            refd[i] = Structure.TvdFromDi(col, ReferenceDi);
            tvt[i] = p.Tvd - refd[i];
            di[i] = Structure.DiFromTvd(col, p.Tvd);
            tvts[i] = Offset.TvdAtMd(di[i]) - ReferenceOffsetTvd;
        });
        return new LateralTable
        {
            Md0 = _tableMd0, Step = TableStep, Md = md, North = N, East = E, Tvd = tvd, Vs = vs, Inc = inc,
            RefDepth = refd, Tvt = tvt, Di = di, TvtStretch = tvts,
        };
    }

    /// <summary>TVT used for plotting a lateral sample at MD in the current mode.</summary>
    public double PlotTvt(double md) => Mode == CorrelationMode.DepthIndexStretch ? Table.Interp(Table.TvtStretch, md) : Table.Interp(Table.Tvt, md);

    /// <summary>Offset-well MD that a lateral sample at MD correlates to, in the current mode.</summary>
    public double CorrelatedOffsetMd(double md)
    {
        if (Mode == CorrelationMode.DepthIndexStretch) return Table.Interp(Table.Di, md);
        double tvt = Table.Interp(Table.Tvt, md);
        if (double.IsNaN(tvt)) return double.NaN;
        return Offset.MdAtTvd(ReferenceOffsetTvd + tvt);
    }

    /// <summary>Lateral curve (MD, value) -> (TVT, value) in sample order, for vertical tracks.</summary>
    public AxisSeries LateralOnTvt(DepthSeries s)
    {
        var a = new double[s.Count];
        for (int i = 0; i < s.Count; i++) a[i] = PlotTvt(s.Depth[i]);
        return new AxisSeries(a, s.Value);
    }

    /// <summary>Lateral curve (MD, value) -> (vertical section, value), for horizontal tracks.</summary>
    public AxisSeries LateralOnVs(DepthSeries s)
    {
        var t = Table;
        var a = new double[s.Count];
        for (int i = 0; i < s.Count; i++) a[i] = t.Interp(t.Vs, s.Depth[i]);
        return new AxisSeries(a, s.Value);
    }

    /// <summary>Offset curve (offset MD, value) -> (TVT, value), for vertical tracks.</summary>
    public AxisSeries OffsetOnTvt(DepthSeries s)
    {
        var a = new double[s.Count];
        for (int i = 0; i < s.Count; i++) a[i] = OffsetTvt(s.Depth[i]);
        return new AxisSeries(a, s.Value);
    }

    /// <summary>
    /// Synthetic ("modeled") curve along the lateral: for every lateral MD, the offset log value at the
    /// offset depth this sample correlates to (same TVT in parallel mode; same DI in stretch mode).
    /// </summary>
    public DepthSeries Synthetic(DepthSeries offsetCurve, double mdStep = 0.5, double? mdMin = null, double? mdMax = null)
    {
        double m0 = mdMin ?? _tableMd0, m1 = mdMax ?? _tableMd1;
        int n = (int)Math.Floor((m1 - m0) / mdStep) + 1;
        var md = new double[n];
        var v = new double[n];
        for (int i = 0; i < n; i++)
        {
            md[i] = m0 + i * mdStep;
            v[i] = offsetCurve.ValueAt(CorrelatedOffsetMd(md[i]));
        }
        return new DepthSeries(md, v);
    }

    /// <summary>
    /// Bed depths along the curtain from <paramref name="xMin"/> to <paramref name="xMax"/>. Inside the
    /// vertical-section span of the lateral each X is mapped to the MD where the well first reaches that
    /// vertical section (running maximum, so build-section wobble is ignored) and beds are evaluated at
    /// the well's N/E with that MD's geomod shifts. Outside that span, points lie on the section line
    /// through the origin and use the shifts of the nearest end of the well.
    /// </summary>
    public CurtainProfile BuildCurtain(double xMin, double xMax, double step)
    {
        var t = Table;
        // monotone (running max) VS -> MD map
        var mx = new List<double>(); var mm = new List<double>();
        double best = double.NegativeInfinity;
        for (int i = 0; i < t.Count; i++)
            if (t.Vs[i] > best + 1e-6) { best = t.Vs[i]; mx.Add(t.Vs[i]); mm.Add(t.Md[i]); }
        int n = Math.Max(2, (int)Math.Ceiling((xMax - xMin) / step) + 1);
        var X = new double[n]; var MD = new double[n]; var R = new double[n];
        var beds = ImportedBeds;
        var bedArr = beds.Select(_ => new double[n]).ToArray();
        double az = VsAzimuthDeg * Math.PI / 180;
        Parallel.For(0, n, k =>
        {
            double x = xMin + k * (xMax - xMin) / (n - 1);
            double md, north, east, azi;
            if (mx.Count >= 2 && x >= mx[0] && x <= mx[^1])
            {
                int j = mx.BinarySearch(x);
                if (j < 0) j = ~j;
                j = Math.Clamp(j, 1, mx.Count - 1);
                double f = (x - mx[j - 1]) / (mx[j] - mx[j - 1]);
                md = mm[j - 1] + f * (mm[j] - mm[j - 1]);
                var p = Lateral.At(md);
                north = p.North; east = p.East; azi = p.AziDeg;
            }
            else
            {
                md = mx.Count == 0 ? t.Md[0] : (x < mx[0] ? mm[0] : mm[^1]);
                north = VsOriginN + x * Math.Cos(az);
                east = VsOriginE + x * Math.Sin(az);
                azi = VsAzimuthDeg;
            }
            var col = Structure.At(north, east, md, azi);
            X[k] = x; MD[k] = md;
            R[k] = Structure.TvdFromDi(col, ReferenceDi);
            for (int b = 0; b < beds.Count; b++) bedArr[b][k] = Structure.TvdFromDi(col, beds[b].di);
        });
        var dict = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        for (int b = 0; b < beds.Count; b++) dict[beds[b].name] = bedArr[b];
        return new CurtainProfile { X = X, Md = MD, RefDepth = R, Beds = dict };
    }
}
