using System.Text.Json;
using GordonGeoSteer.Engine.Geology;
using GordonGeoSteer.Engine.Survey;
using GordonGeoSteer.Model;
using GordonGeoSteer.Services;

namespace GordonGeoSteer.Engine.Steering;

/// <summary>Where a curve comes from, and so how it is positioned on the tracks.</summary>
public enum CurveOrigin
{
    /// <summary>Realtime (actual well) curve, indexed by lateral MD.</summary>
    Realtime,
    /// <summary>Offset (source/pilot) well curve, indexed by offset MD.</summary>
    Offset,
    /// <summary>Synthetic curve computed by this engine from an offset curve, indexed by lateral MD.</summary>
    Synthetic,
    /// <summary>PZS's own stored model curve (_M datasets in the model DB), indexed by lateral MD.</summary>
    PzsModel,
}

/// <summary>A curve the steering view can plot. Id is stable for workspace files: "rt:GRCA", "off:GR", "syn:GR", "pzs:RDAH_M".</summary>
public sealed record CurveRef(CurveOrigin Origin, string Name, string? Unit, string? Description)
{
    public string Id => Prefix(Origin) + Name;
    public string Display => Origin switch
    {
        CurveOrigin.Realtime => Name + " (realtime)",
        CurveOrigin.Offset => Name + " (offset)",
        CurveOrigin.Synthetic => Name + " (synthetic)",
        _ => Name + " (PZS model)",
    };
    public bool IsLateral => Origin != CurveOrigin.Offset;

    public static string Prefix(CurveOrigin o) => o switch
    {
        CurveOrigin.Realtime => "rt:",
        CurveOrigin.Offset => "off:",
        CurveOrigin.Synthetic => "syn:",
        _ => "pzs:",
    };

    public static (CurveOrigin origin, string name)? Parse(string id)
    {
        foreach (var o in Enum.GetValues<CurveOrigin>())
            if (id.StartsWith(Prefix(o), StringComparison.OrdinalIgnoreCase)) return (o, id[Prefix(o).Length..]);
        return null;
    }
}

/// <summary>
/// Builds a <see cref="SteeringModel"/> from an imported gordon.geosteering.project document and loads
/// curve data on demand. Uses the model's live geomod point list (project.json models[].geologyModel
/// .steering.points), never the .gmp file named by GMPFileName (that reference is stale in real data).
/// </summary>
public sealed class SteeringProject
{
    public ProjectDocument Document { get; }
    public string ProjectDir { get; }
    public GeoModel GeoModel { get; }
    public Well ActualWell { get; }
    public Well OffsetWellDoc { get; }
    public Well? ModelWellDoc { get; }
    public SteeringModel Model { get; }
    public List<string> Notes { get; } = new();

    readonly Dictionary<string, DepthSeries> _cache = new(StringComparer.OrdinalIgnoreCase);

    SteeringProject(ProjectDocument doc, string dir, GeoModel gm, Well actual, Well offset, Well? modelWell, SteeringModel model)
    {
        Document = doc; ProjectDir = dir; GeoModel = gm; ActualWell = actual; OffsetWellDoc = offset; ModelWellDoc = modelWell; Model = model;
    }

    public static SteeringProject Load(string projectJsonPath, string? referenceBed = null)
    {
        var doc = ProjectJson.LoadProject(projectJsonPath);
        return Build(doc, Path.GetDirectoryName(Path.GetFullPath(projectJsonPath))!, null, referenceBed);
    }

    public static SteeringProject Build(ProjectDocument doc, string projectDir, GeoModel? gm = null, string? referenceBed = null)
    {
        gm ??= doc.Models.FirstOrDefault() ?? throw new InvalidDataException("The project has no geosteering model (.pzscfg).");
        var geo = gm.GeologyModel ?? throw new InvalidDataException($"Model '{gm.Name}' has no geology model.");
        Well? Role(string role) => doc.Wells.FirstOrDefault(w => w.Roles.Any(r => r.Model == gm.Name && r.Role == role));
        var actual = Role("actual") ?? throw new InvalidDataException("No well has the 'actual' (realtime) role for model " + gm.Name);
        var offset = Role("source") ?? throw new InvalidDataException("No well has the 'source' (offset) role for model " + gm.Name);
        var modelWell = Role("model");
        var actualSurveyWell = Role("actualSurvey") ?? actual;
        var offsetSurveyWell = Role("sourceSurvey") ?? offset;

        var notes = new List<string>();

        // ---- wellpaths. Project-frame position = well location + tie-in + minimum-curvature offset.
        var aSet = gm.WellSettings?.Actual;
        var oSet = gm.WellSettings?.Source;
        Wellpath Path(Well w, WellSettings? s)
        {
            var st = w.Surveys?.Stations ?? new();
            if (st.Count == 0) throw new InvalidDataException($"Well '{w.Name}' has no survey stations.");
            var tie = s?.TieIn ?? w.Reference?.TieIn ?? new Point3();
            var loc = s?.Location ?? new Point3();
            return Wellpath.Compute(st.Select(x => new SurveyInput(x.Md, x.Inclination, x.Azimuth)),
                                    loc.North + tie.North, loc.East + tie.East, loc.Tvd + tie.Tvd);
        }
        var lateralPath = Path(actualSurveyWell, aSet);

        var offPath = Path(offsetSurveyWell, oSet);
        // Offset-well TVD table must cover every offset curve depth (logs often run below the last survey).
        double oMin = offPath.FirstMd, oMax = offPath.LastMd;
        foreach (var c in offset.Curves)
        {
            if (!double.IsNaN(c.DepthStart)) { oMin = Math.Min(oMin, Math.Min(c.DepthStart, c.DepthStop)); oMax = Math.Max(oMax, Math.Max(c.DepthStart, c.DepthStop)); }
        }
        foreach (var b in geo.Beds.Concat(geo.Markers)) { oMin = Math.Min(oMin, b.SourceWellMd); oMax = Math.Max(oMax, b.SourceWellMd); }
        var offWell = new OffsetWell(offPath, oMin, oMax + 50);
        if (offPath.LastMd < oMax) notes.Add($"Offset survey ends at MD {offPath.LastMd:0.##}; offset logs to MD {oMax:0.##} use straight-line extrapolation of the last station.");

        // ---- structure
        var steering = geo.Steering;
        var bedNames = steering?.BedNames.Select(n => n ?? "").ToList() ?? new List<string>();
        var grids = new List<GridBed>();
        foreach (var b in geo.Beds)
        {
            if (b.Grid?.EastAxis == null || b.Grid.NorthAxis == null || b.Grid.Tvd == null) { notes.Add($"Bed '{b.Name}' has no grid; skipped."); continue; }
            var surf = new BicubicSurface(b.Grid.EastAxis, b.Grid.NorthAxis, b.Grid.Tvd);
            int gi = bedNames.FindIndex(n => string.Equals(n, b.Name, StringComparison.OrdinalIgnoreCase));
            if (gi < 0 && steering?.Points.Count > 0) notes.Add($"Bed '{b.Name}' has no geomod column; no dip shift applied.");
            grids.Add(new GridBed(b.Name ?? "?", b.DepthIndex, b.SourceWellMd, surf, ParseGeoType(b.GeoType), gi));
        }
        var markers = geo.Markers.Select(m => new MarkerBed(m.Name ?? "?", m.DepthIndex, m.SourceWellMd));
        GeoModPath gmp;
        if (steering != null && steering.Points.Count > 0)
            gmp = new GeoModPath(steering.Points.Select(p => new GeoModPick(p.Md, p.DeltaTvd ?? new double[bedNames.Count],
                                     p.Slope ?? new double[bedNames.Count], p.FaultThrow)), bedNames.Count, steering.DecayDistance);
        else gmp = GeoModPath.Empty(bedNames.Count);
        var faults = ParseFaults(geo.Faults, notes);
        var structure = new StructureModel(grids, markers, gmp, faults);

        // ---- lateral table range: survey span plus all lateral curve depths
        double lMin = lateralPath.FirstMd, lMax = lateralPath.LastMd;
        foreach (var c in actual.Curves.Concat(modelWell?.Curves ?? new()))
            if (!double.IsNaN(c.DepthStop)) lMax = Math.Max(lMax, Math.Max(c.DepthStart, c.DepthStop));

        var vs = doc.Project?.VerticalSection;
        string refBed = referenceBed ?? grids.First().Name;
        var model = new SteeringModel(lateralPath, offWell, structure, vs?.AzimuthDeg ?? 0, vs?.OriginNorth ?? 0, vs?.OriginEast ?? 0,
                                      refBed, lMin, lMax + 1);
        var sp = new SteeringProject(doc, projectDir, gm, actual, offset, modelWell, model);
        sp.Notes.AddRange(notes);
        return sp;
    }

    static BedGeoType ParseGeoType(string? s) => s?.Replace(" ", "").ToLowerInvariant() switch
    {
        "truncateup" => BedGeoType.TruncateUp,
        "truncatedown" => BedGeoType.TruncateDown,
        _ => BedGeoType.Stretch,
    };

    static FaultTable ParseFaults(Faults? f, List<string> notes)
    {
        if (f?.Table == null || f.Table.Value.ValueKind != JsonValueKind.Array) return FaultTable.None;
        var rows = new List<(double, double)>();
        foreach (var r in f.Table.Value.EnumerateArray())
        {
            if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() >= 2)
                rows.Add((r[0].GetDouble(), r[1].GetDouble()));
            else if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("distance", out var d) && r.TryGetProperty("deltaTvd", out var t))
                rows.Add((d.GetDouble(), t.GetDouble()));
        }
        if (rows.Count > 0) notes.Add($"Fault table: {rows.Count} rows, azimuth {f.AzimuthDeg} deg.");
        return new FaultTable(f.AzimuthDeg, f.North, f.East, rows);
    }

    // ------------------------------------------------------------------ curves

    public IEnumerable<CurveRef> AvailableCurves()
    {
        foreach (var c in ActualWell.Curves) yield return new CurveRef(CurveOrigin.Realtime, c.Name, c.ValueUnit, c.Description);
        foreach (var c in OffsetWellDoc.Curves.Where(c => c.ColumnCount >= 2 && c.DepthType != "TIME"))
        {
            yield return new CurveRef(CurveOrigin.Offset, c.Name, c.ValueUnit, c.Description);
            yield return new CurveRef(CurveOrigin.Synthetic, c.Name, c.ValueUnit, "Synthetic from offset " + c.Name);
        }
        if (ModelWellDoc != null)
            foreach (var c in ModelWellDoc.Curves) yield return new CurveRef(CurveOrigin.PzsModel, c.Name, c.ValueUnit, c.Description);
    }

    public CurveRef? FindCurve(string id) => AvailableCurves().FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Raw curve data (MD, value) for a realtime, offset or PZS-model curve; for synthetic the computed curve.</summary>
    public DepthSeries GetDepthSeries(CurveRef c)
    {
        string key = c.Id + (c.Origin == CurveOrigin.Synthetic ? "|" + Model.ReferenceBed + "|" + Model.Mode : "");
        lock (_cache)
            if (_cache.TryGetValue(key, out var s)) return s;
        DepthSeries result;
        if (c.Origin == CurveOrigin.Synthetic)
        {
            var off = GetDepthSeries(c with { Origin = CurveOrigin.Offset });
            result = Model.Synthetic(off);
        }
        else
        {
            var well = c.Origin switch { CurveOrigin.Realtime => ActualWell, CurveOrigin.Offset => OffsetWellDoc, _ => ModelWellDoc! };
            var curve = well.Curves.FirstOrDefault(x => x.Name == c.Name) ?? throw new KeyNotFoundException(c.Id);
            result = LoadColumns(curve).series;
        }
        lock (_cache) _cache[key] = result;
        return result;
    }

    /// <summary>Loads a curve's data file; returns the (MD, value) series and every column by key.</summary>
    public (DepthSeries series, Dictionary<string, double[]> columns) LoadColumns(Curve curve)
    {
        if (curve.DataFile == null) throw new InvalidDataException($"Curve {curve.Name} has no data file.");
        var data = ProjectJson.LoadCurveData(ProjectDir, curve.DataFile);
        if (!data.Data.TryGetValue("md", out var md)) md = data.Data.Values.First();
        var valueKey = data.Data.Keys.FirstOrDefault(k => k == "value") ?? data.Data.Keys.Skip(1).First();
        return (DepthSeries.FromColumns(md, data.Data[valueKey]), data.Data);
    }

    /// <summary>Clears cached synthetic curves (call after changing reference bed or mode).</summary>
    public void InvalidateDerived()
    {
        lock (_cache)
            foreach (var k in _cache.Keys.Where(k => k.Contains('|')).ToList()) _cache.Remove(k);
    }
}
