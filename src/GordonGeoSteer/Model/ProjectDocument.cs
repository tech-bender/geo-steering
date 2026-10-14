using System.Text.Json;
using System.Text.Json.Serialization;

namespace GordonGeoSteer.Model;

// Plain C# mirror of the "gordon.geosteering.project" v1.0 JSON written by tools/PzsImporter.
// Property names map to the JSON's camelCase keys (see ProjectJson.Options). Numbers that the importer
// writes as null (it writes NaN/Infinity as null) deserialize to double.NaN.

public sealed class ProjectDocument
{
    public SchemaInfo? Schema { get; set; }
    public MigrationInfo? Migration { get; set; }
    public ProjectInfo? Project { get; set; }
    public List<Well> Wells { get; set; } = new();
    public List<GeoModel> Models { get; set; } = new();
    public GeologyInputs? GeologyInputs { get; set; }
    public List<FileEntry> FileInventory { get; set; } = new();
}

public sealed class SchemaInfo
{
    public string? Name { get; set; }
    public string? Version { get; set; }
}

public sealed class MigrationInfo
{
    public string? Tool { get; set; }
    public string? ToolVersion { get; set; }
    public string? GeneratedUtc { get; set; }
    public string? SourceFormat { get; set; }
    public string? SourceRoot { get; set; }
    public Dictionary<string, string>? Conventions { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<CrossCheck> CrossChecks { get; set; } = new();
}

/// <summary>Cross-checks are heterogeneous; common keys are typed, everything else is kept in Extra.</summary>
public sealed class CrossCheck
{
    public string? Check { get; set; }
    public string? File { get; set; }
    public string? Curve { get; set; }
    public string? Model { get; set; }
    public string? Bed { get; set; }
    public bool Pass { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public string Subject => string.Join(" / ", new[] { File, Model, Bed, Curve }.Where(s => !string.IsNullOrEmpty(s)));

    public string Details => Extra == null ? "" :
        string.Join("; ", Extra.Select(kv => kv.Key + "=" + Compact(kv.Value)));

    static string Compact(JsonElement e)
    {
        string s = e.GetRawText();
        s = string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
        return s.Length > 300 ? s[..300] + "..." : s;
    }
}

public sealed class ProjectInfo
{
    public string? Name { get; set; }
    public string? LengthUnit { get; set; }
    public string? SonicUnit { get; set; }
    public string? LocationDescription { get; set; }
    public SurfaceLocation? SurfaceLocation { get; set; }
    public VerticalSection? VerticalSection { get; set; }
    public double TvdSubseaOffset { get; set; }
    public int? RealtimeRefreshRate { get; set; }
}

public sealed class SurfaceLocation
{
    public double Northing { get; set; }
    public double Easting { get; set; }
    public string? Note { get; set; }
}

public sealed class VerticalSection
{
    public double AzimuthDeg { get; set; }
    public double OriginNorth { get; set; }
    public double OriginEast { get; set; }
}

public sealed class Point3
{
    public double North { get; set; }
    public double East { get; set; }
    public double Tvd { get; set; }
    public override string ToString() => $"N {North:0.##}, E {East:0.##}, TVD {Tvd:0.##}";
}

public sealed class Extent
{
    public double West { get; set; }
    public double East { get; set; }
    public double South { get; set; }
    public double North { get; set; }
    public override string ToString() => $"W {West:0.##} .. E {East:0.##}, S {South:0.##} .. N {North:0.##}";
}

// ------------------------------------------------------------------ wells
public sealed class Well
{
    public string Id { get; set; } = "";
    public string? SourceFile { get; set; }
    public string? Name { get; set; }
    public List<WellRole> Roles { get; set; } = new();
    public Dictionary<string, string?>? DatabaseInfo { get; set; }
    public WellReference? Reference { get; set; }
    public Dictionary<string, string?>? Header { get; set; }
    public List<HeaderAttribute> HeaderAttributes { get; set; } = new();
    public SurveySet? Surveys { get; set; }
    public List<Curve> Curves { get; set; } = new();
}

public sealed class WellRole
{
    public string? Model { get; set; }
    public string? Role { get; set; }
}

public sealed class WellReference
{
    public string? DepthUnit { get; set; }
    public Point3? TieIn { get; set; }
}

public sealed class HeaderAttribute
{
    public int Section { get; set; }
    public string? SectionName { get; set; }
    public string? Mnemonic { get; set; }
    public string? Unit { get; set; }
    public string? Value { get; set; }
    public string? Description { get; set; }
}

public sealed class SurveySet
{
    public string? AngleUnit { get; set; }
    public string? DepthUnit { get; set; }
    public int StationCount { get; set; }
    public List<SurveyStation> Stations { get; set; } = new();
}

public sealed class SurveyStation
{
    public double Md { get; set; }
    public double Inclination { get; set; }
    public double Azimuth { get; set; }
    public int? Type { get; set; }
    // Not written by importer v1.0 (PZS stores only MD/Inc/Azi/Type); kept so a future schema can fill them.
    public double? North { get; set; }
    public double? East { get; set; }
    public double? Tvd { get; set; }
}

public sealed class Curve
{
    public string Name { get; set; } = "";
    public string? Kind { get; set; }
    public string? DatasetType { get; set; }
    public string? DepthType { get; set; }
    public string? DepthUnit { get; set; }
    public string? ValueUnit { get; set; }
    public string? Description { get; set; }
    public int RecordCount { get; set; }
    public int ColumnCount { get; set; }
    public double DeclaredStart { get; set; }
    public double DeclaredStop { get; set; }
    public double DeclaredStep { get; set; }
    public int? InterpolateFlag { get; set; }
    public List<CurveColumn> Columns { get; set; } = new();
    public double DepthStart { get; set; } = double.NaN;
    public double DepthStop { get; set; } = double.NaN;
    public double ValueMin { get; set; } = double.NaN;
    public double ValueMax { get; set; } = double.NaN;
    public int NullCount { get; set; }
    public string? DataFile { get; set; }
}

public sealed class CurveColumn
{
    public string Key { get; set; } = "";
    public string? Unit { get; set; }
    public string? Note { get; set; }
}

/// <summary>data/&lt;well&gt;/&lt;curve&gt;.json</summary>
public sealed class CurveDataFile
{
    public string? Well { get; set; }
    public string? Curve { get; set; }
    public int RecordCount { get; set; }
    public List<CurveColumn> Columns { get; set; } = new();
    public Dictionary<string, double[]> Data { get; set; } = new();
}

// ------------------------------------------------------------------ models (.pzscfg)
public sealed class GeoModel
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? SourceFile { get; set; }
    public string? PzsDocVersion { get; set; }
    public string? OriginalPath { get; set; }
    public bool? IsBackupFile { get; set; }
    public Dictionary<string, FileLink?>? WellLinks { get; set; }
    public ModelWellSettings? WellSettings { get; set; }
    public List<Target> Targets { get; set; } = new();
    public List<MapObject> MapObjects { get; set; } = new();
    public GeologyModel? GeologyModel { get; set; }
    public FileLink? GmpFile { get; set; }
    public ResistivityModel? ResistivityModel { get; set; }
    public JsonElement? UnmappedPzsFields { get; set; }
}

public sealed class FileLink
{
    public string? OriginalPath { get; set; }
    public string? File { get; set; }
    public override string ToString() => File ?? ("(not found in project) " + OriginalPath);
}

public sealed class ModelWellSettings
{
    public WellSettings? Model { get; set; }
    public WellSettings? Source { get; set; }
    public WellSettings? Actual { get; set; }
    public List<WellSettings> Additional { get; set; } = new();
}

public sealed class WellSettings
{
    public string? PzsName { get; set; }
    public FileLink? File { get; set; }
    public FileLink? SurveyFile { get; set; }
    public Point3? Location { get; set; }
    public Point3? TieIn { get; set; }
    public double ZPosTieIn { get; set; }
    public Extrapolation? Extrapolation { get; set; }
    public string? TstCurveName { get; set; }
    public Dictionary<string, string?>? WellInfo { get; set; }
}

public sealed class Extrapolation
{
    public double Trend { get; set; }
    public double Straight { get; set; }
}

public sealed class Target
{
    public string? Name { get; set; }
    public double North { get; set; }
    public double East { get; set; }
    public double Tvd { get; set; }
    public double InclinationDeg { get; set; }
    public double AzimuthDeg { get; set; }
    public string? PathType { get; set; }
    public double TargetDoglegPerUnitLength { get; set; }
    public double MaxDoglegPerUnitLength { get; set; }
    public bool? Active { get; set; }
    public double NearestMd { get; set; }
    public double DistanceFromWellPath { get; set; }
    public bool? IsFuture { get; set; }
    public string? Description { get; set; }
    public string? LengthUnit { get; set; }
}

public sealed class MapObject
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? LengthUnit { get; set; }
    public Extent? Extent { get; set; }
    public string? OriginDescription { get; set; }
    public Point3? LocationInProject { get; set; }
    public FileLink? SourceFile { get; set; }
    public List<MapPoint> Points { get; set; } = new();
}

public sealed class MapPoint
{
    public double North { get; set; }
    public double East { get; set; }
    public double Tvd { get; set; }
    public string? Label { get; set; }
}

public sealed class GeologyModel
{
    public Point3? Origin { get; set; }
    public FileLink? GridFile { get; set; }
    public int? PzsGeologyDataId { get; set; }
    public List<Bed> Beds { get; set; } = new();
    public List<string?> EditableBedNames { get; set; } = new();
    public List<Bed> Markers { get; set; } = new();
    public Faults? Faults { get; set; }
    public Steering? Steering { get; set; }
}

public sealed class Bed
{
    public string? Name { get; set; }
    public string? BedType { get; set; }
    public string? GeoType { get; set; }
    public double SourceWellMd { get; set; }
    public double Rvd { get; set; }
    public double DepthIndex { get; set; }
    public BedGrid? Grid { get; set; }
}

public sealed class BedGrid
{
    public int NEast { get; set; }
    public int NNorth { get; set; }
    public double[]? EastAxis { get; set; }
    public double[]? NorthAxis { get; set; }
    /// <summary>[iNorth][iEast], iNorth = 0 is the south edge.</summary>
    public double[][]? Tvd { get; set; }
    public string? Note { get; set; }
}

public sealed class Faults
{
    public int? Count { get; set; }
    public double AzimuthDeg { get; set; }
    public double North { get; set; }
    public double East { get; set; }
    public JsonElement? Table { get; set; }
}

public sealed class Steering
{
    public string? Description { get; set; }
    public List<string?> BedNames { get; set; } = new();
    public double DecayDistance { get; set; }
    public int? ActivePointIndex { get; set; }
    public int UndoHistoryEntriesDropped { get; set; }
    public List<SteeringPoint> Points { get; set; } = new();
}

public sealed class SteeringPoint
{
    public double Md { get; set; }
    public double[]? DeltaTvd { get; set; }
    public double[]? Slope { get; set; }
    public double FaultThrow { get; set; }
}

public sealed class ResistivityModel
{
    public int? ToolIndex { get; set; }
    public string? RtCurveName { get; set; }
    public bool[]? EvaluateCurves { get; set; }
    public int? CalcTolerance { get; set; }
    public double Step { get; set; }
    public double InclinationTolerance { get; set; }
    public double DiTolerance { get; set; }
    public bool? UseFloatingRvd { get; set; }
    public double RvdLocation { get; set; }
    public double DecayDistance { get; set; }
}

// ------------------------------------------------------------------ raw geology inputs
public sealed class GeologyInputs
{
    public List<GeoGridFile> Grids { get; set; } = new();
    public List<MapFile> MapFiles { get; set; } = new();
    public List<BedSurfaceFile> BedSurfaces { get; set; } = new();
    public List<GeoModFile> GeoModFiles { get; set; } = new();
}

public sealed class GeoGridFile
{
    public string? SourceFile { get; set; }
    public string? FormatVersion { get; set; }
    public string? LengthUnit { get; set; }
    public string? OriginDescription { get; set; }
    public Extent? Extent { get; set; }
    public double[]? EastAxis { get; set; }
    public double[]? NorthAxis { get; set; }
    public int NEast { get; set; }
    public int NNorth { get; set; }
    public string? ValueMeaning { get; set; }
    public List<GeoGridBed> Beds { get; set; } = new();
}

public sealed class GeoGridBed
{
    public string? Name { get; set; }
    public double[][]? Values { get; set; }
}

public sealed class MapFile
{
    public string? SourceFile { get; set; }
    public string? Layout { get; set; }
    public string? OriginDescription { get; set; }
    public List<MapFileObject> Objects { get; set; } = new();
}

public sealed class MapFileObject
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public List<MapPoint> Points { get; set; } = new();
}

public sealed class BedSurfaceFile
{
    public string? SourceFile { get; set; }
    public int DeclaredCount { get; set; }
    public string? Note { get; set; }
    public List<BedSurfaceBed> Beds { get; set; } = new();
}

public sealed class BedSurfaceBed
{
    public string? Name { get; set; }
    public List<JsonElement> Points { get; set; } = new();
}

public sealed class GeoModFile
{
    public string? SourceFile { get; set; }
    public string? WrittenTime { get; set; }
    public List<JsonElement> Stations { get; set; } = new();
}

// ------------------------------------------------------------------ inventory
public sealed class FileEntry
{
    public string? Path { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string? Category { get; set; }
    public string? Status { get; set; }
    public string? Note { get; set; }
    public string? DuplicateOf { get; set; }
}
