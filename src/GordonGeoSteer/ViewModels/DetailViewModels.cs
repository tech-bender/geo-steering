using System.Globalization;
using System.IO;
using GordonGeoSteer.Model;
using GordonGeoSteer.Services;

namespace GordonGeoSteer.ViewModels;

static class Fmt
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static string D(double v, string f = "0.###") => double.IsNaN(v) ? "-" : v.ToString(f, Inv);
    public static string D(double? v, string f = "0.###") => v.HasValue ? D(v.Value, f) : "-";
    public static string S(string? s) => string.IsNullOrEmpty(s) ? "-" : s;
}

// ------------------------------------------------------------------ project
public sealed class ProjectDetailViewModel
{
    public string Title { get; }
    public List<KeyValueRow> Rows { get; } = new();

    public ProjectDetailViewModel(ProjectDocument doc, string projectJsonPath)
    {
        var p = doc.Project;
        Title = "Project: " + (p?.Name ?? "(unnamed)");
        void R(string k, string? v) => Rows.Add(new KeyValueRow(k, Fmt.S(v)));
        R("Name", p?.Name);
        R("Location description", p?.LocationDescription);
        R("Length unit", p?.LengthUnit);
        R("Sonic unit", p?.SonicUnit);
        R("Surface location (northing / easting)", p?.SurfaceLocation == null ? null
            : $"{Fmt.D(p.SurfaceLocation.Northing, "0.##")} / {Fmt.D(p.SurfaceLocation.Easting, "0.##")}");
        R("Vertical section azimuth (deg)", p?.VerticalSection == null ? null : Fmt.D(p.VerticalSection.AzimuthDeg));
        R("Vertical section origin (N / E)", p?.VerticalSection == null ? null
            : $"{Fmt.D(p.VerticalSection.OriginNorth)} / {Fmt.D(p.VerticalSection.OriginEast)}");
        R("TVD subsea offset", p == null ? null : Fmt.D(p.TvdSubseaOffset));
        R("Wells", doc.Wells.Count.ToString());
        R("Models", doc.Models.Count.ToString());
        R("Files inventoried", doc.FileInventory.Count.ToString());
        R("Schema", $"{doc.Schema?.Name} v{doc.Schema?.Version}");
        R("Imported by", $"{doc.Migration?.Tool} {doc.Migration?.ToolVersion} at {doc.Migration?.GeneratedUtc} (UTC)");
        R("Source PZS folder", doc.Migration?.SourceRoot);
        R("Loaded project.json", projectJsonPath);
    }
}

public sealed class ImportReportViewModel
{
    public string Title => "Import report";
    public string Summary { get; }
    public bool HasProblems { get; }
    public List<string> Warnings { get; }
    public List<CrossCheck> CrossChecks { get; }
    public List<CrossCheck> FailedCrossChecks { get; }
    public string ImporterLog { get; }

    public ImportReportViewModel(ProjectDocument doc, string? importerLog)
    {
        Warnings = doc.Migration?.Warnings ?? new();
        CrossChecks = doc.Migration?.CrossChecks ?? new();
        FailedCrossChecks = CrossChecks.Where(c => !c.Pass).ToList();
        HasProblems = FailedCrossChecks.Count > 0;
        Summary = $"{CrossChecks.Count} cross-checks, {FailedCrossChecks.Count} failed; {Warnings.Count} warning(s).";
        ImporterLog = string.IsNullOrEmpty(importerLog) ? "(project.json was opened directly; importer was not run in this session)" : importerLog;
    }
}

// ------------------------------------------------------------------ wells
public sealed class WellDetailViewModel
{
    public Well Well { get; }
    public string Title { get; }
    public List<KeyValueRow> Rows { get; } = new();
    public List<HeaderAttribute> HeaderAttributes => Well.HeaderAttributes;

    public WellDetailViewModel(Well w)
    {
        Well = w;
        Title = "Well: " + (w.Name ?? w.Id);
        void R(string k, string? v) => Rows.Add(new KeyValueRow(k, Fmt.S(v)));
        R("Name (header WELL)", w.Name);
        R("Id", w.Id);
        R("Source file", w.SourceFile);
        R("Roles", WellRoles.Describe(w));
        R("Depth unit", w.Reference?.DepthUnit);
        R("Tie-in", w.Reference?.TieIn?.ToString());
        R("Survey stations", (w.Surveys?.StationCount ?? 0).ToString());
        R("Curves", w.Curves.Count.ToString());
        if (w.DatabaseInfo != null)
            foreach (var kv in w.DatabaseInfo) R("DB " + kv.Key, kv.Value);
    }
}

public static class WellRoles
{
    public static string Describe(Well w) => w.Roles.Count == 0
        ? "(not referenced by any model)"
        : string.Join(", ", w.Roles.Select(r => $"{r.Role} of {r.Model}"));

    /// <summary>Primary well role: model / source / actual (ignores the *Survey link roles).</summary>
    public static string Short(Well w)
    {
        var main = w.Roles.Select(r => r.Role).Where(r => r is "model" or "source" or "actual").Distinct().ToList();
        if (main.Count > 0) return string.Join("+", main);
        var any = w.Roles.Select(r => r.Role).Distinct().ToList();
        return any.Count > 0 ? string.Join("+", any) : "unreferenced";
    }
}

public sealed class SurveyRow
{
    public double Md { get; init; }
    public double Inclination { get; init; }
    public double Azimuth { get; init; }
    public int? Type { get; init; }
    public string TypeName { get; init; } = "";
    public double? North { get; init; }
    public double? East { get; init; }
    public double? Tvd { get; init; }
}

public sealed class SurveyViewModel
{
    public string Title { get; }
    public string Summary { get; }
    public List<SurveyRow> Stations { get; }
    public bool HasPositions { get; }

    public static string TypeName(int? t) => t switch
    {
        1 => "Standard", 2 => "NearBit", 3 => "ProjToBit", 4 => "ProjToBitDynamic",
        5 => "Planned", 6 => "Extrapolated", 7 => "Projected", null => "-", _ => "Type " + t,
    };

    public SurveyViewModel(Well w)
    {
        Title = $"Survey: {w.Name} ({w.SourceFile})";
        var st = w.Surveys?.Stations ?? new();
        Stations = st.Select(s => new SurveyRow
        {
            Md = s.Md, Inclination = s.Inclination, Azimuth = s.Azimuth, Type = s.Type, TypeName = TypeName(s.Type),
            North = s.North, East = s.East, Tvd = s.Tvd,
        }).ToList();
        HasPositions = st.Any(s => s.North.HasValue || s.East.HasValue || s.Tvd.HasValue);
        Summary = st.Count == 0 ? "No survey stations." :
            $"{st.Count} stations, MD {Fmt.D(st.Min(s => s.Md))} - {Fmt.D(st.Max(s => s.Md))} {w.Surveys?.DepthUnit}, angles in {w.Surveys?.AngleUnit}. " +
            (HasPositions ? "" : "N/E/TVD are not stored in PZS well databases (computed by survey calculation, which is not part of v1).");
    }
}

public sealed class CurveListViewModel
{
    public string Title { get; }
    public List<Curve> Curves { get; }
    public CurveListViewModel(Well w) { Title = $"Curves: {w.Name} ({w.SourceFile})"; Curves = w.Curves; }
}

public sealed class CurveViewModel
{
    public Curve Curve { get; }
    public Well Well { get; }
    public string Title { get; }
    public List<KeyValueRow> Rows { get; } = new();
    public double[] Md { get; } = Array.Empty<double>();
    public double[] Values { get; } = Array.Empty<double>();
    public string? LoadError { get; }
    public bool LogScaleDefault { get; }
    public string ValueAxisLabel { get; }
    public string DepthAxisLabel { get; }
    public int PlottedPoints { get; }

    public static bool IsNull(double v) => double.IsNaN(v) || v == -999.25;

    public CurveViewModel(Curve c, Well w, string projectDir)
    {
        Curve = c; Well = w;
        Title = $"Curve {c.Name} - {w.Name} ({w.SourceFile})";
        ValueAxisLabel = $"{c.Name} [{c.ValueUnit}]";
        DepthAxisLabel = $"{c.DepthType ?? "MD"} [{c.DepthUnit}]";
        void R(string k, string? v) => Rows.Add(new KeyValueRow(k, Fmt.S(v)));
        R("Name", c.Name);
        R("Description", c.Description);
        R("Kind / dataset type", $"{c.Kind} / {c.DatasetType}");
        R("Depth type / unit", $"{c.DepthType} / {c.DepthUnit}");
        R("Value unit", c.ValueUnit);
        R("Record count", c.RecordCount.ToString());
        R("Columns", string.Join(", ", c.Columns.Select(k => k.Unit == null ? k.Key : $"{k.Key} [{k.Unit}]")));
        R("Depth range (decoded)", $"{Fmt.D(c.DepthStart, "0.####")} - {Fmt.D(c.DepthStop, "0.####")}");
        R("Depth range (declared Start/Stop)", $"{Fmt.D(c.DeclaredStart, "0.####")} - {Fmt.D(c.DeclaredStop, "0.####")}, step {Fmt.D(c.DeclaredStep, "0.####")}");
        R("Value range", $"{Fmt.D(c.ValueMin, "0.####")} - {Fmt.D(c.ValueMax, "0.####")}");
        R("Null samples", c.NullCount.ToString());
        R("Data file", c.DataFile);

        if (string.IsNullOrEmpty(c.DataFile)) { LoadError = "Curve has no data file."; return; }
        try
        {
            var data = ProjectJson.LoadCurveData(projectDir, c.DataFile);
            if (!data.Data.TryGetValue("md", out var md)) throw new InvalidDataException("data file has no 'md' column");
            if (!data.Data.TryGetValue("value", out var val)) throw new InvalidDataException("data file has no 'value' column");
            if (md.Length != val.Length) throw new InvalidDataException($"md/value length mismatch ({md.Length} vs {val.Length})");
            if (md.Length != c.RecordCount) throw new InvalidDataException($"data file has {md.Length} records, metadata says {c.RecordCount}");
            Md = md; Values = val;
            PlottedPoints = val.Count(v => !IsNull(v));
            R("Loaded samples (non-null)", $"{md.Length} ({PlottedPoints})");
            R("First sample (MD, value)", $"{Fmt.D(md[0], "0.####")}, {Fmt.D(val[0], "0.####")}");
            R("Last sample (MD, value)", $"{Fmt.D(md[^1], "0.####")}, {Fmt.D(val[^1], "0.####")}");
        }
        catch (Exception ex)
        {
            LoadError = "Could not load curve data: " + ex.Message;
        }
        LogScaleDefault = (c.ValueUnit ?? "").Equals("OHMM", StringComparison.OrdinalIgnoreCase) && c.ValueMin > 0;
    }
}

// ------------------------------------------------------------------ models
public sealed class ModelDetailViewModel
{
    public string Title { get; }
    public List<KeyValueRow> Rows { get; } = new();

    public ModelDetailViewModel(GeoModel m)
    {
        Title = "Model: " + (m.Name ?? m.Id);
        void R(string k, string? v) => Rows.Add(new KeyValueRow(k, Fmt.S(v)));
        R("Source file", m.SourceFile);
        R("PZS doc version", m.PzsDocVersion);
        R("Original path (PZS workstation)", m.OriginalPath);
        if (m.WellLinks != null)
            foreach (var kv in m.WellLinks) R("Well link: " + kv.Key, kv.Value?.ToString());
        R("Targets", m.Targets.Count.ToString());
        R("Map overlay objects", m.MapObjects.Count.ToString());
        R("Geology beds / markers", $"{m.GeologyModel?.Beds.Count ?? 0} / {m.GeologyModel?.Markers.Count ?? 0}");
        R("Steering (geomod) points", (m.GeologyModel?.Steering?.Points.Count ?? 0).ToString());
        R("GMP file referenced", m.GmpFile?.ToString());
        var rm = m.ResistivityModel;
        if (rm != null)
        {
            R("Resistivity tool index", rm.ToolIndex?.ToString());
            R("Rt curve", rm.RtCurveName);
            R("Resistivity step", Fmt.D(rm.Step));
            R("Use floating RVD", rm.UseFloatingRvd?.ToString());
            R("Decay distance", Fmt.D(rm.DecayDistance));
        }
        foreach (var (role, ws) in new[] { ("model", m.WellSettings?.Model), ("source", m.WellSettings?.Source), ("actual", m.WellSettings?.Actual) })
            if (ws != null)
                R($"{role} well location / tie-in", $"{ws.Location} | tie-in {ws.TieIn}");
    }
}

public sealed class TargetsViewModel
{
    public string Title { get; }
    public List<Target> Targets { get; }
    public TargetsViewModel(GeoModel m) { Title = $"Targets: {m.Name}"; Targets = m.Targets; }
}

public sealed class BedRow
{
    public string Category { get; init; } = "";
    public string? Name { get; init; }
    public string? BedType { get; init; }
    public string? GeoType { get; init; }
    public double SourceWellMd { get; init; }
    public double Rvd { get; init; }
    public double DepthIndex { get; init; }
    public bool HasGrid => Grid?.Tvd != null;
    public string GridSize => Grid == null ? "-" : $"{Grid.NEast} x {Grid.NNorth}";
    public BedGrid? Grid { get; init; }
}

public sealed class GridRow
{
    public string North { get; init; } = "";
    public string[] Cells { get; init; } = Array.Empty<string>();
}

public sealed class SteeringRow
{
    public double Md { get; init; }
    public string DeltaTvd { get; init; } = "";
    public string Slope { get; init; } = "";
    public double FaultThrow { get; init; }
}

public sealed class GeologyViewModel : ObservableObject
{
    public string Title { get; }
    public string Summary { get; }
    public List<BedRow> Beds { get; }
    public List<SteeringRow> Steering { get; }
    public string SteeringSummary { get; }

    BedRow? _selectedBed;
    public BedRow? SelectedBed
    {
        get => _selectedBed;
        set
        {
            if (!Set(ref _selectedBed, value)) return;
            OnPropertyChanged(nameof(SelectedGrid));
            OnPropertyChanged(nameof(GridRows));
            OnPropertyChanged(nameof(GridColumnHeaders));
            OnPropertyChanged(nameof(GridSummary));
        }
    }

    public BedGrid? SelectedGrid => _selectedBed?.Grid;

    /// <summary>Rows north-to-south (map orientation), cells west-to-east.</summary>
    public List<GridRow> GridRows
    {
        get
        {
            var g = SelectedGrid;
            if (g?.Tvd == null || g.NorthAxis == null) return new();
            var rows = new List<GridRow>();
            for (int r = g.Tvd.Length - 1; r >= 0; r--)
                rows.Add(new GridRow { North = Fmt.D(g.NorthAxis[r], "0.#"), Cells = g.Tvd[r].Select(v => Fmt.D(v, "0.00")).ToArray() });
            return rows;
        }
    }

    public List<string> GridColumnHeaders => SelectedGrid?.EastAxis?.Select(e => "E " + Fmt.D(e, "0.#")).ToList() ?? new();

    public string GridSummary
    {
        get
        {
            var g = SelectedGrid;
            if (g?.Tvd == null) return _selectedBed == null ? "Select a bed with a grid." : $"'{_selectedBed.Name}' has no grid (marker beds are point picks).";
            var all = g.Tvd.SelectMany(r => r).Where(v => !double.IsNaN(v)).ToList();
            return $"'{_selectedBed!.Name}': {g.NEast} x {g.NNorth} grid (E x N), TVD {Fmt.D(all.Min(), "0.00")} - {Fmt.D(all.Max(), "0.00")}. " +
                   $"East {Fmt.D(g.EastAxis![0])}..{Fmt.D(g.EastAxis[^1])}, North {Fmt.D(g.NorthAxis![0])}..{Fmt.D(g.NorthAxis[^1])}.";
        }
    }

    public GeologyViewModel(GeoModel m)
    {
        Title = $"Geology model: {m.Name}";
        var gm = m.GeologyModel;
        Beds = new();
        if (gm != null)
        {
            Beds.AddRange(gm.Beds.Select(b => Row("Gridded bed", b)));
            Beds.AddRange(gm.Markers.Select(b => Row("Marker", b)));
        }
        Summary = gm == null ? "No geology model." :
            $"Origin {gm.Origin}; grid file {gm.GridFile}; {gm.Beds.Count} gridded bed(s), {gm.Markers.Count} marker(s); faults: {gm.Faults?.Count ?? 0}.";
        var st = gm?.Steering;
        Steering = st?.Points.Select(p => new SteeringRow
        {
            Md = p.Md,
            DeltaTvd = p.DeltaTvd == null ? "-" : string.Join("  |  ", p.DeltaTvd.Select(v => Fmt.D(v, "0.0000"))),
            Slope = p.Slope == null ? "-" : string.Join("  |  ", p.Slope.Select(v => Fmt.D(v, "0.000000"))),
            FaultThrow = p.FaultThrow,
        }).ToList() ?? new();
        SteeringSummary = st == null ? "No steering points." :
            $"{st.Points.Count} steering (geomod) points; per-bed columns in order: {string.Join(" | ", st.BedNames)}; decay distance {Fmt.D(st.DecayDistance)}; active point index {st.ActivePointIndex}.";
        _selectedBed = Beds.FirstOrDefault(b => b.HasGrid);
    }

    static BedRow Row(string cat, Bed b) => new()
    {
        Category = cat, Name = b.Name, BedType = b.BedType, GeoType = b.GeoType,
        SourceWellMd = b.SourceWellMd, Rvd = b.Rvd, DepthIndex = b.DepthIndex, Grid = b.Grid,
    };
}

// ------------------------------------------------------------------ inventory
public sealed class InventoryViewModel
{
    public string Title => "File inventory";
    public string Summary { get; }
    public List<FileEntry> Files { get; }

    public InventoryViewModel(ProjectDocument doc)
    {
        Files = doc.FileInventory;
        var byStatus = Files.GroupBy(f => f.Status ?? "?").OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}");
        int dups = Files.Count(f => !string.IsNullOrEmpty(f.DuplicateOf));
        Summary = $"{Files.Count} files - {string.Join(", ", byStatus)}; {dups} byte-identical duplicate(s) (SHA-256).";
    }
}
