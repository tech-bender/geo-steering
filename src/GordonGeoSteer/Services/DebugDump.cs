using System.Globalization;
using System.IO;
using System.Text;
using GordonGeoSteer.ViewModels;

namespace GordonGeoSteer.Services;

/// <summary>
/// Writes what the VIEW-MODELS actually hold (not the raw JSON) to a text file, one fact per line:
///   &lt;tree path&gt; :: &lt;key&gt; = &lt;value&gt;
/// Used by Help > Dump loaded data and by the --selftest mode, so the values that reach the UI can be
/// checked programmatically (see tests\Verify-BakkenDump.ps1).
/// </summary>
public static class DebugDump
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string N(double v) => double.IsNaN(v) ? "NaN" : v.ToString("R", Inv);

    public static void Write(MainViewModel vm, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# GordonGeoSteer view-model dump");
        sb.AppendLine("# generated " + DateTime.Now.ToString("s", Inv));
        sb.AppendLine("# projectJson = " + vm.ProjectJsonPath);
        if (vm.LastImport != null)
            sb.AppendLine($"# importer = {vm.LastImport.ImporterPath} exit={vm.LastImport.ExitCode}");
        sb.AppendLine("# banner = " + (vm.Banner ?? ""));
        sb.AppendLine("# status = " + vm.Status);

        foreach (var root in vm.Nodes) Walk(root, "", sb);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    static void Walk(TreeNode node, string parent, StringBuilder sb)
    {
        string p = parent.Length == 0 ? node.Header : parent + " > " + node.Header;
        void L(string k, object? v) => sb.AppendLine($"{p} :: {k} = {Format(v)}");
        L("kind", node.Kind);
        switch (node.Detail)
        {
            case null: break;
            case ErrorViewModel e: L("ERROR", e.Message.Replace("\r", " ").Replace("\n", " ")); break;
            case ProjectDetailViewModel d: foreach (var r in d.Rows) L(r.Key, r.Value); break;
            case ImportReportViewModel d:
                L("summary", d.Summary);
                L("crossChecks", d.CrossChecks.Count);
                L("crossChecksFailed", d.FailedCrossChecks.Count);
                L("warnings", d.Warnings.Count);
                foreach (var w in d.Warnings) L("warning", w);
                foreach (var c in d.FailedCrossChecks) L("FAILED", c.Check + " | " + c.Subject + " | " + c.Details);
                break;
            case WellDetailViewModel d:
                foreach (var r in d.Rows) L(r.Key, r.Value);
                L("headerAttributes", d.HeaderAttributes.Count);
                break;
            case SurveyViewModel d:
                L("stations", d.Stations.Count);
                L("hasPositions", d.HasPositions);
                if (d.Stations.Count > 0)
                {
                    var f = d.Stations[0]; var l = d.Stations[^1];
                    L("first", $"{N(f.Md)},{N(f.Inclination)},{N(f.Azimuth)},{f.TypeName}");
                    L("last", $"{N(l.Md)},{N(l.Inclination)},{N(l.Azimuth)},{l.TypeName}");
                }
                break;
            case CurveListViewModel d: L("curves", string.Join(",", d.Curves.Select(c => c.Name))); break;
            case CurveViewModel d:
                L("name", d.Curve.Name);
                L("valueUnit", d.Curve.ValueUnit);
                L("recordCount", d.Curve.RecordCount);
                L("loadedSamples", d.Md.Length);
                L("plottedPoints", d.PlottedPoints);
                L("loadError", d.LoadError ?? "");
                L("logScaleDefault", d.LogScaleDefault);
                if (d.Md.Length > 0)
                {
                    L("mdFirst", N(d.Md[0])); L("mdLast", N(d.Md[^1]));
                    L("valueFirst", N(d.Values[0])); L("valueLast", N(d.Values[^1]));
                    var nn = d.Values.Where(v => !CurveViewModel.IsNull(v)).ToList();
                    if (nn.Count > 0) { L("valueMinLoaded", N(nn.Min())); L("valueMaxLoaded", N(nn.Max())); }
                }
                L("metaDepthStart", N(d.Curve.DepthStart)); L("metaDepthStop", N(d.Curve.DepthStop));
                break;
            case ModelDetailViewModel d: foreach (var r in d.Rows) L(r.Key, r.Value); break;
            case TargetsViewModel d:
                L("count", d.Targets.Count);
                foreach (var t in d.Targets)
                    L("target", $"{t.Name}|N={N(t.North)}|E={N(t.East)}|TVD={N(t.Tvd)}|Inc={N(t.InclinationDeg)}|Azi={N(t.AzimuthDeg)}|Path={t.PathType}");
                break;
            case GeologyViewModel d:
                L("summary", d.Summary);
                L("beds", d.Beds.Count);
                foreach (var b in d.Beds)
                    L("bed", $"{b.Category}|{b.Name}|{b.BedType}|{b.GeoType}|srcMD={N(b.SourceWellMd)}|RVD={N(b.Rvd)}|grid={b.GridSize}");
                L("selectedBed", d.SelectedBed?.Name);
                L("gridSummary", d.GridSummary);
                L("gridRows", d.GridRows.Count);
                if (d.GridRows.Count > 0) L("gridTopRow(north edge)", d.GridRows[0].North + ": " + string.Join(" ", d.GridRows[0].Cells));
                L("steeringPoints", d.Steering.Count);
                L("steeringSummary", d.SteeringSummary);
                if (d.Steering.Count > 0) L("steeringFirst", N(d.Steering[0].Md) + " | " + d.Steering[0].DeltaTvd);
                break;
            case InventoryViewModel d:
                L("summary", d.Summary);
                L("files", d.Files.Count);
                L("duplicates", d.Files.Count(f => !string.IsNullOrEmpty(f.DuplicateOf)));
                foreach (var f in d.Files) L("file", $"{f.Path}|{f.Category}|{f.Status}|dupOf={f.DuplicateOf}");
                break;
            default: L("detailType", node.Detail.GetType().Name); break;
        }
        foreach (var c in node.Children) Walk(c, p, sb);
    }

    static string Format(object? v) => v switch
    {
        null => "",
        double d => N(d),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, Inv),
        _ => v.ToString()!.Replace("\r", " ").Replace("\n", " "),
    };
}
