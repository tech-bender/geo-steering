using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GordonGeoSteer.Engine.Workspace;

public enum TrackOrientation { Vertical, Horizontal }
public enum LineStyleKind { Solid, Dashed, Dotted, DotDash }
public enum ShadingMode { None, ToCurve, ToTrackMin, ToTrackMax, Gradient }
public enum ColorRampKind { Earth, Rainbow, Grayscale, Heat, Viridis }
public enum BedKind { Imported, User }

/// <summary>
/// Gordon workspace file ("gordon.geosteering.workspace" v1): everything the steering view lets the
/// user change - tracks, curve styles and shading, bed boundaries, reference bed, correlation mode and
/// zoom ranges. It is a separate file keyed to the imported project; PZS files are never written.
/// </summary>
public sealed class WorkspaceDocument
{
    public const string SchemaName = "gordon.geosteering.workspace";
    public const string SchemaVersion = "1.0";

    public string Schema { get; set; } = SchemaName;
    public string Version { get; set; } = SchemaVersion;
    public string? ProjectName { get; set; }
    public string? ProjectSourceRoot { get; set; }
    public string? ModelName { get; set; }
    public string? SavedUtc { get; set; }

    public string? ReferenceBed { get; set; }
    public string CorrelationMode { get; set; } = "ParallelToReference";

    public List<TrackSpec> Tracks { get; set; } = new();
    public List<BedBoundarySpec> Beds { get; set; } = new();
    public ViewSpec? View { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public void Save(string path)
    {
        SavedUtc = DateTime.UtcNow.ToString("o");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static WorkspaceDocument Load(string path)
    {
        var w = JsonSerializer.Deserialize<WorkspaceDocument>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Workspace file deserialized to null: " + path);
        if (w.Schema != SchemaName) throw new InvalidDataException($"Not a {SchemaName} file: {path}");
        if (!w.Version.StartsWith("1.")) throw new InvalidDataException($"Unsupported workspace version {w.Version}");
        return w;
    }

    /// <summary>Default workspace location for a project: %LOCALAPPDATA%\GordonGeoSteer\workspaces\&lt;name&gt;-&lt;hash&gt;.gws.json.</summary>
    public static string DefaultPath(string? projectName, string? sourceRoot, string? modelName)
    {
        string key = (sourceRoot ?? "") + "|" + (projectName ?? "") + "|" + (modelName ?? "");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant())))[..10].ToLowerInvariant();
        string safe = new string((projectName ?? "project").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (safe.Length > 60) safe = safe[..60];
        string root = Environment.GetEnvironmentVariable("GORDONGEOSTEER_WORKSPACE_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GordonGeoSteer", "workspaces");
        return Path.Combine(root, $"{safe}-{hash}.gws.json");
    }
}

public sealed class TrackSpec
{
    public string Title { get; set; } = "Track";
    public TrackOrientation Orientation { get; set; }
    /// <summary>Vertical track width / horizontal track height, in device-independent pixels.</summary>
    public double Size { get; set; } = 200;
    public double ScaleMin { get; set; } = 0;
    public double ScaleMax { get; set; } = 150;
    public bool LogScale { get; set; }
    public List<CurveSpec> Curves { get; set; } = new();
}

public sealed class CurveSpec
{
    /// <summary>Curve id: "rt:NAME", "off:NAME", "syn:NAME" or "pzs:NAME".</summary>
    public string Source { get; set; } = "";
    public string Color { get; set; } = "#1F4E9E";
    public double Thickness { get; set; } = 1.2;
    public LineStyleKind LineStyle { get; set; } = LineStyleKind.Solid;
    public bool Visible { get; set; } = true;
    public ShadingSpec Shading { get; set; } = new();
}

public sealed class ShadingSpec
{
    public ShadingMode Mode { get; set; } = ShadingMode.None;
    /// <summary>For ToCurve: the other curve's Source id (must be in the same track).</summary>
    public string? OtherCurve { get; set; }
    public string Color { get; set; } = "#7FB3E6";
    public double Opacity { get; set; } = 0.45;
    public ColorRampKind Ramp { get; set; } = ColorRampKind.Earth;
}

public sealed class BedBoundarySpec
{
    public string Name { get; set; } = "Boundary";
    public BedKind Kind { get; set; } = BedKind.User;
    /// <summary>User beds: stratigraphic offset below the reference bed (TVT, positive down), parallel to the structure. Ignored for imported beds.</summary>
    public double Offset { get; set; }
    public string Color { get; set; } = "#8B4513";
    public double Thickness { get; set; } = 1.5;
    public bool Visible { get; set; } = true;
    public bool ShowLabel { get; set; } = true;
}

public sealed class ViewSpec
{
    public double? XMin { get; set; }
    public double? XMax { get; set; }
    public double? TvdMin { get; set; }
    public double? TvdMax { get; set; }
    public double? TvtMin { get; set; }
    public double? TvtMax { get; set; }
    public bool? ShowProperties { get; set; }
}
