// PzsImporter.cs - GordonGeoSteer import engine: PZS project folder -> gordon.geosteering.project v1.0 JSON.
// (Originally developed as "PzsMigrate"; moved into the GordonGeoSteer repo as tools/PzsImporter.)
// Reads a Schlumberger Pathfinder PZS 5.2.0 project FOLDER (read-only) and writes a self-contained
// JSON export (project.json + per-curve data files) to a separate output directory.
//
// Original code written against the documented file formats (see project memory). For .pzscfg it
// deserializes with the real PZS assemblies (BinaryFormatter + strict allow-list binder, same approach
// as PzsCfgDump.cs) and reads public/private fields by name via reflection. No PZS code is copied.
// Licensing assemblies (SKCLNET/PFLicense/LicenInf/InstallPzsLicense) are never loaded.
//
// Build (x86 required: PZS assemblies are 32-bit, and the 32-bit Jet ODBC driver is used):
//   build.cmd   (C:\Windows\Microsoft.NET\Framework\v3.5\csc.exe /platform:x86 ... -> bin\PzsImporter.exe)
// Run:
//   PzsImporter.exe <pzsProjectRoot> <outputDir> [--pzs-dir=<PZS 5.2.0 install dir>]
// Exit codes: 0 = OK, 2 = usage / protected path / bad input, 3 = one or more cross-checks failed,
//             4 = unexpected fatal error.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Odbc;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// ---------------------------------------------------------------- ordered JSON object + writer
class JObj : List<KeyValuePair<string, object>>
{
    // Setting an existing key replaces its value in place (JSON objects must not carry duplicate keys).
    public JObj Set(string k, object v)
    {
        for (int i = 0; i < Count; i++)
            if (this[i].Key == k) { this[i] = new KeyValuePair<string, object>(k, v); return this; }
        Add(new KeyValuePair<string, object>(k, v)); return this;
    }
    public object Get(string k) { foreach (KeyValuePair<string, object> kv in this) if (kv.Key == k) return kv.Value; return null; }
}

static class Json
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void WriteFile(string path, object v)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(false)))
        { Write(w, v, 0); w.WriteLine(); }
    }

    static bool IsScalar(object v)
    {
        return v == null || v is string || v is bool || v is double || v is float || v is int || v is long
            || v is short || v is byte || v is decimal || v is Enum || v is uint || v is ushort || v is sbyte || v is ulong;
    }

    static void Indent(TextWriter w, int d) { w.Write('\n'); w.Write(new string(' ', d * 2)); }

    public static void Write(TextWriter w, object v, int d)
    {
        if (v == null) { w.Write("null"); return; }
        if (v is string) { Str(w, (string)v); return; }
        if (v is bool) { w.Write((bool)v ? "true" : "false"); return; }
        if (v is double) { Num(w, (double)v); return; }
        if (v is float) { Num(w, (double)(float)v); return; }
        if (v is Enum) { Str(w, v.ToString()); return; }
        if (v is int || v is long || v is short || v is byte || v is uint || v is ushort || v is sbyte || v is ulong || v is decimal)
        { w.Write(Convert.ToString(v, Inv)); return; }
        if (v is JObj)
        {
            JObj o = (JObj)v;
            if (o.Count == 0) { w.Write("{}"); return; }
            w.Write('{');
            for (int i = 0; i < o.Count; i++)
            {
                if (i > 0) w.Write(',');
                Indent(w, d + 1); Str(w, o[i].Key); w.Write(": "); Write(w, o[i].Value, d + 1);
            }
            Indent(w, d); w.Write('}');
            return;
        }
        if (v is IEnumerable)
        {
            List<object> items = new List<object>();
            foreach (object x in (IEnumerable)v) items.Add(x);
            if (items.Count == 0) { w.Write("[]"); return; }
            bool allScalar = true;
            foreach (object x in items) if (!IsScalar(x)) { allScalar = false; break; }
            w.Write('[');
            if (allScalar)
            {   // compact numeric/string arrays on one line (curve data can be 26k+ values)
                for (int i = 0; i < items.Count; i++) { if (i > 0) w.Write(','); Write(w, items[i], d + 1); }
                w.Write(']');
                return;
            }
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) w.Write(',');
                Indent(w, d + 1); Write(w, items[i], d + 1);
            }
            Indent(w, d); w.Write(']');
            return;
        }
        Str(w, v.ToString());
    }

    static void Num(TextWriter w, double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) { w.Write("null"); return; }
        w.Write(x.ToString("R", Inv));
    }

    static void Str(TextWriter w, string s)
    {
        w.Write('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': w.Write("\\\""); break;
                case '\\': w.Write("\\\\"); break;
                case '\n': w.Write("\\n"); break;
                case '\r': w.Write("\\r"); break;
                case '\t': w.Write("\\t"); break;
                default:
                    if (c < 0x20) w.Write("\\u" + ((int)c).ToString("x4")); else w.Write(c);
                    break;
            }
        }
        w.Write('"');
    }
}

// ---------------------------------------------------------------- main tool
class PzsMigrate
{
    const string ToolName = "PzsImporter";
    const string ToolVersion = "1.0.0";
    const string SchemaName = "gordon.geosteering.project";
    const string SchemaVersion = "1.0";
    public static string PzsInstallDir = @"C:\Program Files (x86)\Schlumberger\PZS 5.2.0";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string SrcRoot, OutDir, WorkDir;
    static List<string> Warnings = new List<string>();
    static List<JObj> CrossChecks = new List<JObj>();
    static Dictionary<string, string> HashByRel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static List<string> AllRelFiles = new List<string>();

    static void Warn(string s) { Warnings.Add(s); Console.WriteLine("  WARN: " + s); }

    static string Rel(string full)
    {
        string r = Path.GetFullPath(full);
        string root = SrcRoot.TrimEnd('\\') + "\\";
        if (r.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return r.Substring(root.Length);
        return r;
    }

    static string Slug(string s)
    {
        string x = Regex.Replace(s, @"[^A-Za-z0-9._-]+", "_").Trim('_');
        return x.Length == 0 ? "_" : x;
    }

    static string Sha256(string path)
    {
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (SHA256 h = SHA256.Create())
        {
            byte[] b = h.ComputeHash(fs);
            StringBuilder sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    // Original PZS paths are absolute paths from the original workstation (e.g. C:\_PathfinderData\...).
    // Re-root them by finding the longest trailing path suffix that exists under the source root;
    // fall back to a unique file-name match anywhere in the project.
    static string ResolveOriginal(string orig)
    {
        if (string.IsNullOrEmpty(orig)) return null;
        string[] parts = orig.Replace('/', '\\').Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < parts.Length; i++)
        {
            string cand = SrcRoot;
            for (int j = i; j < parts.Length; j++) cand = Path.Combine(cand, parts[j]);
            if (File.Exists(cand)) return Rel(cand);
        }
        string fn = parts.Length > 0 ? parts[parts.Length - 1] : orig;
        string hit = null; int n = 0;
        foreach (string r in AllRelFiles) if (string.Equals(Path.GetFileName(r), fn, StringComparison.OrdinalIgnoreCase)) { hit = r; n++; }
        return n == 1 ? hit : null;
    }


    static double ParseD(string s)
    {
        double d;
        if (double.TryParse(s.Trim(), NumberStyles.Float, Inv, out d)) return d;
        return double.NaN;
    }
    static bool IsNum(string s) { double d; return double.TryParse(s.Trim(), NumberStyles.Float, Inv, out d); }

    // ================================================================ Main
    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL: " + ex);
            return 4;
        }
    }

    static int Run(string[] argv)
    {
        List<string> pos = new List<string>();
        foreach (string a in argv)
        {
            if (a.StartsWith("--pzs-dir=", StringComparison.OrdinalIgnoreCase)) PzsInstallDir = a.Substring(10).Trim('"');
            else pos.Add(a);
        }
        string[] args = pos.ToArray();
        if (args.Length < 2)
        {
            Console.WriteLine("usage: PzsImporter <pzsProjectRoot> <outputDir> [--pzs-dir=<PZS install dir>]");
            return 2;
        }
        SrcRoot = Path.GetFullPath(args[0]).TrimEnd('\\');
        OutDir = Path.GetFullPath(args[1]).TrimEnd('\\');
        if (!Directory.Exists(SrcRoot)) { Console.WriteLine("Source folder not found: " + SrcRoot); return 2; }
        // Safety: never write into the source project or any Schlumberger install/data tree.
        string[] forbidden = { SrcRoot, @"C:\Program Files (x86)\Schlumberger", @"C:\Program Files\Schlumberger", @"C:\ProgramData\Schlumberger" };
        foreach (string f in forbidden)
            if ((OutDir + "\\").StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            { Console.WriteLine("Refusing to write output under protected/source path: " + f); return 2; }
        if (IntPtr.Size != 4) Console.WriteLine("WARNING: not running as 32-bit; PZS assemblies / Jet driver may fail.");

        Directory.CreateDirectory(OutDir);
        WorkDir = Path.Combine(OutDir, "_work");
        Directory.CreateDirectory(WorkDir);
        Console.WriteLine(ToolName + " " + ToolVersion + "  source=" + SrcRoot);

        // ---- 1. inventory
        Console.WriteLine("[1/5] Inventory");
        List<string> files = new List<string>(Directory.GetFiles(SrcRoot, "*", SearchOption.AllDirectories));
        files.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string f in files) AllRelFiles.Add(Rel(f));
        foreach (string f in files) HashByRel[Rel(f)] = Sha256(f);

        JObj handledBy = new JObj(); // rel -> status, filled as we go
        Dictionary<string, string> status = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ---- 2. well databases (.pzsdb)
        Console.WriteLine("[2/5] Well databases (.pzsdb)");
        List<JObj> wells = new List<JObj>();
        Dictionary<string, JObj> wellByRel = new Dictionary<string, JObj>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in files)
        {
            if (!f.EndsWith(".pzsdb", StringComparison.OrdinalIgnoreCase)) continue;
            string rel = Rel(f);
            Console.WriteLine("  " + rel);
            try
            {
                JObj w = ReadWellDb(f, rel);
                wells.Add(w); wellByRel[rel.Replace('\\', '/')] = w;
                status[rel] = "parsed"; notes[rel] = "PZS well database (Jet/Access); exported to wells[] + data/";
            }
            catch (Exception ex) { status[rel] = "error"; notes[rel] = ex.GetType().Name + ": " + ex.Message; Warn("pzsdb " + rel + ": " + ex.Message); }
        }

        // ---- 3. geology / model text files
        Console.WriteLine("[3/5] Geology text files (.geo/.map/.mapline/.beddef/.gmp)");
        JObj geoInputs = new JObj();
        List<JObj> grids = new List<JObj>(), maps = new List<JObj>(), bedSurfaces = new List<JObj>(), geoMods = new List<JObj>();
        Dictionary<string, JObj> gridByRel = new Dictionary<string, JObj>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JObj> gmpByRel = new Dictionary<string, JObj>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in files)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            string rel = Rel(f);
            try
            {
                switch (ext)
                {
                    case ".geo": { JObj g = ParseGeo(f, rel); grids.Add(g); gridByRel[rel.Replace('\\', '/')] = g; status[rel] = "parsed"; notes[rel] = "structure/isopach grid -> geologyInputs.grids"; break; }
                    case ".map": maps.Add(ParseMap(f, rel)); status[rel] = "parsed"; notes[rel] = "map polylines -> geologyInputs.mapFiles"; break;
                    case ".mapline": maps.Add(ParseMapLine(f, rel)); status[rel] = "parsed"; notes[rel] = "map polylines (alt. text layout) -> geologyInputs.mapFiles"; break;
                    case ".beddef": bedSurfaces.Add(ParseBedDef(f, rel)); status[rel] = "parsed"; notes[rel] = "3D bed surface definition -> geologyInputs.bedSurfaces"; break;
                    case ".gmp": { JObj g = ParseGmp(f, rel); geoMods.Add(g); gmpByRel[rel.Replace('\\', '/')] = g; status[rel] = "parsed"; notes[rel] = "geology modification along well path -> geologyInputs.geoModFiles"; break; }
                }
            }
            catch (Exception ex) { status[rel] = "error"; notes[rel] = ex.Message; Warn(ext + " " + rel + ": " + ex.Message); }
        }
        geoInputs.Set("grids", grids).Set("mapFiles", maps).Set("bedSurfaces", bedSurfaces).Set("geoModFiles", geoMods);

        // ---- 4. model configs (.pzscfg/.cfgbak)
        Console.WriteLine("[4/5] Model configs (.pzscfg/.cfgbak)");
        List<JObj> models = new List<JObj>();
        JObj projectFromCfg = null;
        List<string> cfgFiles = new List<string>();
        foreach (string f in files)
            if (f.EndsWith(".pzscfg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cfgbak", StringComparison.OrdinalIgnoreCase)) cfgFiles.Add(f);
        // A .cfgbak whose bytes equal a sibling .pzscfg is recorded as a backup, not exported twice.
        PzsCfg.Resolver = delegate(string o) { string r = ResolveOriginal(o); if (r == null) Warn("Could not re-root referenced file into the project folder: " + o); return r; };
        if (cfgFiles.Count > 0) PzsCfg.Init();
        foreach (string f in cfgFiles)
        {
            string rel = Rel(f);
            if (f.EndsWith(".cfgbak", StringComparison.OrdinalIgnoreCase))
            {
                string twin = Path.ChangeExtension(rel, ".pzscfg");
                if (HashByRel.ContainsKey(twin) && HashByRel[twin] == HashByRel[rel])
                {
                    status[rel] = "parsed"; notes[rel] = "backup of " + twin + " (byte-identical, SHA-256 match) - not exported separately";
                    CrossChecks.Add(new JObj().Set("check", "cfgbak identical to pzscfg").Set("files", new string[] { rel.Replace('\\', '/'), twin.Replace('\\', '/') }).Set("pass", true));
                    continue;
                }
            }
            Console.WriteLine("  " + rel);
            try
            {
                JObj proj;
                JObj m = PzsCfg.ReadModel(f, rel, out proj);
                if (f.EndsWith(".cfgbak", StringComparison.OrdinalIgnoreCase)) m.Set("isBackupFile", true);
                models.Add(m);
                if (projectFromCfg == null) projectFromCfg = proj;
                status[rel] = "parsed"; notes[rel] = "PZS model session (.NET BinaryFormatter) -> models[]";
            }
            catch (Exception ex)
            {
                status[rel] = "error"; notes[rel] = ex.GetType().Name + ": " + ex.Message;
                Warn("pzscfg " + rel + ": " + ex.ToString());
            }
        }

        // well roles from model links
        foreach (JObj m in models)
        {
            JObj links = (JObj)m.Get("wellLinks");
            if (links == null) continue;
            foreach (KeyValuePair<string, object> kv in links)
            {
                JObj l = kv.Value as JObj;
                if (l == null) continue;
                string res = l.Get("file") as string;
                if (res != null && wellByRel.ContainsKey(res))
                    ((List<JObj>)wellByRel[res].Get("roles")).Add(new JObj().Set("model", m.Get("id")).Set("role", kv.Key));
            }
        }

        // cross-checks against sibling text files
        foreach (JObj m in models) CrossCheckModel(m, gridByRel, gmpByRel);

        // ---- 5. inventory + opaque passthrough
        Console.WriteLine("[5/5] Inventory + opaque passthrough");
        List<JObj> inventory = new List<JObj>();
        Dictionary<string, string> firstByHash = new Dictionary<string, string>();
        foreach (string f in files)
        {
            string rel = Rel(f);
            string ext = Path.GetExtension(f).ToLowerInvariant();
            string st, note, category = Category(ext);
            if (status.ContainsKey(rel)) { st = status[rel]; note = notes[rel]; }
            else if (ext == ".swrcfg" || ext == ".pzsgcfg")
            {
                string dst = Path.Combine(Path.Combine(OutDir, "opaque"), rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(f, dst, true);
                st = "opaque-copied"; note = "binary PZS config (GUID tag + BinaryWriter primitives), not yet decoded; copied verbatim to opaque/" + rel.Replace('\\', '/');
            }
            else { st = "inventoried"; note = Describe(ext); }
            JObj e = new JObj().Set("path", rel.Replace('\\', '/')).Set("sizeBytes", new FileInfo(f).Length)
                .Set("sha256", HashByRel[rel]).Set("category", category).Set("status", st).Set("note", note);
            string h = HashByRel[rel];
            if (firstByHash.ContainsKey(h)) e.Set("duplicateOf", firstByHash[h].Replace('\\', '/'));
            else firstByHash[h] = rel;
            inventory.Add(e);
        }

        // ---- assemble project.json
        JObj project = projectFromCfg ?? new JObj();
        project.Insert(0, new KeyValuePair<string, object>("name", Path.GetFileName(SrcRoot)));

        JObj doc = new JObj()
            .Set("schema", new JObj().Set("name", SchemaName).Set("version", SchemaVersion))
            .Set("migration", new JObj()
                .Set("tool", ToolName).Set("toolVersion", ToolVersion)
                .Set("generatedUtc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv))
                .Set("sourceFormat", "Schlumberger Pathfinder PZS 5.2.0 project folder")
                .Set("sourceRoot", SrcRoot)
                .Set("conventions", new JObj()
                    .Set("paths", "All file paths are relative to sourceRoot (forward slashes). originalPath = absolute path as stored by PZS on the original workstation.")
                    .Set("coordinates", "north/east are local project offsets in project.lengthUnit; tvd is true vertical depth (positive down).")
                    .Set("grids", "grid values are indexed [iNorth][iEast], iNorth=0 is the south edge, iEast=0 the west edge.")
                    .Set("curveData", "bulk curve samples live in data/<well>/<curve>.json as column arrays; wells[].curves[] holds metadata + dataFile.")
                    .Set("curveSuffixes", "_M = modeled (derived), _A = actual/real-time, _S = source/pilot (PZS naming convention)."))
                .Set("warnings", Warnings)
                .Set("crossChecks", CrossChecks))
            .Set("project", project)
            .Set("wells", wells)
            .Set("models", models)
            .Set("geologyInputs", geoInputs)
            .Set("fileInventory", inventory);

        string outJson = Path.Combine(OutDir, "project.json");
        Json.WriteFile(outJson, doc);

        PzsCfg.SuppressAllFinalizers();
        try { Directory.Delete(WorkDir, true); } catch { }

        int bad = 0; foreach (JObj c in CrossChecks) if (!(bool)c.Get("pass")) bad++;
        Console.WriteLine();
        Console.WriteLine("Wrote " + outJson);
        Console.WriteLine("Wells: " + wells.Count + ", models: " + models.Count + ", grids: " + grids.Count + ", map files: " + maps.Count
            + ", bed surfaces: " + bedSurfaces.Count + ", geomod files: " + geoMods.Count + ", inventory: " + inventory.Count);
        Console.WriteLine("Cross-checks: " + CrossChecks.Count + " (" + bad + " failed), warnings: " + Warnings.Count);
        return bad == 0 ? 0 : 3;
    }

    static string Category(string ext)
    {
        switch (ext)
        {
            case ".pzsdb": return "pzs-well-database";
            case ".pzscfg": case ".cfgbak": return "pzs-model-config";
            case ".geo": case ".map": case ".mapline": case ".beddef": case ".gmp": return "pzs-geology-text";
            case ".swrcfg": case ".pzsgcfg": return "pzs-binary-config";
            case ".dig": return "pzs-map-digitizer";
            case ".las": return "vendor-las";
            case ".asc": case ".dat": case ".txt": case ".csv": return "ascii-data";
            case ".pdf": case ".jpg": case ".png": case ".pptx": case ".xls": case ".xlsx": case ".doc": case ".docx": return "reference-document";
            default: return "other";
        }
    }

    static string Describe(string ext)
    {
        switch (ext)
        {
            case ".las": return "LAS log/survey file (open standard; import directly with any LAS reader)";
            case ".dig": return "PZS map digitizer file: binary header + embedded PNG image (digitized structure map); not decoded";
            case ".csv": return "CSV (contour points digitized from a map; source of a .map/.mapline)";
            case ".asc": case ".dat": case ".txt": return "ASCII data export (surveys/log data)";
            default: return "not interpreted by this tool";
        }
    }

    // ================================================================ .pzsdb
    static readonly string[] OdbcDrivers = { "Microsoft Access Driver (*.mdb, *.accdb)", "Microsoft Access Driver (*.mdb)" };

    static OdbcConnection OpenJet(string mdb)
    {
        Exception last = null;
        foreach (string d in OdbcDrivers)
        {
            OdbcConnection c = new OdbcConnection("Driver={" + d + "};DBQ=" + mdb + ";ReadOnly=1;Exclusive=0;");
            try { c.Open(); return c; }
            catch (Exception ex) { last = ex; c.Dispose(); }
        }
        throw new Exception("No usable Access ODBC driver: " + (last == null ? "" : last.Message));
    }

    // Jet's ODBC driver only supports SQLGetData in ascending column order, so read each row
    // left-to-right once and look values up by name afterwards.
    static Dictionary<string, object> Row(OdbcDataReader r)
    {
        Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < r.FieldCount; i++)
        {
            object v = r.IsDBNull(i) ? null : r.GetValue(i);
            if (v is string) v = ((string)v).Trim();
            d[r.GetName(i)] = v;
        }
        return d;
    }
    static object Col(Dictionary<string, object> r, string name) { object v; return r.TryGetValue(name, out v) ? v : null; }

    static string S(object v) { return v == null ? null : Convert.ToString(v, Inv); }
    static double Dn(object v) { return v == null ? double.NaN : Convert.ToDouble(v, Inv); }

    static JObj ReadWellDb(string path, string rel)
    {
        string wellSlug = Slug(Path.ChangeExtension(rel, null));
        string tmp = Path.Combine(WorkDir, wellSlug + ".mdb");
        File.Copy(path, tmp, true); // never let Jet touch the original (lock files, etc.)
        File.SetAttributes(tmp, FileAttributes.Normal);

        JObj well = new JObj();
        well.Set("id", wellSlug).Set("sourceFile", rel.Replace('\\', '/'));
        List<JObj> roles = new List<JObj>();

        using (OdbcConnection c = OpenJet(tmp))
        {
            // DBAttribute
            JObj dbProps = new JObj();
            using (OdbcCommand cmd = new OdbcCommand("SELECT * FROM DBAttribute", c))
            using (OdbcDataReader r = cmd.ExecuteReader())
                while (r.Read()) { Dictionary<string, object> row = Row(r); dbProps.Set(S(Col(row, "PropertyName")), S(Col(row, "PropertyValue"))); }

            // Attribute (LAS-header-like, grouped by Section)
            List<JObj> attrs = new List<JObj>();
            JObj header = new JObj();
            JObj pzsAttrs = new JObj();
            using (OdbcCommand cmd = new OdbcCommand("SELECT * FROM Attribute", c))
            using (OdbcDataReader r = cmd.ExecuteReader())
                while (r.Read())
                {
                    Dictionary<string, object> row = Row(r);
                    int sec =Convert.ToInt32(Col(row, "Section"), Inv);
                    string mn = S(Col(row, "Mnemonic")), unit = S(Col(row, "Unit")), val = S(Col(row, "DataValue")), cm = S(Col(row, "Comment"));
                    attrs.Add(new JObj().Set("section", sec).Set("sectionName", SectionName(sec)).Set("mnemonic", mn)
                        .Set("unit", string.IsNullOrEmpty(unit) ? null : unit).Set("value", val).Set("description", string.IsNullOrEmpty(cm) ? null : cm));
                    if (sec == 2 && mn != null && header.Get(mn) == null) header.Set(mn, val);
                    if (sec == 6 && mn != null) pzsAttrs.Set(mn, val);
                }

            string wellName = header.Get("WELL") as string;
            well.Set("name", wellName ?? Path.GetFileNameWithoutExtension(path));
            well.Set("roles", roles);
            well.Set("databaseInfo", dbProps);
            JObj depthRef = new JObj();
            if (pzsAttrs.Get("DepthUnit") != null) depthRef.Set("depthUnit", pzsAttrs.Get("DepthUnit"));
            if (pzsAttrs.Get("TieInNS") != null)
                depthRef.Set("tieIn", new JObj().Set("north", ParseD((string)pzsAttrs.Get("TieInNS")))
                    .Set("east", ParseD((string)(pzsAttrs.Get("TieInEW") ?? "NaN"))).Set("tvd", ParseD((string)(pzsAttrs.Get("TieInTVD") ?? "NaN"))));
            well.Set("reference", depthRef);
            well.Set("header", header);
            well.Set("headerAttributes", attrs);

            // Survey
            List<JObj> stations = new List<JObj>();
            using (OdbcCommand cmd = new OdbcCommand("SELECT * FROM Survey", c))
            using (OdbcDataReader r = cmd.ExecuteReader())
                while (r.Read())
                {
                    Dictionary<string, object> row = Row(r);
                    stations.Add(new JObj().Set("md", Dn(Col(row, "MD"))).Set("inclination", Dn(Col(row, "Inclination")))
                        .Set("azimuth", Dn(Col(row, "Azimuth"))).Set("type", Col(row, "Type") == null ? (object)null : Convert.ToInt32(Col(row, "Type"), Inv)));
                }
            well.Set("surveys", new JObj().Set("angleUnit", "deg").Set("depthUnit", pzsAttrs.Get("DepthUnit"))
                .Set("stationCount", stations.Count).Set("stations", stations));

            // Dataset
            List<JObj> curves = new List<JObj>();
            using (OdbcCommand cmd = new OdbcCommand("SELECT * FROM Dataset", c))
            using (OdbcDataReader r = cmd.ExecuteReader())
                while (r.Read())
                {
                    Dictionary<string, object> row = Row(r);
                    string name = S(Col(row, "DatasetName"));
                    int ncol = Convert.ToInt32(Col(row, "TotalColumns"), Inv);
                    int nrec = Convert.ToInt32(Col(row, "TotalRecords"), Inv);
                    double start = Dn(Col(row, "Start")), stop = Dn(Col(row, "Stop"));
                    string dsType = S(Col(row, "DatasetType")), depthUnit = S(Col(row, "DepthUnit")), valueUnit = S(Col(row, "ValueUnit"));
                    if (string.IsNullOrEmpty(depthUnit)) depthUnit = pzsAttrs.Get("DepthUnit") as string;
                    byte[] blob = Col(row, "Data") as byte[];

                    JObj cv = new JObj();
                    cv.Set("name", name).Set("kind", CurveKind(name, dsType)).Set("datasetType", dsType)
                      .Set("depthType", S(Col(row, "DepthType"))).Set("depthUnit", depthUnit).Set("valueUnit", valueUnit)
                      .Set("description", NullIfEmpty(S(Col(row, "Comment"))))
                      .Set("recordCount", nrec).Set("columnCount", ncol)
                      .Set("declaredStart", start).Set("declaredStop", stop).Set("declaredStep", Dn(Col(row, "Step")))
                      .Set("interpolateFlag", Col(row, "InterpolateFlag") == null ? (object)null : Convert.ToInt32(Col(row, "InterpolateFlag"), Inv));

                    string[] colNames = ColumnNames(name, dsType, ncol);
                    List<JObj> colMeta = new List<JObj>();
                    for (int k = 0; k < ncol; k++)
                    {
                        JObj cm = new JObj().Set("key", colNames[k]).Set("unit", k == 0 ? depthUnit : (k == 1 ? valueUnit : null));
                        if (colNames[k] == "pilotReferenceDepth") { cm.Set("unit", depthUnit); cm.Set("note", "INFERRED (not confirmed from code): correlated MD in the source/pilot well for this model station"); }
                        else if (colNames[k].StartsWith("col")) cm.Set("note", "meaning not yet identified");
                        colMeta.Add(cm);
                    }
                    cv.Set("columns", colMeta);

                    if (blob == null) { Warn(rel + " dataset " + name + ": empty Data blob"); curves.Add(cv); continue; }
                    double[][] cols = DecodeDatasetBlob(blob, ncol, nrec, rel + ":" + name);

                    // stats + cross-check against declared Start/Stop
                    double d0 = nrec > 0 ? cols[0][0] : double.NaN, dN = nrec > 0 ? cols[0][nrec - 1] : double.NaN;
                    double vmin = double.MaxValue, vmax = double.MinValue; int nulls = 0;
                    if (ncol > 1)
                        foreach (double v in cols[1])
                        {
                            if (double.IsNaN(v) || v == -999.25) { nulls++; continue; }
                            if (v < vmin) vmin = v; if (v > vmax) vmax = v;
                        }
                    cv.Set("depthStart", d0).Set("depthStop", dN);
                    cv.Set("valueMin", vmin == double.MaxValue ? double.NaN : vmin).Set("valueMax", vmax == double.MinValue ? double.NaN : vmax).Set("nullCount", nulls);
                    bool ok = Math.Abs(Math.Min(d0, dN) - Math.Min(start, stop)) < 1e-6 && Math.Abs(Math.Max(d0, dN) - Math.Max(start, stop)) < 1e-6;
                    CrossChecks.Add(new JObj().Set("check", "decoded depth range == Dataset.Start/Stop")
                        .Set("file", rel.Replace('\\', '/')).Set("curve", name)
                        .Set("declared", new double[] { start, stop }).Set("decoded", new double[] { d0, dN }).Set("pass", ok));

                    string dataRel = "data/" + wellSlug + "/" + Slug(name) + ".json";
                    JObj data = new JObj().Set("well", wellSlug).Set("curve", name).Set("recordCount", nrec);
                    JObj colData = new JObj();
                    for (int k = 0; k < ncol; k++) colData.Set(colNames[k], cols[k]);
                    data.Set("columns", colMeta).Set("data", colData);
                    Json.WriteFile(Path.Combine(OutDir, dataRel.Replace('/', '\\')), data);
                    cv.Set("dataFile", dataRel);
                    curves.Add(cv);
                }
            well.Set("curves", curves);
        }
        try { File.Delete(tmp); } catch { }
        return well;
    }

    static string NullIfEmpty(string s) { return string.IsNullOrEmpty(s) ? null : s; }

    static string SectionName(int s)
    {
        switch (s)
        {
            case 1: return "version";      // LAS ~V
            case 2: return "well";         // LAS ~W
            case 3: return "curve";        // LAS ~C (not seen in samples)
            case 4: return "parameter";    // LAS ~P / remarks
            case 6: return "pzs";          // PZS-specific (DepthUnit, TieIn*)
            default: return "section" + s;
        }
    }

    static string CurveKind(string name, string dsType)
    {
        if (dsType != null && dsType != "CURVE") return dsType.ToLowerInvariant();
        if (name.EndsWith("_M")) return "modeled";
        if (name.EndsWith("_A")) return "actual";
        if (name.EndsWith("_S")) return "source";
        return "measured";
    }

    static string[] ColumnNames(string name, string dsType, int ncol)
    {
        string[] n = new string[ncol];
        for (int k = 0; k < ncol; k++) n[k] = "col" + k;
        if (ncol >= 1) n[0] = "md";
        if (ncol >= 2) n[1] = "value";
        if (ncol == 3 && name.EndsWith("_M")) n[2] = "pilotReferenceDepth";
        return n;
    }

    // Dataset.Data = gzip( NRBF stream: SerializationHeaderRecord(17) + ArraySinglePrimitive(0x0F, objId, length, type=Byte) + bytes + MessageEnd(0x0B) )
    // payload = row-major little-endian doubles, ncol per record.
    static double[][] DecodeDatasetBlob(byte[] blob, int ncol, int nrec, string ctx)
    {
        byte[] raw;
        using (MemoryStream ms = new MemoryStream(blob))
        using (GZipStream gz = new GZipStream(ms, CompressionMode.Decompress))
        using (MemoryStream o = new MemoryStream())
        {
            byte[] buf = new byte[65536]; int n;
            while ((n = gz.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, n);
            raw = o.ToArray();
        }
        if (raw.Length < 28 || raw[0] != 0x00) throw new Exception(ctx + ": not an NRBF stream after gunzip");
        if (raw[17] != 0x0F || raw[26] != 0x02) throw new Exception(ctx + ": expected ArraySinglePrimitive<Byte> record, got type 0x" + raw[17].ToString("X2"));
        int len = BitConverter.ToInt32(raw, 22);
        if (27 + len + 1 != raw.Length || raw[raw.Length - 1] != 0x0B) Warn(ctx + ": unexpected NRBF framing (len=" + len + ", total=" + raw.Length + ")");
        long expect = (long)ncol * nrec * 8;
        if (len != expect) throw new Exception(ctx + ": payload " + len + " bytes != TotalColumns*TotalRecords*8 = " + expect);
        double[][] cols = new double[ncol][];
        for (int k = 0; k < ncol; k++) cols[k] = new double[nrec];
        int p = 27;
        for (int i = 0; i < nrec; i++)
            for (int k = 0; k < ncol; k++) { cols[k][i] = BitConverter.ToDouble(raw, p); p += 8; }
        return cols;
    }

    // ================================================================ text formats
    static string[] ReadLines(string path) { return File.ReadAllLines(path, Encoding.Default); }

    static void ReadKeyHeader(string line, JObj target)
    {
        string t = line.Trim();
        if (t.StartsWith("Version ")) target.Set("formatVersion", t.Substring(8).Trim());
        else if (t.StartsWith("LengthUnit ")) target.Set("lengthUnit", t.Substring(11).Trim());
        else if (t.StartsWith("Origin ")) target.Set("originDescription", t.Substring(7).Trim());
    }

    // .geo: Version/LengthUnit/Origin header, fault block, then (nBeds, nWE, nNS), extents, per-bed grids
    static JObj ParseGeo(string path, string rel)
    {
        string[] lines = ReadLines(path);
        JObj g = new JObj().Set("sourceFile", rel.Replace('\\', '/'));
        int i = 0;
        for (; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("Version") || t.StartsWith("LengthUnit") || t.StartsWith("Origin")) { ReadKeyHeader(t, g); continue; }
            if (t.StartsWith("#") || t.Length == 0) continue;
            break;
        }
        // numeric token stream until first bed name line
        // header numbers: faultDir, nFaults, [nFaults*(distance, deltaTvd)], nBeds, nWE, nNS, west, east, south, north
        int j = i;
        List<double> hdr = new List<double>();
        for (; j < lines.Length; j++)
        {
            string t = lines[j].Trim();
            if (t.StartsWith("#") || t.Length == 0) continue;
            string[] toks = t.Split(new char[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            bool allNum = true; foreach (string tk in toks) if (!IsNum(tk)) { allNum = false; break; }
            if (!allNum) break;
            foreach (string tk in toks) hdr.Add(ParseD(tk));
        }
        int h = 0;
        double faultDir = hdr[h++]; int nFaults = (int)hdr[h++];
        List<double[]> faultTable = new List<double[]>();
        for (int k = 0; k < nFaults; k++) { faultTable.Add(new double[] { hdr[h], hdr[h + 1] }); h += 2; }
        int nBeds = (int)hdr[h++], nWE = (int)hdr[h++], nNS = (int)hdr[h++];
        double west = hdr[h++], east = hdr[h++], south = hdr[h++], north = hdr[h++];
        if (h != hdr.Count) Warn(rel + ": .geo header had " + (hdr.Count - h) + " unexpected extra numbers");
        g.Set("faults", new JObj().Set("directionDeg", faultDir).Set("count", nFaults).Set("table", faultTable).Set("tableColumns", new string[] { "distance", "deltaTvd" }));
        double[] xs = new double[nWE], ys = new double[nNS];
        for (int k = 0; k < nWE; k++) xs[k] = nWE == 1 ? west : west + (east - west) * k / (nWE - 1);
        for (int k = 0; k < nNS; k++) ys[k] = nNS == 1 ? south : south + (north - south) * k / (nNS - 1);
        g.Set("extent", new JObj().Set("west", west).Set("east", east).Set("south", south).Set("north", north))
         .Set("eastAxis", xs).Set("northAxis", ys).Set("nEast", nWE).Set("nNorth", nNS);

        List<JObj> beds = new List<JObj>();
        string bedName = null; List<double> vals = null;
        Action flush = delegate
        {
            if (bedName == null) return;
            if (vals.Count != nWE * nNS) Warn(rel + ": bed '" + bedName + "' has " + vals.Count + " values, expected " + (nWE * nNS));
            double[][] grid = new double[nNS][];
            for (int r = 0; r < nNS; r++) { grid[r] = new double[nWE]; for (int c = 0; c < nWE; c++) { int idx = r * nWE + c; grid[r][c] = idx < vals.Count ? vals[idx] : double.NaN; } }
            beds.Add(new JObj().Set("name", bedName).Set("values", grid));
        };
        for (; j < lines.Length; j++)
        {
            string t = lines[j].Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;
            string[] toks = t.Split(new char[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            bool allNum = true; foreach (string tk in toks) if (!IsNum(tk)) { allNum = false; break; }
            if (!allNum) { flush(); bedName = t; vals = new List<double>(); continue; }
            foreach (string tk in toks) vals.Add(ParseD(tk));
        }
        flush();
        if (beds.Count != nBeds) Warn(rel + ": declared " + nBeds + " beds, found " + beds.Count);
        g.Set("valueMeaning", "depth of bed surface relative to the geology origin datum (PZS adds geology origin TVD to get TVD)");
        g.Set("beds", beds);
        return g;
    }

    static JObj Pt(string line, out bool ok)
    {
        ok = false;
        string[] toks = line.Split(new char[] { ',' }, StringSplitOptions.None);
        if (toks.Length < 2 || !IsNum(toks[0]) || !IsNum(toks[1])) return null;
        ok = true;
        JObj p = new JObj().Set("north", ParseD(toks[0])).Set("east", ParseD(toks[1]));
        if (toks.Length > 2 && toks[2].Trim().Length > 0) p.Set("label", toks[2].Trim());
        return p;
    }

    // .map: header, then blocks of "MapObject" / <name> / <type e.g. PolyLine> / "north, east[, label]" rows
    static JObj ParseMap(string path, string rel)
    {
        string[] lines = ReadLines(path);
        JObj m = new JObj().Set("sourceFile", rel.Replace('\\', '/')).Set("layout", "map");
        List<JObj> objs = new List<JObj>();
        JObj cur = null; List<JObj> pts = null;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (cur == null && (t.StartsWith("Version") || t.StartsWith("LengthUnit") || t.StartsWith("Origin"))) { ReadKeyHeader(t, m); continue; }
            if (t == "MapObject")
            {
                string nm = i + 1 < lines.Length ? lines[i + 1].Trim() : "";
                string ty = i + 2 < lines.Length ? lines[i + 2].Trim() : "";
                pts = new List<JObj>();
                cur = new JObj().Set("name", nm).Set("type", ty).Set("points", pts);
                objs.Add(cur); i += 2; continue;
            }
            bool ok; JObj p = Pt(t, out ok);
            if (ok && pts != null) pts.Add(p);
            else Warn(rel + ": unrecognised line " + (i + 1) + ": " + t);
        }
        m.Set("pointLayout", "north, east[, label]").Set("objects", objs);
        return m;
    }

    // .mapline: "0,0" header line, "*** <origin>" then "*** <name>" blocks of "north, east[, n]" rows
    static JObj ParseMapLine(string path, string rel)
    {
        string[] lines = ReadLines(path);
        JObj m = new JObj().Set("sourceFile", rel.Replace('\\', '/')).Set("layout", "mapline");
        List<JObj> objs = new List<JObj>();
        List<JObj> pts = null; bool sawOrigin = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (i == 0 && !t.StartsWith("***")) { m.Set("headerLine", t); continue; }
            if (t.StartsWith("***"))
            {
                string body = t.Substring(3).Trim();
                if (!sawOrigin) { sawOrigin = true; m.Set("originDescription", body); continue; }
                pts = new List<JObj>();
                objs.Add(new JObj().Set("name", body).Set("type", "PolyLine").Set("points", pts));
                continue;
            }
            bool ok; JObj p = Pt(t, out ok);
            if (ok && pts != null) pts.Add(p);
            else Warn(rel + ": unrecognised line " + (i + 1) + ": " + t);
        }
        m.Set("pointLayout", "north, east[, label]").Set("objects", objs);
        return m;
    }

    // .beddef: count, origin triple, then "*** <bed>" / "*** <column header>" / rows
    static JObj ParseBedDef(string path, string rel)
    {
        string[] lines = ReadLines(path);
        JObj b = new JObj().Set("sourceFile", rel.Replace('\\', '/'));
        int i = 0;
        while (i < lines.Length && lines[i].Trim().Length == 0) i++;
        b.Set("declaredCount", (int)ParseD(lines[i++]));
        string[] o = lines[i++].Split(',');
        b.Set("origin", new JObj().Set("north", ParseD(o[0])).Set("east", ParseD(o[1])).Set("tvd", o.Length > 2 ? ParseD(o[2]) : 0.0));
        List<JObj> beds = new List<JObj>(); List<JObj> rows = null;
        for (; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("***"))
            {
                string body = t.Substring(3).Trim();
                if (body.StartsWith("North,", StringComparison.OrdinalIgnoreCase)) continue; // column header
                rows = new List<JObj>();
                beds.Add(new JObj().Set("name", body).Set("points", rows));
                continue;
            }
            string[] tk = t.Split(',');
            if (tk.Length < 7 || rows == null) { Warn(rel + ": unrecognised line " + (i + 1)); continue; }
            int id = (int)ParseD(tk[5]);
            rows.Add(new JObj().Set("north", ParseD(tk[0])).Set("east", ParseD(tk[1])).Set("tvd", ParseD(tk[2]))
                .Set("dip", ParseD(tk[3])).Set("dipAzimuth", ParseD(tk[4])).Set("controlPointId", id < 0 ? (object)null : id)
                .Set("isControlPoint", id >= 0).Set("distance", ParseD(tk[6])));
        }
        if (beds.Count != (int)b.Get("declaredCount"))
            b.Set("note", "declaredCount (" + b.Get("declaredCount") + ") != number of bed blocks found (" + beds.Count + "); meaning of the first line is not confirmed");
        b.Set("beds", beds);
        return b;
    }

    // .gmp: header + rows "md deltaTvd fault appDip bedName" with continuation rows "deltaTvd appDip bedName"
    static JObj ParseGmp(string path, string rel)
    {
        string[] lines = ReadLines(path);
        JObj g = new JObj().Set("sourceFile", rel.Replace('\\', '/'));
        List<JObj> stations = new List<JObj>(); List<JObj> beds = null;
        foreach (string line in lines)
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("#"))
            {
                Match wm = Regex.Match(t, @"written time:\s*(.+)$");
                if (wm.Success) g.Set("writtenTime", wm.Groups[1].Value.Trim());
                continue;
            }
            if (t.StartsWith("Version") || t.StartsWith("LengthUnit") || t.StartsWith("Origin")) { ReadKeyHeader(t, g); continue; }
            string[] tk = t.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int nNum = 0; while (nNum < tk.Length && IsNum(tk[nNum])) nNum++;
            string bed = string.Join(" ", tk, nNum, tk.Length - nNum);
            if (nNum >= 4)
            {
                beds = new List<JObj>();
                stations.Add(new JObj().Set("md", ParseD(tk[0])).Set("fault", ParseD(tk[2])).Set("beds", beds));
                beds.Add(new JObj().Set("bed", bed).Set("deltaTvd", ParseD(tk[1])).Set("apparentDip", ParseD(tk[3])));
            }
            else if (nNum == 2 && beds != null)
                beds.Add(new JObj().Set("bed", bed).Set("deltaTvd", ParseD(tk[0])).Set("apparentDip", ParseD(tk[1])));
            else Warn(rel + ": unrecognised .gmp line: " + t);
        }
        g.Set("stations", stations);
        return g;
    }

    // ================================================================ cross-checks: model vs sibling text files
    static void CrossCheckModel(JObj m, Dictionary<string, JObj> gridByRel, Dictionary<string, JObj> gmpByRel)
    {
        JObj geo = m.Get("geologyModel") as JObj;
        if (geo == null) return;
        // geology grid: cfg bed grid - .geo grid == origin TVD (constant)
        JObj gl = geo.Get("gridFile") as JObj;
        string gridRel = gl == null ? null : gl.Get("file") as string;
        if (gridRel != null && gridByRel.ContainsKey(gridRel))
        {
            JObj gf = gridByRel[gridRel];
            double originTvd = (double)((JObj)geo.Get("origin")).Get("tvd");
            foreach (JObj bed in (List<JObj>)geo.Get("beds"))
            {
                JObj grid = bed.Get("grid") as JObj; if (grid == null) continue;
                double[][] a = (double[][])grid.Get("tvd");
                JObj fb = null;
                foreach (JObj x in (List<JObj>)gf.Get("beds")) if ((string)x.Get("name") == (string)bed.Get("name")) fb = x;
                if (fb == null) { CrossChecks.Add(new JObj().Set("check", "model bed grid vs .geo").Set("bed", bed.Get("name")).Set("pass", false).Set("detail", "bed not in .geo")); continue; }
                double[][] b = (double[][])fb.Get("values");
                double maxDev = 0;
                for (int r = 0; r < a.Length && r < b.Length; r++)
                    for (int c = 0; c < a[r].Length && c < b[r].Length; c++)
                        maxDev = Math.Max(maxDev, Math.Abs(a[r][c] - b[r][c] - originTvd));
                CrossChecks.Add(new JObj().Set("check", "model bed grid TVD == .geo grid value + geology origin TVD")
                    .Set("model", m.Get("id")).Set("bed", bed.Get("name")).Set("geoFile", gridRel.Replace('\\', '/'))
                    .Set("originTvd", originTvd).Set("maxAbsDeviation", maxDev).Set("pass", maxDev < 1e-3));
            }
        }
        // steering points vs .gmp file
        JObj gmpLink = m.Get("gmpFile") as JObj;
        string gmpRel = gmpLink == null ? null : gmpLink.Get("file") as string;
        JObj st = geo.Get("steering") as JObj;
        // PZS keeps the live geomod state in the .pzscfg; .gmp files are snapshots the user saved. Compare
        // against every .gmp in the project and report which (if any) match exactly.
        if (st != null && gmpByRel.Count > 0)
        {
            List<JObj> pts = (List<JObj>)st.Get("points");
            List<JObj> per = new List<JObj>();
            List<string> exact = new List<string>();
            foreach (KeyValuePair<string, JObj> kv in gmpByRel)
            {
                List<JObj> gs = (List<JObj>)kv.Value.Get("stations");
                double maxDev = 0; int firstMismatch = -1;
                for (int i = 0; i < Math.Max(pts.Count, gs.Count); i++)
                {
                    if (i >= pts.Count || i >= gs.Count) { if (firstMismatch < 0) firstMismatch = i; break; }
                    double dev = Math.Abs((double)pts[i].Get("md") - (double)gs[i].Get("md"));
                    double[] dt = pts[i].Get("deltaTvd") as double[];
                    List<JObj> gb = (List<JObj>)gs[i].Get("beds");
                    if (dt != null) for (int k = 0; k < dt.Length && k < gb.Count; k++) dev = Math.Max(dev, Math.Abs(dt[k] - (double)gb[k].Get("deltaTvd")));
                    if (dev >= 1e-3 && firstMismatch < 0) firstMismatch = i;
                    maxDev = Math.Max(maxDev, dev);
                }
                bool match = firstMismatch < 0;
                if (match) exact.Add(kv.Key);
                per.Add(new JObj().Set("gmpFile", kv.Key).Set("writtenTime", kv.Value.Get("writtenTime")).Set("stations", gs.Count)
                    .Set("exactMatch", match).Set("firstMismatchIndex", match ? null : (object)firstMismatch));
            }
            CrossChecks.Add(new JObj().Set("check", "model steering points vs project .gmp snapshots (md + deltaTvd per bed, tol 1e-3; .gmp has 4 dp)")
                .Set("model", m.Get("id")).Set("modelPoints", pts.Count)
                .Set("referencedGmpFile", gmpRel)
                .Set("exactMatchFiles", exact)
                .Set("referencedFileMatches", gmpRel != null && exact.Contains(gmpRel))
                .Set("perFile", per)
                .Set("note", "the .pzscfg holds the live interpretation; GMPFileName is only the last .gmp path used and need not match it")
                .Set("pass", exact.Count > 0));
            if (gmpRel != null && !exact.Contains(gmpRel))
                Warn("model " + m.Get("id") + ": steering points do not match referenced " + gmpRel + "; exact match(es): " + (exact.Count == 0 ? "none" : string.Join(", ", exact.ToArray())) + ". models[].geologyModel.steering (from .pzscfg) is authoritative.");
        }
    }
}

// ================================================================ .pzscfg reader
static class PzsCfg
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] Excluded = { "SKCLNET", "PFLICENSE", "LICENINF", "INSTALLPZSLICENSE", "UNINS000" };
    static readonly HashSet<string> AllowedSystemAsm = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mscorlib", "System", "System.Drawing" };
    static HashSet<string> PzsAsmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static List<object> Roots = new List<object>();
    static HashSet<string> MissingFieldWarned = new HashSet<string>();
    static bool inited;

    public static void Init()
    {
        if (inited) return; inited = true;
        AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        if (!Directory.Exists(PzsMigrate.PzsInstallDir))
            throw new DirectoryNotFoundException("PZS 5.2.0 install folder not found (needed to read .pzscfg): " + PzsMigrate.PzsInstallDir + " - pass --pzs-dir=<path>");
        LoadDir(PzsMigrate.PzsInstallDir);
        LoadDir(Path.Combine(PzsMigrate.PzsInstallDir, "VDC"));
    }

    static bool IsExcluded(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
        return Array.IndexOf(Excluded, n) >= 0;
    }

    static void LoadDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        List<string> fs = new List<string>();
        fs.AddRange(Directory.GetFiles(dir, "*.dll")); fs.AddRange(Directory.GetFiles(dir, "*.exe"));
        foreach (string f in fs)
        {
            if (IsExcluded(f)) continue; // licensing: out of scope, never loaded
            try { AssemblyName.GetAssemblyName(f); Assembly a = Assembly.LoadFrom(f); PzsAsmNames.Add(a.GetName().Name); }
            catch { } // native DLLs etc.
        }
    }

    static Assembly FindLoaded(string sn)
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            if (string.Equals(a.GetName().Name, sn, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }

    static Assembly OnResolve(object sender, ResolveEventArgs args)
    {
        string sn = new AssemblyName(args.Name).Name;
        if (Array.IndexOf(Excluded, sn.ToUpperInvariant()) >= 0) return null;
        return FindLoaded(sn);
    }

    class Binder : SerializationBinder
    {
        public override Type BindToType(string assemblyName, string typeName)
        {
            string sn = new AssemblyName(assemblyName).Name;
            bool allowed = PzsAsmNames.Contains(sn) || AllowedSystemAsm.Contains(sn);
            foreach (Match mm in Regex.Matches(typeName, @"\[([^\[\],]+),\s*([^,\]]+)"))
            {
                string inner = mm.Groups[2].Value.Trim();
                if (!(PzsAsmNames.Contains(inner) || AllowedSystemAsm.Contains(inner))) allowed = false;
            }
            if (!allowed) throw new SerializationException("Refusing type outside PZS/allowed assemblies: " + sn + " :: " + typeName);
            Assembly a = FindLoaded(sn);
            if (a == null) throw new SerializationException("Assembly not loaded: " + sn);
            Type t = a.GetType(typeName, false) ?? Type.GetType(typeName + ", " + a.FullName, false);
            if (t == null) throw new SerializationException("Type not found: " + sn + " :: " + typeName);
            return t;
        }
    }

    // ---- reflection helpers (fields by name; tolerant of auto-property backing-field naming)
    static FieldInfo FindField(Type t, string name)
    {
        for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
            foreach (FieldInfo f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (f.Name == name || f.Name == "<" + name + ">k__BackingField" || f.Name == "<backing_store>" + name) return f;
        return null;
    }

    static object F(object o, string path)
    {
        foreach (string part in path.Split('.'))
        {
            if (o == null) return null;
            FieldInfo fi = FindField(o.GetType(), part);
            if (fi == null)
            {
                string key = o.GetType().FullName + "." + part;
                if (MissingFieldWarned.Add(key)) Console.WriteLine("  WARN: field not found: " + key);
                return null;
            }
            o = fi.GetValue(o);
        }
        return o;
    }
    static object D(object o, string path) { object v = F(o, path); return v == null ? null : (object)Convert.ToDouble(v, Inv); }
    static object I(object o, string path) { object v = F(o, path); return v == null ? null : (object)Convert.ToInt32(v, Inv); }
    static object B(object o, string path) { object v = F(o, path); return v == null ? null : (object)(bool)v; }
    static string Str(object o, string path) { object v = F(o, path); return v == null ? null : v.ToString(); }
    static string Name(object o) { return Str(o, "id.Name"); }
    static IList L(object o, string path) { return F(o, path) as IList; }

    static JObj Loc(object loc)
    {
        if (loc == null) return null;
        return new JObj().Set("north", D(loc, "North")).Set("east", D(loc, "East")).Set("tvd", D(loc, "Tvd"));
    }

    static JObj Link(string orig)
    {
        if (string.IsNullOrEmpty(orig)) return null;
        string res = ResolveOriginalPublic(orig);
        return new JObj().Set("originalPath", orig).Set("file", res == null ? null : res.Replace('\\', '/'));
    }
    // indirection so PzsMigrate's resolver (private) is used
    public static Func<string, string> Resolver;
    static string ResolveOriginalPublic(string orig) { return Resolver == null ? null : Resolver(orig); }

    // generic fallback for not-yet-mapped domain objects: simple fields only, bounded depth, skips UI types
    static object Generic(object v, int depth)
    {
        if (v == null) return null;
        Type t = v.GetType();
        if (t.IsPrimitive || v is string || t.IsEnum || v is decimal || v is DateTime) return v is DateTime ? (object)((DateTime)v).ToString("o") : v;
        if (t.Namespace != null && (t.Namespace.StartsWith("System.Drawing") || t.Namespace.StartsWith("System.Windows"))) return null;
        if (depth <= 0) return "(depth limit)";
        if (v is IDictionary)
        {
            JObj o = new JObj();
            foreach (DictionaryEntry e in (IDictionary)v) o.Set(Convert.ToString(e.Key, Inv), Generic(e.Value, depth - 1));
            return o;
        }
        if (v is Array && ((Array)v).Rank == 2)
        {
            Array a = (Array)v; List<object> rows = new List<object>();
            for (int i = 0; i < a.GetLength(0); i++) { List<object> r = new List<object>(); for (int j = 0; j < a.GetLength(1); j++) r.Add(Generic(a.GetValue(i, j), depth - 1)); rows.Add(r); }
            return rows;
        }
        if (v is IEnumerable)
        {
            List<object> l = new List<object>();
            foreach (object x in (IEnumerable)v) l.Add(Generic(x, depth - 1));
            return l;
        }
        JObj obj = new JObj();
        for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
            foreach (FieldInfo f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                object fv = Generic(f.GetValue(v), depth - 1);
                if (fv != null) obj.Set(f.Name.Replace("<backing_store>", "").Replace(">k__BackingField", "").TrimStart('<'), fv);
            }
        return obj;
    }

    static int Count(IList l) { return l == null ? 0 : l.Count; }

    public static JObj ReadModel(string path, string rel, out JObj project)
    {
        object root;
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            BinaryFormatter bf = new BinaryFormatter();
            bf.Binder = new Binder();
            bf.AssemblyFormat = System.Runtime.Serialization.Formatters.FormatterAssemblyStyle.Simple;
            root = bf.Deserialize(fs);
            if (fs.Position != fs.Length) Console.WriteLine("  WARN: " + (fs.Length - fs.Position) + " trailing bytes after object graph");
        }
        Roots.Add(root);
        if (root.GetType().FullName != "PFNSPZS.PFSerialization") throw new Exception("unexpected root type " + root.GetType().FullName);

        object ps = F(root, "ProjectSettings");
        // ---- project-level settings
        project = new JObj()
            .Set("lengthUnit", Str(ps, "lengthUnit"))
            .Set("sonicUnit", Str(ps, "sonicUnit"))
            .Set("locationDescription", Str(ps, "projLocDesc"))
            .Set("surfaceLocation", new JObj().Set("northing", D(ps, "projLocNADNorth")).Set("easting", D(ps, "projLocNADEast"))
                .Set("note", "PZS projLocNADNorth/projLocNADEast as stored (grid coordinates; datum/units not recorded in file)"))
            .Set("verticalSection", new JObj().Set("azimuthDeg", D(ps, "azimuthVS")).Set("originNorth", D(ps, "northVS")).Set("originEast", D(ps, "eastVS")))
            .Set("tvdSubseaOffset", D(root, "tvdSubseaProj"))
            .Set("realtimeRefreshRate", I(ps, "refreshRate"));

        string id = PzsMigrateSlug(Path.GetFileNameWithoutExtension(rel));
        JObj m = new JObj()
            .Set("id", id)
            .Set("name", Path.GetFileNameWithoutExtension(rel))
            .Set("sourceFile", rel.Replace('\\', '/'))
            .Set("pzsDocVersion", Str(ps, "docVersion"))
            .Set("originalPath", Str(ps, "fileFullName"));

        // ---- well links (role -> pzsdb)
        JObj links = new JObj();
        links.Set("model", Link(Str(F(root, "ModelWell"), "fileFullName")));
        links.Set("source", Link(Str(ps, "sourceWellFileFullName")));
        links.Set("actual", Link(Str(ps, "actualWellFileFullName")));
        links.Set("sourceSurvey", Link(Str(ps, "srcSurveyFileFullName")));
        links.Set("actualSurvey", Link(Str(ps, "actSurveyFileFullName")));
        IList addl = L(ps, "additionalWellFileFullNameList");
        int ai = 0;
        if (addl != null) foreach (object a in addl) links.Set("additional" + (ai++), Link(a as string));
        m.Set("wellLinks", links);

        // ---- per-role well settings
        JObj ws = new JObj();
        foreach (string role in new string[] { "ModelWell", "SourceWell", "ActualWell" })
            ws.Set(role.Replace("Well", "").ToLowerInvariant(), WellSettings(F(root, role)));
        List<object> addWells = new List<object>();
        IList aw = L(root, "AdditionalWellList");
        if (aw != null) foreach (object w in aw) addWells.Add(WellSettings(w));
        ws.Set("additional", addWells);
        m.Set("wellSettings", ws);

        // ---- targets
        List<JObj> targets = new List<JObj>();
        IList tl = L(root, "TargetList");
        if (tl != null)
            foreach (object t in tl)
            {
                object td = F(t, "targetData");
                JObj loc = Loc(F(td, "TargetLocation"));
                targets.Add(new JObj()
                    .Set("name", Name(t))
                    .Set("north", loc.Get("north")).Set("east", loc.Get("east")).Set("tvd", loc.Get("tvd"))
                    .Set("inclinationDeg", D(td, "Inclination")).Set("azimuthDeg", D(td, "Azimuth"))
                    .Set("pathType", Str(td, "PathType"))
                    .Set("targetDoglegPerUnitLength", D(td, "TargetDoglegPerUnitDistance"))
                    .Set("maxDoglegPerUnitLength", D(td, "MaxDoglegPerUnitDistance"))
                    .Set("active", B(td, "Active"))
                    .Set("nearestMd", D(t, "mdNear"))
                    .Set("distanceFromWellPath", D(t, "distance"))
                    .Set("isFuture", B(t, "future"))
                    .Set("description", Str(t, "description"))
                    .Set("lengthUnit", ((bool?)B(t, "isDepthUnitFeet") ?? true) ? "feet" : "meters"));
            }
        m.Set("targets", targets);

        // ---- map overlays
        List<JObj> mapObjs = new List<JObj>();
        IList gol = L(root, "GeometryObjectList");
        if (gol != null)
            foreach (object g in gol)
            {
                List<JObj> pts = new List<JObj>();
                IList pl = L(g, "mapPointList");
                if (pl != null)
                    foreach (object p in pl)
                    {
                        JObj l = Loc(F(p, "location"));
                        string txt = Str(p, "text");
                        if (!string.IsNullOrEmpty(txt)) l.Set("label", txt);
                        pts.Add(l);
                    }
                mapObjs.Add(new JObj()
                    .Set("name", Name(g))
                    .Set("type", Str(g, "mapObjectType"))
                    .Set("lengthUnit", Str(g, "lengthUnit.val"))
                    .Set("extent", new JObj().Set("west", D(g, "minEW")).Set("east", D(g, "maxEW")).Set("south", D(g, "minNS")).Set("north", D(g, "maxNS")))
                    .Set("originDescription", Str(g, "mapOriginDesc"))
                    .Set("locationInProject", Loc(F(g, "locationInProject")))
                    .Set("sourceFile", Link(Str(g, "fileFullName")))
                    .Set("points", pts));
            }
        m.Set("mapObjects", mapObjs);

        // ---- geology model
        object geo = F(root, "Geology");
        JObj gm = new JObj();
        if (geo != null)
        {
            gm.Set("origin", Loc(F(geo, "origin")));
            gm.Set("gridFile", Link(Str(geo, "fileFullName")));
            gm.Set("pzsGeologyDataId", I(geo, "geologyDataID"));
            List<JObj> beds = new List<JObj>();
            IList gbl = L(geo, "geologyBedList");
            if (gbl != null)
                foreach (object b in gbl)
                {
                    JObj bj = BedCommon(b);
                    object gr = F(b, "gridRawData");
                    if (gr != null)
                    {
                        double[] x = F(gr, "x") as double[], y = F(gr, "y") as double[];
                        double[][] z = F(gr, "z") as double[][];
                        double[][] rows = null;
                        if (x != null && y != null && z != null)
                        {   // PZS stores z[iEast][iNorth]; emit [iNorth][iEast] (same orientation as the .geo text file)
                            rows = new double[y.Length][];
                            for (int r = 0; r < y.Length; r++) { rows[r] = new double[x.Length]; for (int c = 0; c < x.Length; c++) rows[r][c] = z[c][r]; }
                        }
                        bj.Set("grid", new JObj().Set("nEast", x == null ? 0 : x.Length).Set("nNorth", y == null ? 0 : y.Length)
                            .Set("eastAxis", x).Set("northAxis", y).Set("tvd", rows)
                            .Set("note", "absolute TVD (= .geo value + geology origin tvd). PZS also caches bicubic-spline 2nd derivatives (dZpp); omitted as derivable."));
                    }
                    beds.Add(bj);
                }
            gm.Set("beds", beds);
            gm.Set("editableBedNames", ToStrList(L(geo, "allowChangeBedNameList")));

            List<JObj> markers = new List<JObj>();
            IList mbl = L(geo, "markerBedList");
            if (mbl != null) foreach (object b in mbl) markers.Add(BedCommon(b));
            gm.Set("markers", markers);

            object flt = F(geo, "faults");
            if (flt != null)
            {
                object az = D(flt, "azimuth");
                gm.Set("faults", new JObj().Set("count", I(flt, "numOfFaults"))
                    .Set("azimuthDeg", az == null ? null : (object)((double)az * 180.0 / Math.PI))
                    .Set("north", D(flt, "north")).Set("east", D(flt, "east"))
                    .Set("table", Generic(F(flt, "table"), 3)));
            }

            object pg = F(geo, "pathGeoMod");
            if (pg != null)
            {
                List<JObj> pts = new List<JObj>();
                IList gpl = L(pg, "geoModPointList");
                if (gpl != null)
                    foreach (object p in gpl)
                        pts.Add(new JObj().Set("md", D(p, "Md")).Set("deltaTvd", F(p, "DeltaTvdDipArray"))
                            .Set("slope", F(p, "SlopeArray")).Set("faultThrow", D(p, "DeltaTvdFault")));
                gm.Set("steering", new JObj()
                    .Set("description", "Interpreter's geology modification points along the well path (PZS 'geomod'); deltaTvd/slope arrays are one entry per bed in bedNames")
                    .Set("bedNames", ToStrList(L(pg, "geologyBedNameList")))
                    .Set("decayDistance", D(pg, "decayDistance"))
                    .Set("activePointIndex", I(pg, "activeGMPIndex"))
                    .Set("undoHistoryEntriesDropped", Count(L(pg, "undoHistoryList")))
                    .Set("points", pts));
            }
        }
        m.Set("geologyModel", gm);
        m.Set("gmpFile", Link(Str(root, "GMPFileName")));

        // ---- resistivity modeling settings
        m.Set("resistivityModel", new JObj()
            .Set("toolIndex", I(root, "ResTool"))
            .Set("rtCurveName", Str(root, "RtCurveName"))
            .Set("evaluateCurves", F(root, "EvaluateCurves"))
            .Set("calcTolerance", I(root, "ResCalcTolerance"))
            .Set("step", D(root, "resStep"))
            .Set("inclinationTolerance", D(root, "incTolerance"))
            .Set("diTolerance", D(root, "diTolerance"))
            .Set("useFloatingRvd", B(root, "useFloatingRvd"))
            .Set("rvdLocation", D(root, "RvdLocation"))
            .Set("decayDistance", D(root, "decayDistance")));

        // ---- present but not yet mapped (shape unconfirmed): generic dump, flagged
        JObj unm = new JObj();
        IList dp = L(root, "dipPickList"); unm.Set("dipPicks", Generic(dp, 5));
        IList zd = L(root, "zonesDef"); unm.Set("targetZones", Generic(zd, 5));
        unm.Set("logStatistics", Generic(F(root, "logStatDef"), 4));
        unm.Set("modeledImage", Generic(F(root, "modeledImage"), 3));
        unm.Set("holeSizeImage", Generic(F(root, "HoleSizeImageDI"), 3));
        m.Set("unmappedPzsFields", unm);
        return m;
    }

    static string PzsMigrateSlug(string s) { string x = Regex.Replace(s, @"[^A-Za-z0-9._-]+", "_").Trim('_'); return x.Length == 0 ? "_" : x; }

    static List<string> ToStrList(IList l)
    {
        List<string> r = new List<string>();
        if (l != null) foreach (object o in l) r.Add(o == null ? null : o.ToString());
        return r;
    }

    static JObj BedCommon(object b)
    {
        return new JObj()
            .Set("name", Str(b, "bedName") ?? Name(b))
            .Set("bedType", Str(b, "BedType"))
            .Set("geoType", Str(b, "BedGeoType"))
            .Set("sourceWellMd", D(b, "BedSrcWellMD"))
            .Set("rvd", D(b, "BedRVD"))
            .Set("depthIndex", D(b, "BedDepthIndex"));
    }

    static JObj WellSettings(object w)
    {
        if (w == null) return null;
        object ti = F(w, "tieIn");
        JObj info = new JObj();
        object wi = F(w, "wellInfo");
        if (wi != null)
            foreach (string k in new string[] { "Name", "COMP", "WELL", "FLD", "LOC", "PRVC", "CNTY", "STAT", "CTRY", "SRVC", "DATE", "UWI", "API" })
            { string v = Str(wi, k); if (v != null) info.Set(k, v); }
        return new JObj()
            .Set("pzsName", Name(w))
            .Set("file", Link(Str(w, "fileFullName")))
            .Set("surveyFile", Link(Str(w, "surveyFileFullName")))
            .Set("location", Loc(F(w, "wellLocation")))
            .Set("tieIn", ti == null ? null : new JObj().Set("north", D(ti, "NS")).Set("east", D(ti, "EW")).Set("tvd", D(ti, "TVD")))
            .Set("zPosTieIn", D(w, "zPosTiein"))
            .Set("extrapolation", new JObj().Set("trend", D(w, "extrapTrend")).Set("straight", D(w, "extrapStraight")))
            .Set("tstCurveName", Str(w, "tstCurveName"))
            .Set("wellInfo", info);
    }

    // PZS objects have finalizers calling into mixed-mode C++/CLI; suppress for everything reachable
    // (read-only use) to avoid a crash at process exit.
    public static void SuppressAllFinalizers()
    {
        Dictionary<object, bool> seen = new Dictionary<object, bool>(new RefEq());
        Stack<object> st = new Stack<object>();
        foreach (object r in Roots) st.Push(r);
        while (st.Count > 0)
        {
            object o = st.Pop();
            if (o == null || seen.ContainsKey(o)) continue;
            Type t = o.GetType();
            if (t.IsPrimitive || o is string || t.IsEnum) continue;
            if (!t.IsValueType) { seen[o] = true; GC.SuppressFinalize(o); }
            if (t.IsArray)
            {
                if (t.GetElementType().IsPrimitive) continue;
                foreach (object x in (Array)o) if (x != null) st.Push(x);
                continue;
            }
            for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
                foreach (FieldInfo f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType.IsPrimitive) continue;
                    object v; try { v = f.GetValue(o); } catch { continue; }
                    if (v != null) st.Push(v);
                }
        }
    }

    class RefEq : IEqualityComparer<object>
    {
        public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
        public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
    }
}
