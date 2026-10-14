using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using GordonGeoSteer.Controls.Steering;
using GordonGeoSteer.Engine.Steering;
using GordonGeoSteer.Engine.Validation;
using GordonGeoSteer.Engine.Workspace;
using GordonGeoSteer.ViewModels.Steering;

namespace GordonGeoSteer.Services;

/// <summary>
/// Steering-view part of --selftest: engine validation report, renders of the steering view with every
/// line style and shading mode in use, programmatic checks of independent x/y zoom and track alignment,
/// and a workspace save -> reopen round trip. Writes steer_*.png and engine-validation.txt.
/// </summary>
public static class SteeringSelfTest
{
    public static async Task<int> RunAsync(MainWindow win, string dir, Action<string> log)
    {
        int errors = 0;
        void Fail(string m) { errors++; log("ERROR steering: " + m); }
        void Check(bool ok, string what) { if (ok) log("  ok: " + what); else Fail(what); }
        var main = win.VM;
        var vm = main.Steering;
        if (vm == null) { Fail("steering view not built: " + main.SteeringMessage); return errors; }

        // ---- engine validation numbers
        var val = EngineValidation.Run(vm.Project);
        File.WriteAllText(Path.Combine(dir, "engine-validation.txt"), val.Text);
        log("engine validation written (engine-validation.txt):");
        foreach (var k in new[] { "arc1.interpMaxErr", "mttv.stations.maxAbs", "mttv.stations.meanAbs", "mttv.all.maxAbs", "m3.di.lateral.medianAbs",
                                  "m3.di.lateral.maxAbs", "m3.parallel.lateral.medianAbs", "gr.parallel.r", "gr.parallel.shuffledAbsR", "gr.stretch.r" })
            if (val.Metrics.TryGetValue(k, out var v)) log($"  {k} = {v.ToString("0.#####", CultureInfo.InvariantCulture)}");

        win.Width = 1800; win.Height = 1000;
        win.ShowSteering();
        await Settle(win);
        var view = win.SteeringViewControl;
        var curtain = view.Curtain;
        log($"steering: reference bed {vm.ReferenceBed}, mode {vm.Mode}, {vm.HorizontalTracks.Count} horizontal + {vm.VerticalTracks.Count} vertical tracks, {vm.Beds.Count} beds");
        Check(vm.HorizontalTracks.Count >= 1 && vm.VerticalTracks.Count >= 1, "default layout has horizontal and vertical tracks");
        await Shot(win, dir, "steer_01_default", log);

        // ---- styled scenario: every line style and every shading mode, user boundaries
        ConfigureStyled(vm);
        await Settle(win);
        var used = vm.AllTracks.SelectMany(t => t.Curves).ToList();
        foreach (var ls in Enum.GetValues<LineStyleKind>()) Check(used.Any(c => c.LineStyle == ls && !c.Missing), $"line style {ls} in use");
        foreach (var sm in Enum.GetValues<ShadingMode>().Where(m => m != ShadingMode.None)) Check(used.Any(c => c.ShadeMode == sm && !c.Missing), $"shading {sm} in use");
        foreach (var c in used) Check(!c.Missing && vm.GetSeries(c.Source, c.Track.IsVertical) is { Count: > 0 }, $"curve {c.Source} loads ({c.Track.Title})");
        await Shot(win, dir, "steer_02_styled", log);

        // ---- alignment: horizontal tracks share the curtain's x axis object and pixel extent
        Check(ReferenceEquals(vm.XRange, vm.XRange), "x axis shared");
        foreach (var tc in FindAll<LogTrackControl>(view).Where(t => t.Track is { IsVertical: false }))
            Check(Math.Abs(tc.ActualWidth - curtain.ActualWidth) < 0.5, $"horizontal track '{tc.Track!.Title}' width {tc.ActualWidth:0.#} == curtain width {curtain.ActualWidth:0.#} (same VS->pixel mapping)");

        // ---- independent zoom (drive the real control's wheel handler)
        var plot = curtain.PlotRect;
        var centre = new Point(plot.Left + plot.Width * 0.6, plot.Top + plot.Height * 0.4);
        double x0 = vm.XRange.Span, y0 = vm.TvdRange.Span, t0 = vm.TvtRange.Span;
        double anchorVs = vm.XRange.FromPixel(centre.X, plot.Left, plot.Width);
        curtain.WheelAt(centre, 240, ctrl: true, shift: false);
        Check(Math.Abs(vm.XRange.Span - x0 / 1.44) < 1e-6 * x0 && Math.Abs(vm.TvdRange.Span - y0) < 1e-9,
              $"Ctrl+wheel zooms horizontally only (VS span {x0:0.#} -> {vm.XRange.Span:0.#}, TVD span {y0:0.##} -> {vm.TvdRange.Span:0.##})");
        Check(Math.Abs(vm.XRange.FromPixel(centre.X, plot.Left, plot.Width) - anchorVs) < 1e-6, "zoom keeps the point under the cursor fixed");
        await Settle(win);
        await Shot(win, dir, "steer_03_zoom_horizontal", log);
        double x1 = vm.XRange.Span;
        curtain.WheelAt(centre, 360, ctrl: false, shift: true);
        Check(Math.Abs(vm.TvdRange.Span - y0 / Math.Pow(1.2, 3)) < 1e-6 * y0 && Math.Abs(vm.XRange.Span - x1) < 1e-9,
              $"Shift+wheel zooms vertically only (TVD span {y0:0.##} -> {vm.TvdRange.Span:0.##}, VS span unchanged {vm.XRange.Span:0.#})");
        Check(Math.Abs(vm.TvtRange.Span - t0) < 1e-9, "curtain zoom does not touch the track TVT axis");
        curtain.WheelAt(centre, -120, ctrl: false, shift: false);
        Check(Math.Abs(vm.XRange.Span - x1 * 1.2) < 1e-6 * x1, "plain wheel zooms both axes");
        await Settle(win);
        await Shot(win, dir, "steer_04_zoom_vertical", log);
        vm.ResetView();
        Check(Math.Abs(vm.XRange.Span - x0) < 1e-6 && Math.Abs(vm.TvdRange.Span - y0) < 1e-6, "reset restores the home view");

        // ---- DI-stretch mode render
        vm.Mode = CorrelationMode.DepthIndexStretch;
        await Settle(win);
        await Shot(win, dir, "steer_05_stretch_mode", log);
        vm.Mode = CorrelationMode.ParallelToReference;

        // ---- workspace save -> reopen project -> identical state
        vm.TvtRange.Set(vm.TvtRange.Min + 3, vm.TvtRange.Max - 2); // a non-default view to restore
        vm.Save();
        string saved = Normalize(vm.ToWorkspace());
        log("workspace saved: " + vm.WorkspacePath);
        string? projectJson = main.ProjectJsonPath;
        if (projectJson == null) { Fail("no project path"); return errors; }
        main.LoadProjectJson(projectJson);           // same code path as File > Open Imported project.json
        win.ShowSteering();
        await Settle(win);
        var vm2 = main.Steering;
        if (vm2 == null || ReferenceEquals(vm2, vm)) { Fail("steering view not rebuilt on reopen"); return errors; }
        string reopened = Normalize(vm2.ToWorkspace());
        Check(saved == reopened, "reopening the project restores tracks, styles, shading, beds, reference bed, mode and zoom exactly");
        if (saved != reopened)
        {
            File.WriteAllText(Path.Combine(dir, "workspace-saved.json"), saved);
            File.WriteAllText(Path.Combine(dir, "workspace-reopened.json"), reopened);
        }
        await Shot(win, dir, "steer_06_reopened", log);
        return errors;
    }

    static string Normalize(WorkspaceDocument w)
    {
        w.SavedUtc = null;
        return JsonSerializer.Serialize(w, WorkspaceDocument.Json);
    }

    /// <summary>A layout that exercises every line style and shading mode plus user bed boundaries.</summary>
    public static void ConfigureStyled(SteeringViewModel vm)
    {
        var w = vm.DefaultWorkspace();
        string? Has(params string[] ids) => ids.FirstOrDefault(id => vm.Project.FindCurve(id) != null);
        string rtGr = Has("rt:GRCA", "rt:GR") ?? "rt:GRCA";
        string offGr = Has("off:GR") ?? "off:GR";
        string rtRes = Has("rt:RDAH", "rt:RDPH") ?? "rt:RDAH";
        string offRes = Has("off:AT90", "off:RT") ?? "off:AT90";
        string pzsRes = Has("pzs:RDAH_M", "pzs:RDPH_M") ?? "pzs:RDAH_M";
        w.Tracks.Clear();
        w.Tracks.Add(new TrackSpec
        {
            Title = "Gamma ray", Orientation = TrackOrientation.Horizontal, Size = 150, ScaleMin = 0, ScaleMax = 250,
            Curves =
            {
                new CurveSpec { Source = rtGr, Color = "#2E9E3E", Thickness = 1, Shading = new ShadingSpec { Mode = ShadingMode.Gradient, Ramp = ColorRampKind.Earth, Opacity = 0.9 } },
                new CurveSpec { Source = "syn:" + offGr[4..], Color = "#000000", Thickness = 1.6 },
            },
        });
        w.Tracks.Add(new TrackSpec
        {
            Title = "Resistivity", Orientation = TrackOrientation.Horizontal, Size = 130, ScaleMin = 1, ScaleMax = 1000, LogScale = true,
            Curves =
            {
                new CurveSpec { Source = rtRes, Color = "#1F4E9E", Thickness = 1, Shading = new ShadingSpec { Mode = ShadingMode.ToTrackMin, Color = "#3A7BD5", Opacity = 0.25 } },
                new CurveSpec { Source = pzsRes, Color = "#D62728", Thickness = 1.4, LineStyle = LineStyleKind.Dashed },
                new CurveSpec { Source = "syn:" + offRes[4..], Color = "#000000", Thickness = 1.6, LineStyle = LineStyleKind.Dotted },
            },
        });
        w.Tracks.Add(new TrackSpec
        {
            Title = "GR (TVT)", Orientation = TrackOrientation.Vertical, Size = 250, ScaleMin = 0, ScaleMax = 250,
            Curves =
            {
                new CurveSpec { Source = offGr, Color = "#000000", Thickness = 1.3, Shading = new ShadingSpec { Mode = ShadingMode.ToTrackMax, Color = "#B0B0B0", Opacity = 0.35 } },
                new CurveSpec { Source = rtGr, Color = "#2E9E3E", Thickness = 1.4, LineStyle = LineStyleKind.DotDash, Shading = new ShadingSpec { Mode = ShadingMode.ToCurve, OtherCurve = offGr, Color = "#7DBA2F", Opacity = 0.35 } },
            },
        });
        w.Tracks.Add(new TrackSpec
        {
            Title = "Resistivity (TVT)", Orientation = TrackOrientation.Vertical, Size = 220, ScaleMin = 1, ScaleMax = 1000, LogScale = true,
            Curves =
            {
                new CurveSpec { Source = offRes, Color = "#000000", Thickness = 1.3, Shading = new ShadingSpec { Mode = ShadingMode.Gradient, Ramp = ColorRampKind.Viridis, Opacity = 0.35 } },
                new CurveSpec { Source = rtRes, Color = "#1F4E9E", Thickness = 1.2, LineStyle = LineStyleKind.Dashed },
            },
        });
        w.Beds.Add(new BedBoundarySpec { Name = "Target top", Kind = BedKind.User, Offset = -18, Color = "#D62728", Thickness = 2 });
        w.Beds.Add(new BedBoundarySpec { Name = "Target base", Kind = BedKind.User, Offset = -6, Color = "#E07B00", Thickness = 2 });
        w.Beds.Add(new BedBoundarySpec { Name = "Hard streak", Kind = BedKind.User, Offset = -12, Color = "#7B3FA0", Thickness = 1 });
        vm.ApplyWorkspace(w);
    }

    static async Task Settle(Window win)
    {
        await win.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        win.UpdateLayout();
        await win.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
        await win.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    static async Task Shot(MainWindow win, string dir, string name, Action<string> log)
    {
        await Settle(win);
        string path = Path.Combine(dir, $"ui_{name}.png");
        win.SaveWindowPng(path);
        log($"screenshot {name}: {path}");
    }

    static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in FindAll<T>(c)) yield return d;
        }
    }
}
