using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using GordonGeoSteer.Engine.Display;
using GordonGeoSteer.Engine.Steering;
using GordonGeoSteer.Engine.Workspace;

namespace GordonGeoSteer.ViewModels.Steering;

/// <summary>
/// State of the steering view: the engine model, the tracks and their curves, bed boundaries, the three
/// zoomable axes (curtain vertical section shared with the horizontal tracks, curtain TVD, track TVT)
/// and the workspace file. Raises <see cref="Redraw"/> whenever anything that affects drawing changes.
/// </summary>
public sealed class SteeringViewModel : ObservableObject
{
    public SteeringProject Project { get; }
    public SteeringModel Model => Project.Model;

    /// <summary>Curtain + horizontal tracks: vertical-section axis (shared so they always line up).</summary>
    public AxisRange XRange { get; } = new(0, 1000);
    /// <summary>Curtain vertical axis (TVD).</summary>
    public AxisRange TvdRange { get; } = new(0, 100);
    /// <summary>Vertical tracks: TVT axis (reference bed at 0).</summary>
    public AxisRange TvtRange { get; } = new(-50, 50);

    public ObservableCollection<TrackViewModel> VerticalTracks { get; } = new();
    public ObservableCollection<TrackViewModel> HorizontalTracks { get; } = new();
    public ObservableCollection<TrackViewModel> AllTracks { get; } = new();
    public ObservableCollection<BedBoundaryViewModel> Beds { get; } = new();

    public IReadOnlyList<CurveRef> AvailableCurves { get; }
    public IReadOnlyList<string> BedNames { get; }
    public static IReadOnlyList<LineStyleKind> LineStyles { get; } = Enum.GetValues<LineStyleKind>();
    public static IReadOnlyList<ShadingMode> ShadingModes { get; } = Enum.GetValues<ShadingMode>();
    public static IReadOnlyList<ColorRampKind> Ramps { get; } = Enum.GetValues<ColorRampKind>();
    public static IReadOnlyList<CorrelationMode> Modes { get; } = Enum.GetValues<CorrelationMode>();
    public static IReadOnlyList<string> Palette { get; } = new[]
    {
        "#000000", "#404040", "#808080", "#1F4E9E", "#3A7BD5", "#00A3C4", "#2E9E3E", "#7DBA2F", "#C9A100",
        "#E07B00", "#D62728", "#A0225A", "#7B3FA0", "#8B4513", "#B5651D", "#FFFFFF",
    };

    public event EventHandler? Redraw;
    public void RaiseRedraw() => Redraw?.Invoke(this, EventArgs.Empty);

    public string WorkspacePath { get; set; }
    public string DefaultWorkspacePath { get; }

    string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    string _cursorText = "";
    public string CursorText { get => _cursorText; set => Set(ref _cursorText, value); }

    readonly Dictionary<string, AxisSeries?> _seriesCache = new();
    CurtainProfile? _curtain;
    (double min, double max) _lateralVs;

    public SteeringViewModel(SteeringProject project, string? workspacePath = null)
    {
        Project = project;
        AvailableCurves = project.AvailableCurves().ToList();
        BedNames = Model.ImportedBeds.Select(b => b.name).ToList();
        DefaultWorkspacePath = WorkspaceDocument.DefaultPath(project.Document.Project?.Name, project.Document.Migration?.SourceRoot, project.GeoModel.Name);
        WorkspacePath = workspacePath ?? DefaultWorkspacePath;

        VerticalTracks.CollectionChanged += Tracks_CollectionChanged;
        HorizontalTracks.CollectionChanged += Tracks_CollectionChanged;
        Beds.CollectionChanged += Beds_CollectionChanged;
        XRange.Changed += (_, _) => RaiseRedraw();
        TvdRange.Changed += (_, _) => RaiseRedraw();
        TvtRange.Changed += (_, _) => RaiseRedraw();
        XRange.MinSpan = 5; TvdRange.MinSpan = 0.5; TvtRange.MinSpan = 0.5;

        var t = Model.Table;
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int i = 0; i < t.Count; i++) { lo = Math.Min(lo, t.Vs[i]); hi = Math.Max(hi, t.Vs[i]); }
        _lateralVs = (lo, hi);
    }

    // ------------------------------------------------------------------ reference bed / mode

    public string ReferenceBed
    {
        get => Model.ReferenceBed;
        set
        {
            if (value == null || string.Equals(value, Model.ReferenceBed, StringComparison.OrdinalIgnoreCase)) return;
            // keep the same stratigraphic window on screen: shift the TVT range by the old->new reference offset
            double shift = Model.OffsetTvtOfBed(value);
            Model.SetReferenceBed(value);
            DerivedChanged();
            if (!double.IsNaN(shift)) { TvtRange.SetHome(TvtRange.HomeMin - shift, TvtRange.HomeMax - shift, apply: false); TvtRange.Pan(-shift); }
            OnPropertyChanged();
        }
    }

    public CorrelationMode Mode
    {
        get => Model.Mode;
        set
        {
            if (Model.Mode == value) return;
            Model.Mode = value;
            DerivedChanged();
            OnPropertyChanged();
        }
    }

    void DerivedChanged()
    {
        Project.InvalidateDerived();
        lock (_seriesCache) _seriesCache.Clear();
        _curtain = null;
        UpdateImportedBedTvt();
        RaiseRedraw();
    }

    void UpdateImportedBedTvt()
    {
        foreach (var b in Beds.Where(b => b.IsImported)) b.SetImportedTvt(Model.OffsetTvtOfBed(b.Name));
    }

    // ------------------------------------------------------------------ data access for the controls

    /// <summary>
    /// A curve positioned for a track: vertical tracks get (TVT, value); horizontal tracks get (vertical
    /// section, value) - an offset curve in a horizontal track is shown as its synthetic along the lateral.
    /// Null if the curve is not in the project or fails to load.
    /// </summary>
    public AxisSeries? GetSeries(string sourceId, bool vertical)
    {
        string key = (vertical ? "V|" : "H|") + sourceId;
        lock (_seriesCache) if (_seriesCache.TryGetValue(key, out var s)) return s;
        AxisSeries? result = null;
        try
        {
            var r = Project.FindCurve(sourceId);
            if (r != null)
            {
                if (!vertical && r.Origin == CurveOrigin.Offset) r = r with { Origin = CurveOrigin.Synthetic };
                var ds = Project.GetDepthSeries(r);
                result = vertical
                    ? (r.Origin == CurveOrigin.Offset ? Model.OffsetOnTvt(ds) : Model.LateralOnTvt(ds))
                    : Model.LateralOnVs(ds);
            }
        }
        catch (Exception ex)
        {
            Status = $"Could not load {sourceId}: {ex.Message}";
        }
        lock (_seriesCache) _seriesCache[key] = result;
        return result;
    }

    /// <summary>Bed profiles along the curtain (computed over the lateral's span plus margins, cached).</summary>
    public CurtainProfile Curtain
    {
        get
        {
            if (_curtain != null) return _curtain;
            double span = Math.Max(500, _lateralVs.max - _lateralVs.min);
            double a = _lateralVs.min - 0.5 * span, b = _lateralVs.max + 0.5 * span;
            int n = 2400;
            return _curtain = Model.BuildCurtain(a, b, (b - a) / n);
        }
    }

    /// <summary>TVD of a user bed along the curtain = reference bed depth + offset (parallel to the structure).</summary>
    public double[] UserBedDepths(BedBoundaryViewModel bed)
    {
        var c = Curtain;
        var r = new double[c.X.Length];
        for (int i = 0; i < r.Length; i++) r[i] = c.RefDepth[i] + bed.Offset;
        return r;
    }

    // ------------------------------------------------------------------ selection

    TrackViewModel? _selectedTrack;
    public TrackViewModel? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (Set(ref _selectedTrack, value))
            {
                SelectedCurve = value?.Curves.FirstOrDefault();
                OnPropertyChanged(nameof(HasSelectedTrack));
                RaiseRedraw();
            }
        }
    }
    public bool HasSelectedTrack => _selectedTrack != null;

    TrackCurveViewModel? _selectedCurve;
    public TrackCurveViewModel? SelectedCurve
    {
        get => _selectedCurve;
        set { if (Set(ref _selectedCurve, value)) { OnPropertyChanged(nameof(HasSelectedCurve)); OnPropertyChanged(nameof(ShadeCandidates)); } }
    }
    public bool HasSelectedCurve => _selectedCurve != null;

    /// <summary>Other curves in the selected curve's track (for curve-to-curve shading).</summary>
    public IReadOnlyList<string> ShadeCandidates =>
        _selectedCurve == null ? Array.Empty<string>() : _selectedCurve.Track.Curves.Where(c => c != _selectedCurve).Select(c => c.Source).ToList();

    BedBoundaryViewModel? _selectedBed;
    public BedBoundaryViewModel? SelectedBed { get => _selectedBed; set => Set(ref _selectedBed, value); }

    CurveRef? _curveToAdd;
    public CurveRef? CurveToAdd { get => _curveToAdd; set => Set(ref _curveToAdd, value); }

    // ------------------------------------------------------------------ editing operations

    public TrackViewModel AddTrack(TrackOrientation o, string? title = null)
    {
        int n = (o == TrackOrientation.Vertical ? VerticalTracks.Count : HorizontalTracks.Count) + 1;
        var t = new TrackViewModel(new TrackSpec
        {
            Title = title ?? (o == TrackOrientation.Vertical ? $"Track {n}" : $"Log {n}"), Orientation = o,
            Size = o == TrackOrientation.Vertical ? 210 : 130, ScaleMin = 0, ScaleMax = 150,
        });
        (o == TrackOrientation.Vertical ? VerticalTracks : HorizontalTracks).Add(t);
        SelectedTrack = t;
        return t;
    }

    public void RemoveTrack(TrackViewModel? t)
    {
        if (t == null) return;
        VerticalTracks.Remove(t);
        HorizontalTracks.Remove(t);
        if (SelectedTrack == t) SelectedTrack = AllTracks.FirstOrDefault();
    }

    public TrackCurveViewModel? AddCurve(TrackViewModel? t, CurveRef? r, CurveSpec? style = null)
    {
        if (t == null || r == null) return null;
        var spec = style ?? new CurveSpec { Color = Palette[(3 + t.Curves.Count * 3) % Palette.Count] };
        spec.Source = r.Id;
        var c = new TrackCurveViewModel(t, r, spec);
        t.Curves.Add(c);
        if (t.Curves.Count == 1) AutoScale(t, r);
        SelectedTrack = t;
        SelectedCurve = c;
        RaiseRedraw();
        return c;
    }

    /// <summary>Sets a sensible scale for a track from its first curve (log for resistivity units).</summary>
    public void AutoScale(TrackViewModel t, CurveRef r)
    {
        bool res = (r.Unit ?? "").Contains("OHM", StringComparison.OrdinalIgnoreCase);
        if (res) { t.LogScale = true; t.ScaleMin = 0.2; t.ScaleMax = 2000; return; }
        try
        {
            var s = Project.GetDepthSeries(r.Origin == CurveOrigin.Synthetic ? r with { Origin = CurveOrigin.Offset } : r);
            var vals = s.Value.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
            if (vals.Length > 10)
            {
                double lo = vals[(int)(vals.Length * 0.01)], hi = vals[(int)(vals.Length * 0.99)];
                var ticks = Controls.Steering.Draw.NiceRange(lo, hi);
                t.LogScale = false; t.ScaleMin = ticks.min; t.ScaleMax = ticks.max;
            }
        }
        catch { }
    }

    public void RemoveCurve(TrackCurveViewModel? c)
    {
        if (c == null) return;
        c.Track.Curves.Remove(c);
        foreach (var o in c.Track.Curves.Where(o => o.ShadeOther == c.Source)) o.ShadeMode = ShadingMode.None;
        if (SelectedCurve == c) SelectedCurve = c.Track.Curves.FirstOrDefault();
        RaiseRedraw();
    }

    public BedBoundaryViewModel AddUserBed(string name, double offset, string color = "#D62728", double thickness = 1.5)
    {
        var b = new BedBoundaryViewModel(new BedBoundarySpec { Name = name, Kind = BedKind.User, Offset = offset, Color = color, Thickness = thickness });
        Beds.Add(b);
        SelectedBed = b;
        return b;
    }

    public void RemoveBed(BedBoundaryViewModel? b)
    {
        if (b == null) return;
        if (b.IsImported) { b.Visible = false; Status = $"Imported bed '{b.Name}' hidden (imported beds can be hidden, not deleted)."; return; }
        Beds.Remove(b);
    }

    // ------------------------------------------------------------------ change tracking

    void Tracks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (TrackViewModel t in e.NewItems) { t.PropertyChanged += Child_PropertyChanged; t.CurveChanged += Child_Changed; }
        if (e.OldItems != null) foreach (TrackViewModel t in e.OldItems) { t.PropertyChanged -= Child_PropertyChanged; t.CurveChanged -= Child_Changed; }
        AllTracks.Clear();
        foreach (var t in HorizontalTracks) AllTracks.Add(t);
        foreach (var t in VerticalTracks) AllTracks.Add(t);
        OnPropertyChanged(nameof(VerticalHeaderHeight));
        RaiseRedraw();
    }

    void Beds_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (BedBoundaryViewModel b in e.NewItems) b.PropertyChanged += Child_PropertyChanged;
        if (e.OldItems != null) foreach (BedBoundaryViewModel b in e.OldItems) b.PropertyChanged -= Child_PropertyChanged;
        RaiseRedraw();
    }

    void Child_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrackViewModel.Curves)) OnPropertyChanged(nameof(VerticalHeaderHeight));
        if (e.PropertyName == nameof(TrackCurveViewModel.ShadeMode)) OnPropertyChanged(nameof(ShadeCandidates));
        RaiseRedraw();
    }

    void Child_Changed(object? sender, EventArgs e) => RaiseRedraw();

    /// <summary>Header height shared by all vertical tracks (one legend row per curve of the busiest track).</summary>
    public double VerticalHeaderHeight => 22 + 30 * Math.Max(1, VerticalTracks.Select(t => t.Curves.Count).DefaultIfEmpty(1).Max());

    // ------------------------------------------------------------------ view ranges

    /// <summary>Home ranges: the lateral from landing to TD, with the beds around it.</summary>
    public void ComputeHomeRanges()
    {
        var t = Model.Table;
        int land = Enumerable.Range(0, t.Count).FirstOrDefault(i => t.Inc[i] > 80, 0);
        double vs0 = t.Vs[land], vs1 = _lateralVs.max;
        double pad = Math.Max(100, (vs1 - vs0) * 0.04);
        XRange.SetHome(vs0 - 3 * pad, vs1 + pad);

        double lo = double.PositiveInfinity, hi = double.NegativeInfinity, tlo = double.PositiveInfinity, thi = double.NegativeInfinity;
        for (int i = land; i < t.Count; i++)
        {
            if (t.Md[i] > Model.Lateral.LastMd) break;
            lo = Math.Min(lo, Math.Min(t.Tvd[i], t.RefDepth[i]));
            hi = Math.Max(hi, Math.Max(t.Tvd[i], t.RefDepth[i]));
            double tv = Mode == CorrelationMode.DepthIndexStretch ? t.TvtStretch[i] : t.Tvt[i];
            tlo = Math.Min(tlo, tv); thi = Math.Max(thi, tv);
        }
        if (double.IsInfinity(lo)) { lo = t.Tvd.Min(); hi = t.Tvd.Max(); tlo = -50; thi = 50; }
        TvdRange.SetHome(lo - 40, hi + 40);
        // include the imported beds near the lateral in the TVT window
        var bedTvt = Beds.Where(b => b.IsImported && !double.IsNaN(b.Tvt) && b.Tvt > tlo - 40 && b.Tvt < thi + 40).Select(b => b.Tvt).ToList();
        if (bedTvt.Count > 0) { tlo = Math.Min(tlo, bedTvt.Min()); thi = Math.Max(thi, bedTvt.Max()); }
        TvtRange.SetHome(Math.Min(tlo, 0) - 25, Math.Max(thi, 0) + 25);
    }

    public void ResetView() { XRange.Reset(); TvdRange.Reset(); TvtRange.Reset(); }

    // ------------------------------------------------------------------ defaults / workspace

    /// <summary>The gridded bed whose surface is nearest the lateral (median |TVT| over inclination &gt; 80 deg).</summary>
    public static string NearestGridBed(SteeringModel m)
    {
        string best = m.ReferenceBed; double bestScore = double.PositiveInfinity;
        string saved = m.ReferenceBed;
        foreach (var b in m.Structure.Beds)
        {
            m.SetReferenceBed(b.Name);
            var t = m.Table;
            var v = Enumerable.Range(0, t.Count).Where(i => t.Inc[i] > 80).Select(i => Math.Abs(t.Tvt[i])).OrderBy(x => x).ToArray();
            double score = v.Length == 0 ? double.PositiveInfinity : v[v.Length / 2];
            if (score < bestScore) { bestScore = score; best = b.Name; }
        }
        m.SetReferenceBed(saved);
        return best;
    }

    static readonly string[] MarkerColors = { "#6A5ACD", "#2E8B57", "#B8860B", "#4682B4", "#9932CC", "#CD5C5C", "#008B8B", "#8B008B", "#556B2F", "#D2691E" };

    /// <summary>Default workspace: GR and resistivity in both a vertical and a horizontal track, all imported beds shown.</summary>
    public WorkspaceDocument DefaultWorkspace()
    {
        string refBed = NearestGridBed(Model);
        var w = new WorkspaceDocument { ReferenceBed = refBed, CorrelationMode = nameof(CorrelationMode.ParallelToReference) };
        string? Pick(params string[] ids) => ids.FirstOrDefault(id => Project.FindCurve(id) != null);
        string? rtGr = Pick("rt:GRCA", "rt:GR", "rt:GRC");
        string? offGr = Pick("off:GR", "off:GRC");
        string? rtRes = Pick("rt:RDAH", "rt:RDPH", "rt:RT");
        string? offRes = Pick("off:AT90", "off:RT", "off:AF90");
        string? pzsRes = rtRes == null ? null : Pick("pzs:" + rtRes[3..] + "_M");

        var hGr = new TrackSpec { Title = "Gamma ray", Orientation = TrackOrientation.Horizontal, Size = 150, ScaleMin = 0, ScaleMax = 250 };
        if (rtGr != null) hGr.Curves.Add(new CurveSpec { Source = rtGr, Color = "#2E9E3E", Thickness = 1, Shading = new ShadingSpec { Mode = ShadingMode.Gradient, Ramp = ColorRampKind.Earth, Opacity = 0.85 } });
        if (offGr != null) hGr.Curves.Add(new CurveSpec { Source = "syn:" + offGr[4..], Color = "#000000", Thickness = 1.6 });
        var hRes = new TrackSpec { Title = "Resistivity", Orientation = TrackOrientation.Horizontal, Size = 120, ScaleMin = 1, ScaleMax = 1000, LogScale = true };
        if (rtRes != null) hRes.Curves.Add(new CurveSpec { Source = rtRes, Color = "#1F4E9E", Thickness = 1 });
        if (pzsRes != null) hRes.Curves.Add(new CurveSpec { Source = pzsRes, Color = "#D62728", Thickness = 1.3, LineStyle = LineStyleKind.Dashed });
        if (offRes != null) hRes.Curves.Add(new CurveSpec { Source = "syn:" + offRes[4..], Color = "#000000", Thickness = 1.3 });

        var vGr = new TrackSpec { Title = "GR (TVT)", Orientation = TrackOrientation.Vertical, Size = 230, ScaleMin = 0, ScaleMax = 250 };
        if (offGr != null) vGr.Curves.Add(new CurveSpec { Source = offGr, Color = "#000000", Thickness = 1.2 });
        if (rtGr != null) vGr.Curves.Add(new CurveSpec { Source = rtGr, Color = "#2E9E3E", Thickness = 1 });
        var vRes = new TrackSpec { Title = "Resistivity (TVT)", Orientation = TrackOrientation.Vertical, Size = 200, ScaleMin = 1, ScaleMax = 1000, LogScale = true };
        if (offRes != null) vRes.Curves.Add(new CurveSpec { Source = offRes, Color = "#000000", Thickness = 1.2 });
        if (rtRes != null) vRes.Curves.Add(new CurveSpec { Source = rtRes, Color = "#1F4E9E", Thickness = 1 });
        w.Tracks.AddRange(new[] { hGr, hRes, vGr, vRes }.Where(t => t.Curves.Count > 0));

        int k = 0;
        foreach (var b in Model.ImportedBeds)
            w.Beds.Add(new BedBoundarySpec
            {
                Name = b.name, Kind = BedKind.Imported,
                Color = b.isGrid ? "#8B4513" : MarkerColors[k++ % MarkerColors.Length],
                Thickness = b.isGrid ? 2.5 : 1,
            });
        return w;
    }

    /// <summary>Applies a workspace (tracks, curves, beds, reference bed, mode, view ranges). Missing curves are kept and flagged.</summary>
    public void ApplyWorkspace(WorkspaceDocument w)
    {
        if (!string.IsNullOrEmpty(w.ReferenceBed) && BedNames.Contains(w.ReferenceBed, StringComparer.OrdinalIgnoreCase))
            Model.SetReferenceBed(w.ReferenceBed);
        Model.Mode = Enum.TryParse<CorrelationMode>(w.CorrelationMode, out var mo) ? mo : CorrelationMode.ParallelToReference;
        Project.InvalidateDerived();
        lock (_seriesCache) _seriesCache.Clear();
        _curtain = null;

        VerticalTracks.Clear(); HorizontalTracks.Clear(); Beds.Clear();
        foreach (var ts in w.Tracks)
        {
            var t = new TrackViewModel(ts);
            foreach (var cs in ts.Curves) t.Curves.Add(new TrackCurveViewModel(t, Project.FindCurve(cs.Source), cs));
            (ts.Orientation == TrackOrientation.Vertical ? VerticalTracks : HorizontalTracks).Add(t);
        }
        // every imported bed appears exactly once (style from the workspace if present, else default style)
        var defaults = DefaultBedStyles();
        foreach (var b in Model.ImportedBeds)
        {
            var spec = w.Beds.FirstOrDefault(x => x.Kind == BedKind.Imported && string.Equals(x.Name, b.name, StringComparison.OrdinalIgnoreCase))
                       ?? defaults.GetValueOrDefault(b.name) ?? new BedBoundarySpec { Name = b.name, Kind = BedKind.Imported };
            Beds.Add(new BedBoundaryViewModel(spec, b.isGrid));
        }
        foreach (var u in w.Beds.Where(x => x.Kind == BedKind.User)) Beds.Add(new BedBoundaryViewModel(u));
        UpdateImportedBedTvt();

        ComputeHomeRanges();
        var v = w.View;
        if (v?.XMin != null && v.XMax != null) XRange.Set(v.XMin.Value, v.XMax.Value);
        if (v?.TvdMin != null && v.TvdMax != null) TvdRange.Set(v.TvdMin.Value, v.TvdMax.Value);
        if (v?.TvtMin != null && v.TvtMax != null) TvtRange.Set(v.TvtMin.Value, v.TvtMax.Value);
        if (v?.ShowProperties != null) ShowProperties = v.ShowProperties.Value;

        SelectedTrack = AllTracks.FirstOrDefault();
        OnPropertyChanged(nameof(ReferenceBed));
        OnPropertyChanged(nameof(Mode));
        RaiseRedraw();
    }

    Dictionary<string, BedBoundarySpec> DefaultBedStyles()
    {
        var d = new Dictionary<string, BedBoundarySpec>(StringComparer.OrdinalIgnoreCase);
        int k = 0;
        foreach (var b in Model.ImportedBeds)
            d[b.name] = new BedBoundarySpec { Name = b.name, Kind = BedKind.Imported, Color = b.isGrid ? "#8B4513" : MarkerColors[k++ % MarkerColors.Length], Thickness = b.isGrid ? 2.5 : 1 };
        return d;
    }

    public WorkspaceDocument ToWorkspace() => new()
    {
        ProjectName = Project.Document.Project?.Name,
        ProjectSourceRoot = Project.Document.Migration?.SourceRoot,
        ModelName = Project.GeoModel.Name,
        ReferenceBed = Model.ReferenceBed,
        CorrelationMode = Model.Mode.ToString(),
        Tracks = HorizontalTracks.Concat(VerticalTracks).Select(t => t.ToSpec()).ToList(),
        Beds = Beds.Select(b => b.ToSpec()).ToList(),
        View = new ViewSpec
        {
            XMin = XRange.Min, XMax = XRange.Max, TvdMin = TvdRange.Min, TvdMax = TvdRange.Max, TvtMin = TvtRange.Min, TvtMax = TvtRange.Max,
            ShowProperties = ShowProperties,
        },
    };

    /// <summary>Loads the workspace file if it exists, else applies defaults. Returns true if a file was loaded.</summary>
    public bool LoadOrDefault()
    {
        if (File.Exists(WorkspacePath))
        {
            try
            {
                ApplyWorkspace(WorkspaceDocument.Load(WorkspacePath));
                Status = "Workspace loaded: " + WorkspacePath;
                return true;
            }
            catch (Exception ex)
            {
                Status = $"Workspace file could not be read ({ex.Message}); using defaults.";
            }
        }
        ApplyWorkspace(DefaultWorkspace());
        if (Status.Length == 0) Status = "Default steering layout (no saved workspace yet).";
        return false;
    }

    public void Save(string? path = null)
    {
        path ??= WorkspacePath;
        ToWorkspace().Save(path);
        WorkspacePath = path;
        OnPropertyChanged(nameof(WorkspacePath));
        Status = "Workspace saved: " + path;
    }

    public void Load(string path)
    {
        ApplyWorkspace(WorkspaceDocument.Load(path));
        WorkspacePath = path;
        OnPropertyChanged(nameof(WorkspacePath));
        Status = "Workspace loaded: " + path;
    }

    bool _showProps = true;
    public bool ShowProperties { get => _showProps; set => Set(ref _showProps, value); }
}
