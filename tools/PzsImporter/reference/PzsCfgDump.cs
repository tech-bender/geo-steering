// PzsCfgDump.cs - read-only deserializer/dumper for PZS .pzscfg (NRBF BinaryFormatter) files.
// Gordon Technologies PZS replacement project - data-format/migration research only.
// Build (x86, .NET 3.5 compiler):
//   C:\Windows\Microsoft.NET\Framework\v3.5\csc.exe /platform:x86 /target:exe /out:PzsCfgDump.exe
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll PzsCfgDump.cs
// Run:  PzsCfgDump.exe <file.pzscfg> <outTree.txt> [maxDepth=8] [maxListItems=6]
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

class PzsCfgDump
{
    const string InstallDir = @"C:\Program Files (x86)\Schlumberger\PZS 5.2.0";
    // Licensing assemblies are out of scope - never load them.
    static readonly string[] Excluded = { "SKCLNET", "PFLICENSE", "LICENINF", "INSTALLPZSLICENSE", "UNINS000" };
    // Assemblies that the binder will allow types from (short names, upper-case).
    static readonly HashSet<string> AllowedSystemAsm = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "mscorlib", "System", "System.Drawing" };
    static HashSet<string> PzsAsmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static List<string> LoadLog = new List<string>();
    static List<string> BindLog = new List<string>();
    static HashSet<string> BindSeen = new HashSet<string>();
    static List<string> Rejected = new List<string>();

    static bool IsExcluded(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
        foreach (string e in Excluded) if (n == e) return true;
        return false;
    }

    static void LoadDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        List<string> files = new List<string>();
        files.AddRange(Directory.GetFiles(dir, "*.dll"));
        files.AddRange(Directory.GetFiles(dir, "*.exe"));
        foreach (string f in files)
        {
            if (IsExcluded(f)) { LoadLog.Add("SKIP (licensing, out of scope): " + f); continue; }
            try
            {
                AssemblyName.GetAssemblyName(f); // throws BadImageFormat for native DLLs without loading them
                Assembly a = Assembly.LoadFrom(f);
                PzsAsmNames.Add(a.GetName().Name);
                LoadLog.Add("OK   " + a.FullName + "  <- " + f);
            }
            catch (Exception ex) { LoadLog.Add("FAIL " + f + " : " + ex.GetType().Name + ": " + ex.Message.Trim()); }
        }
    }

    static Assembly FindLoaded(string shortName)
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            if (string.Equals(a.GetName().Name, shortName, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }

    static Assembly OnResolve(object sender, ResolveEventArgs args)
    {
        string sn = new AssemblyName(args.Name).Name;
        if (Array.IndexOf(Excluded, sn.ToUpperInvariant()) >= 0) { Rejected.Add("AssemblyResolve refused (licensing): " + args.Name); return null; }
        return FindLoaded(sn); // version-agnostic: 3.0.3.x -> 5.2.0.0
    }

    class VersionAgnosticBinder : SerializationBinder
    {
        public override Type BindToType(string assemblyName, string typeName)
        {
            string sn = new AssemblyName(assemblyName).Name;
            string key = sn + " :: " + typeName;
            if (BindSeen.Add(key)) BindLog.Add(key);
            bool allowed = PzsAsmNames.Contains(sn) || AllowedSystemAsm.Contains(sn);
            // generic args may name other assemblies; check them all
            foreach (string inner in InnerAssemblies(typeName))
                if (!(PzsAsmNames.Contains(inner) || AllowedSystemAsm.Contains(inner))) allowed = false;
            if (!allowed)
            {
                Rejected.Add(key);
                throw new SerializationException("Refusing type outside PZS/allowed assemblies: " + key);
            }
            Assembly a = FindLoaded(sn);
            if (a == null) throw new SerializationException("Assembly not loaded: " + sn);
            Type t = a.GetType(typeName, false);
            if (t == null) t = Type.GetType(typeName + ", " + a.FullName, false);
            if (t == null) throw new SerializationException("Type not found: " + key);
            return t;
        }
    }

    // Extract assembly short names from generic type args: "...[[T, Asm, Version=...]]"
    static IEnumerable<string> InnerAssemblies(string typeName)
    {
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(typeName, @"\[([^\[\],]+),\s*([^,\]]+)"))
            yield return m.Groups[2].Value.Trim();
    }

    // ---------------- graph walker ----------------
    static StreamWriter Out;
    static int MaxDepth = 8;
    static Dictionary<object, int> Visited = new Dictionary<object, int>(new RefEq());
    static int NextId = 1;
    static Dictionary<string, int> TypeCounts = new Dictionary<string, int>();
    static int MaxListShow = 6;

    class RefEq : IEqualityComparer<object>
    {
        public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
        public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
    }

    static bool IsSimple(Type t)
    {
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)
            || t == typeof(TimeSpan) || t == typeof(Guid);
    }

    static string Fmt(object v)
    {
        if (v == null) return "null";
        if (v is string) { string s = (string)v; if (s.Length > 200) s = s.Substring(0, 200) + "...(" + s.Length + " chars)"; return "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n") + "\""; }
        if (v is double) return ((double)v).ToString("R");
        if (v is float) return ((float)v).ToString("R");
        if (v is DateTime) return ((DateTime)v).ToString("yyyy-MM-dd HH:mm:ss");
        if (v is Enum) return v.GetType().Name + "." + v.ToString();
        if (v is System.Drawing.Color) { System.Drawing.Color c = (System.Drawing.Color)v; return "Color(" + (c.IsNamedColor ? c.Name : "#" + c.ToArgb().ToString("X8")) + ")"; }
        if (v is System.Drawing.Font) { System.Drawing.Font f = (System.Drawing.Font)v; return "Font(" + f.Name + "," + f.Size + "," + f.Style + ")"; }
        return v.ToString();
    }

    static string TName(Type t)
    {
        if (!t.IsGenericType) return t.FullName ?? t.Name;
        StringBuilder sb = new StringBuilder(t.Namespace + "." + t.Name.Split('`')[0] + "<");
        Type[] ga = t.GetGenericArguments();
        for (int i = 0; i < ga.Length; i++) { if (i > 0) sb.Append(","); sb.Append(TName(ga[i])); }
        return sb.Append(">").ToString();
    }

    static IEnumerable<FieldInfo> AllFields(Type t)
    {
        List<FieldInfo> res = new List<FieldInfo>();
        Stack<Type> chain = new Stack<Type>();
        for (Type x = t; x != null && x != typeof(object); x = x.BaseType) chain.Push(x);
        foreach (Type x in chain)
            foreach (FieldInfo f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                res.Add(f);
        return res;
    }

    static void W(int depth, string s) { Out.WriteLine(new string(' ', depth * 2) + s); }

    static void Dump(string label, object v, int depth)
    {
        if (v == null) { W(depth, label + " = null"); return; }
        Type t = v.GetType();
        string tn = TName(t);
        if (IsSimple(t) || v is System.Drawing.Color || v is System.Drawing.Font) { W(depth, label + " : " + t.Name + " = " + Fmt(v)); return; }
        if (t.IsValueType && (v is System.Drawing.PointF || v is System.Drawing.Point || v is System.Drawing.Size || v is System.Drawing.SizeF || v is System.Drawing.Rectangle || v is System.Drawing.RectangleF))
        { W(depth, label + " : " + t.Name + " = " + v); return; }

        if (!t.IsValueType)
        {
            int id;
            if (Visited.TryGetValue(v, out id)) { W(depth, label + " : " + tn + " -> (see #" + id + ")"); return; }
            id = NextId++; Visited[v] = id;
            int cnt; TypeCounts.TryGetValue(tn, out cnt); TypeCounts[tn] = cnt + 1;
            label = label + " #" + id;
        }
        if (v is System.Drawing.Image || v is System.Windows.Forms.Control || v is System.Drawing.Brush || v is System.Drawing.Pen)
        { W(depth, label + " : " + tn + " [UI/graphics object - not expanded]"); return; }

        if (t.IsArray)
        {
            Array arr = (Array)v;
            Type et = t.GetElementType();
            if (et.IsPrimitive)
            {
                StringBuilder sb = new StringBuilder();
                int n = Math.Min(arr.Length, 12);
                for (int i = 0; i < n; i++) { if (i > 0) sb.Append(", "); sb.Append(Fmt(arr.GetValue(i))); }
                if (arr.Length > n) sb.Append(", ... last=" + Fmt(arr.GetValue(arr.Length - 1)));
                W(depth, label + " : " + TName(et) + "[" + arr.Length + "] = [" + sb + "]");
                return;
            }
            W(depth, label + " : " + TName(et) + "[" + arr.Length + "]");
            if (depth >= MaxDepth) return;
            int k = 0;
            foreach (object o in arr) { if (k >= MaxListShow) { W(depth + 1, "... (" + (arr.Length - MaxListShow) + " more)"); break; } Dump("[" + k + "]", o, depth + 1); k++; }
            if (arr.Length > MaxListShow) Dump("[last=" + (arr.Length - 1) + "]", arr.GetValue(arr.Length - 1), depth + 1);
            return;
        }
        if (v is IDictionary)
        {
            IDictionary d = (IDictionary)v;
            W(depth, label + " : " + tn + " (dict, Count=" + d.Count + ")");
            if (depth >= MaxDepth) return;
            int k = 0;
            foreach (DictionaryEntry e in d) { if (k >= MaxListShow * 2) { W(depth + 1, "... (" + (d.Count - k) + " more)"); break; } Dump("[" + Fmt(e.Key) + "]", e.Value, depth + 1); k++; }
            return;
        }
        if (v is IList)
        {
            IList l = (IList)v;
            W(depth, label + " : " + tn + " (list, Count=" + l.Count + ")");
            if (depth >= MaxDepth) return;
            // compact numeric lists
            for (int i = 0; i < l.Count && i < MaxListShow; i++) Dump("[" + i + "]", l[i], depth + 1);
            if (l.Count > MaxListShow) { W(depth + 1, "... (" + (l.Count - MaxListShow - 1) + " more)"); Dump("[last=" + (l.Count - 1) + "]", l[l.Count - 1], depth + 1); }
            return;
        }

        W(depth, label + " : " + tn);
        if (depth >= MaxDepth) { W(depth + 1, "(max depth reached)"); return; }
        foreach (FieldInfo f in AllFields(t))
        {
            object fv;
            try { fv = f.GetValue(v); } catch (Exception ex) { W(depth + 1, f.Name + " : <error " + ex.GetType().Name + ">"); continue; }
            string fname = f.Name;
            if (f.DeclaringType != t) fname = f.DeclaringType.Name + "." + fname;
            Dump(fname + " {" + TName(f.FieldType) + "}", fv, depth + 1);
        }
    }

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: PzsCfgDump <file.pzscfg> <out.txt> [maxDepth]"); return 2; }
        if (args.Length > 2) MaxDepth = int.Parse(args[2]);
        if (args.Length > 3) MaxListShow = int.Parse(args[3]);
        Console.WriteLine("Process is " + (IntPtr.Size == 4 ? "32-bit" : "64-bit") + ", CLR " + Environment.Version);
        AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        LoadDir(InstallDir);
        LoadDir(Path.Combine(InstallDir, "VDC"));

        string outPath = args[1];
        string logPath = Path.ChangeExtension(outPath, ".log.txt");
        object root = null;
        Exception failure = null;
        using (FileStream fs = new FileStream(args[0], FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            BinaryFormatter bf = new BinaryFormatter();
            bf.Binder = new VersionAgnosticBinder();
            bf.AssemblyFormat = System.Runtime.Serialization.Formatters.FormatterAssemblyStyle.Simple;
            try { root = bf.Deserialize(fs); }
            catch (Exception ex) { failure = ex; }
            Console.WriteLine("Stream position after deserialize: " + fs.Position + " / " + fs.Length);
        }

        using (StreamWriter lw = new StreamWriter(logPath, false, Encoding.UTF8))
        {
            lw.WriteLine("=== Assembly load log ==="); foreach (string s in LoadLog) lw.WriteLine(s);
            lw.WriteLine(); lw.WriteLine("=== Types requested by stream (asm :: type) ==="); foreach (string s in BindLog) lw.WriteLine(s);
            lw.WriteLine(); lw.WriteLine("=== Rejected ==="); foreach (string s in Rejected) lw.WriteLine(s);
            if (failure != null)
            {
                lw.WriteLine(); lw.WriteLine("=== FAILURE ===");
                for (Exception e = failure; e != null; e = e.InnerException) lw.WriteLine(e.ToString() + "\n---- inner ----");
            }
        }
        Console.WriteLine("Types bound: " + BindLog.Count + ", rejected: " + Rejected.Count);
        if (failure != null)
        {
            Console.WriteLine("DESERIALIZE FAILED:");
            for (Exception e = failure; e != null; e = e.InnerException) Console.WriteLine("  " + e.GetType().FullName + ": " + e.Message);
            Console.WriteLine("See " + logPath);
            return 1;
        }

        using (Out = new StreamWriter(outPath, false, Encoding.UTF8))
        {
            Out.WriteLine("Source: " + args[0]);
            Out.WriteLine("Root type: " + TName(root.GetType()) + "  (assembly " + root.GetType().Assembly.GetName().Name + ")");
            Out.WriteLine();
            Dump("ROOT", root, 0);
            Out.WriteLine(); Out.WriteLine("=== Object counts by type ===");
            List<string> keys = new List<string>(TypeCounts.Keys); keys.Sort();
            foreach (string k in keys) Out.WriteLine(TypeCounts[k].ToString().PadLeft(7) + "  " + k);
        }
        // PZS objects (e.g. PFNSProject.PFWell) have finalizers that call into mixed-mode C++/CLI
        // modules; on process exit that throws a TypeInitializationException. We only read the
        // graph, so suppress all finalizers on visited objects.
        foreach (object o in Visited.Keys) GC.SuppressFinalize(o);
        Console.WriteLine("OK. Root type: " + TName(root.GetType()) + "; objects visited: " + (NextId - 1));
        Console.WriteLine("Tree written to " + outPath);
        return 0;
    }
}
