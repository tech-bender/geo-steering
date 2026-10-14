using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GordonGeoSteer.Model;

namespace GordonGeoSteer.Services;

/// <summary>Loads the importer's JSON output (project.json + data/*.json) into the C# model.</summary>
public static class ProjectJson
{
    public const string ExpectedSchemaName = "gordon.geosteering.project";
    public const string ExpectedSchemaMajor = "1";

    public static readonly JsonSerializerOptions Options = CreateOptions();

    static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new NullAsNaNDoubleConverter());
        return o;
    }

    public static ProjectDocument LoadProject(string projectJsonPath)
    {
        using var fs = File.OpenRead(projectJsonPath);
        var doc = JsonSerializer.Deserialize<ProjectDocument>(fs, Options)
                  ?? throw new InvalidDataException("project.json deserialized to null");
        if (doc.Schema?.Name != ExpectedSchemaName)
            throw new InvalidDataException($"Not a {ExpectedSchemaName} file (schema.name = '{doc.Schema?.Name}').");
        var ver = doc.Schema.Version ?? "";
        if (!ver.StartsWith(ExpectedSchemaMajor + ".", StringComparison.Ordinal) && ver != ExpectedSchemaMajor)
            throw new InvalidDataException($"Unsupported schema version '{ver}' (this build reads {ExpectedSchemaMajor}.x).");
        return doc;
    }

    public static CurveDataFile LoadCurveData(string projectDir, string dataFileRel)
    {
        string full = Path.GetFullPath(Path.Combine(projectDir, dataFileRel.Replace('/', Path.DirectorySeparatorChar)));
        // Data files must live under the project output folder.
        if (!full.StartsWith(Path.GetFullPath(projectDir), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Curve data path escapes the project folder: " + dataFileRel);
        using var fs = File.OpenRead(full);
        return JsonSerializer.Deserialize<CurveDataFile>(fs, Options)
               ?? throw new InvalidDataException("Curve data file deserialized to null: " + dataFileRel);
    }

    /// <summary>The importer writes NaN/Infinity as JSON null; read those back as NaN.</summary>
    sealed class NullAsNaNDoubleConverter : JsonConverter<double>
    {
        public override bool HandleNull => true;

        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return double.NaN;
            if (reader.TokenType == JsonTokenType.String)
                return double.TryParse(reader.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
            return reader.GetDouble();
        }

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) writer.WriteNullValue();
            else writer.WriteNumberValue(value);
        }
    }
}
