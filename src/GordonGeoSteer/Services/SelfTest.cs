using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GordonGeoSteer.ViewModels;

namespace GordonGeoSteer.Services;

/// <summary>
/// --selftest: drives the real window through the same code paths as a user (import or open, then
/// select every tree node so every detail view is built and rendered), and records evidence:
///   report\selftest.log            progress + any per-node errors
///   report\viewmodel-dump.txt      DebugDump of everything the view-models hold
///   report\ui_*.png                WPF render of the window for key views
///   report\screen_*.png            real desktop screen capture of the window (best effort, GDI BitBlt)
///   report\ui_steer_*.png          steering-view renders (see SteeringSelfTest); report\engine-validation.txt
/// </summary>
public static class SelfTest
{
    static string ReportDir(Dictionary<string, string> a) =>
        a.TryGetValue("report", out var r) && r.Length > 0 ? r : Path.Combine(Path.GetTempPath(), "GordonGeoSteer", "selftest");

    public static void LogFatal(Dictionary<string, string> args, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(ReportDir(args));
            File.AppendAllText(Path.Combine(ReportDir(args), "selftest.log"), "FATAL (unhandled): " + ex + Environment.NewLine);
        }
        catch { }
    }

    public static async Task<int> RunAsync(MainWindow win, Dictionary<string, string> args)
    {
        string dir = ReportDir(args);
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        void Log(string s) { log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); File.WriteAllText(Path.Combine(dir, "selftest.log"), log.ToString()); }
        var vm = win.VM;
        int errors = 0;

        try
        {
            win.WindowState = WindowState.Normal;
            win.Width = 1400; win.Height = 860;
            win.Left = 20; win.Top = 20;
            win.Topmost = true; // so the screen capture shows this window, not whatever is in front

            // The steering workspace goes into the report folder, never the user's own workspace store.
            string wsPath = Path.Combine(dir, "selftest-workspace.gws.json");
            if (File.Exists(wsPath)) File.Delete(wsPath);
            vm.WorkspacePathOverride = wsPath;

            bool loaded;
            if (args.TryGetValue("import", out var src) && src.Length > 0)
            {
                string outDir = args.TryGetValue("out", out var o) && o.Length > 0 ? o : ImporterRunner.DefaultOutputDir(src);
                Log($"import: {src} -> {outDir}");
                loaded = await vm.ImportAndLoadAsync(src, outDir);
                Log($"importer exit code: {vm.LastImport?.ExitCode} ({vm.LastImport?.ExitMeaning}); importer = {vm.LastImport?.ImporterPath}");
            }
            else if (args.TryGetValue("open", out var json) && json.Length > 0)
            {
                Log("open: " + json);
                loaded = vm.LoadProjectJson(json);
            }
            else { Log("ERROR: --selftest needs --import <pzsFolder> or --open <project.json>"); return 1; }

            Log("status: " + vm.Status);
            Log("banner: " + (vm.Banner ?? "(none)"));
            if (!loaded) { Log("ERROR: project did not load"); File.WriteAllText(Path.Combine(dir, "importer.log"), vm.ImportLog); return 1; }

            // Visit every node exactly like a user clicking through the tree.
            var nodes = vm.AllNodes().ToList();
            Log($"tree nodes: {nodes.Count}");
            foreach (var n in nodes)
            {
                await win.SelectAndRenderAsync(n);
                if (n.Detail is ErrorViewModel ev) { errors++; Log($"ERROR building view for '{n.Header}': {ev.Message}"); }
                if (n.Detail is CurveViewModel cv && cv.LoadError != null) { errors++; Log($"ERROR curve '{n.Header}': {cv.LoadError}"); }
            }
            Log($"visited {nodes.Count} nodes, {errors} view error(s)");

            DebugDump.Write(vm, Path.Combine(dir, "viewmodel-dump.txt"));
            Log("wrote viewmodel-dump.txt");

            // Screenshots of representative views.
            var shots = new (string file, Func<TreeNode, bool> pick)[]
            {
                ("01_project", n => n.Kind == "project"),
                ("02_import_report", n => n.Kind == "report"),
                ("03_well_realtime", n => n.Kind == "well" && n.Header.Contains("Realtime")),
                ("04_survey_realtime", n => n.Kind == "survey" && Parent(vm, n)?.Header.Contains("Realtime") == true),
                ("05_curve_RDAH_M", n => n.Kind == "curve" && n.Header.StartsWith("RDAH_M ")),
                ("06_curve_GR_pilot", n => n.Kind == "curve" && n.Header.StartsWith("GR ") && Parent(vm, Parent(vm, n)!)?.Header.Contains("01 Offset Data/P2_Pilot") == true),
                ("07_targets", n => n.Kind == "targets"),
                ("08_geology", n => n.Kind == "geology"),
                ("09_inventory", n => n.Kind == "inventory"),
            };
            foreach (var (file, pick) in shots)
            {
                var n = nodes.FirstOrDefault(pick);
                if (n == null) { Log($"screenshot {file}: no matching node"); continue; }
                await win.SelectAndRenderAsync(n);
                await Task.Delay(150);
                win.SaveWindowPng(Path.Combine(dir, $"ui_{file}.png"));
                string screenMsg = TryScreenCapture(win, Path.Combine(dir, $"screen_{file}.png"));
                Log($"screenshot {file}: '{n.Header}' (screen capture: {screenMsg})");
            }

            // Steering view (v2): renders, zoom checks, workspace round trip, engine validation.
            Log("steering view self-test:");
            errors += await SteeringSelfTest.RunAsync(win, dir, Log);
        }
        catch (Exception ex)
        {
            errors++;
            Log("FATAL: " + ex);
        }
        Log(errors == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({errors} error(s))");
        return errors == 0 ? 0 : 1;
    }

    static TreeNode? Parent(MainViewModel vm, TreeNode child) =>
        vm.AllNodes().FirstOrDefault(p => p.Children.Contains(child));

    // ---------------- real screen capture of the window's on-screen rectangle (GDI)
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    static string TryScreenCapture(Window win, string path)
    {
        try
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            if (!GetWindowRect(hwnd, out var r)) return "GetWindowRect failed";
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return "empty window rect";
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h);
            IntPtr old = SelectObject(mem, bmp);
            bool ok = BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);
            try
            {
                if (!ok) return "BitBlt failed (no interactive desktop?)";
                var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(src));
                using var fs = File.Create(path);
                enc.Save(fs);
                return $"ok {w}x{h}";
            }
            finally { DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
        }
        catch (Exception ex) { return "failed: " + ex.Message; }
    }
}
