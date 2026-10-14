using System.Windows;
using System.Windows.Threading;
using GordonGeoSteer.Services;

namespace GordonGeoSteer;

/// <summary>
/// Command line:
///   GordonGeoSteer.exe                                       normal interactive start
///   GordonGeoSteer.exe --open &lt;project.json&gt;                 open a previous import
///   GordonGeoSteer.exe --import &lt;pzsFolder&gt; [--out &lt;dir&gt;]    import a PZS folder on start
///   GordonGeoSteer.exe --selftest (--import &lt;pzsFolder&gt; --out &lt;dir&gt; | --open &lt;project.json&gt;) --report &lt;dir&gt;
///       Non-interactive end-to-end check: runs the same import/load path as the UI, selects every tree
///       node (building every view), writes a view-model dump + screenshots to the report dir, then exits
///       0 = OK, 1 = load/display failure.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = ParseArgs(e.Args);
        bool selfTest = args.ContainsKey("selftest");

        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            if (selfTest)
            {
                SelfTest.LogFatal(args, ex.Exception);
                Shutdown(1);
                return;
            }
            MessageBox.Show(ex.Exception.ToString(), "GordonGeoSteer - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        var win = new MainWindow();
        MainWindow = win;
        win.Show();

        win.Dispatcher.BeginInvoke(async () =>
        {
            if (selfTest)
            {
                int code = await SelfTest.RunAsync(win, args);
                Shutdown(code);
                return;
            }
            if (args.TryGetValue("open", out var json)) win.VM.LoadProjectJson(json);
            else if (args.TryGetValue("import", out var src))
                await win.VM.ImportAndLoadAsync(src, args.TryGetValue("out", out var o) && o.Length > 0 ? o : null);
        }, DispatcherPriority.ApplicationIdle);
    }

    static Dictionary<string, string> ParseArgs(string[] a)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) continue;
            string k = a[i][2..];
            if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) d[k] = a[++i];
            else d[k] = "";
        }
        return d;
    }
}
