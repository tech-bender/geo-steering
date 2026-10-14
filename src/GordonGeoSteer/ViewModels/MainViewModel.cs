using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using GordonGeoSteer.Model;
using GordonGeoSteer.Services;
using GordonGeoSteer.ViewModels.Steering;
using GordonGeoSteer.Engine.Steering;

namespace GordonGeoSteer.ViewModels;

public sealed class TreeNode : ObservableObject
{
    readonly Func<object?>? _factory;
    object? _detail;
    bool _detailBuilt;
    bool _isExpanded, _isSelected;

    public TreeNode(string header, string kind, Func<object?>? detailFactory = null)
    {
        Header = header; Kind = kind; _factory = detailFactory;
    }

    public string Header { get; }
    /// <summary>project / report / wells / well / survey / curves / curve / models / model / targets / geology / inventory</summary>
    public string Kind { get; }
    public ObservableCollection<TreeNode> Children { get; } = new();

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>The detail view-model shown when this node is selected (built lazily, then cached).</summary>
    public object? Detail
    {
        get
        {
            if (!_detailBuilt)
            {
                _detailBuilt = true;
                try { _detail = _factory?.Invoke(); }
                catch (Exception ex) { _detail = new ErrorViewModel(Header, ex.ToString()); }
            }
            return _detail;
        }
    }

    public TreeNode Add(TreeNode child) { Children.Add(child); return child; }

    public IEnumerable<TreeNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants()) yield return d;
    }
}

public sealed record ErrorViewModel(string Title, string Message);

public sealed class MainViewModel : ObservableObject
{
    public ObservableCollection<TreeNode> Nodes { get; } = new();

    TreeNode? _selected;
    public TreeNode? SelectedNode
    {
        get => _selected;
        set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(SelectedDetail)); }
    }
    public object? SelectedDetail => _selected?.Detail;

    string _status = "Ready. File > Open PZS Project Folder... to import a PZS project.";
    public string Status { get => _status; set => Set(ref _status, value); }

    bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    string _log = "";
    /// <summary>Live importer console output for the current/last import.</summary>
    public string ImportLog { get => _log; private set => Set(ref _log, value); }

    string? _banner;
    /// <summary>Error / warning banner text (null = hidden).</summary>
    public string? Banner { get => _banner; set { if (Set(ref _banner, value)) OnPropertyChanged(nameof(HasBanner)); } }
    public bool HasBanner => !string.IsNullOrEmpty(_banner);

    bool _bannerIsError;
    public bool BannerIsError { get => _bannerIsError; set => Set(ref _bannerIsError, value); }

    public ProjectDocument? Document { get; private set; }
    public string? ProjectJsonPath { get; private set; }
    public string? ProjectDir => ProjectJsonPath == null ? null : Path.GetDirectoryName(ProjectJsonPath);
    public ImportResult? LastImport { get; private set; }

    readonly StringBuilder _logBuf = new();

    void AppendLog(string line)
    {
        lock (_logBuf) _logBuf.AppendLine(line);
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) ImportLog = SnapshotLog();
        else d.BeginInvoke(() => { ImportLog = SnapshotLog(); Status = "Importing... " + line.Trim(); });
    }

    string SnapshotLog() { lock (_logBuf) return _logBuf.ToString(); }

    /// <summary>Runs PzsImporter on a PZS project folder, then loads its project.json.
    /// Returns true if a project was loaded (possibly with cross-check failures flagged).</summary>
    public async Task<bool> ImportAndLoadAsync(string pzsFolder, string? outputDir = null)
    {
        if (IsBusy) return false;
        IsBusy = true;
        Banner = null;
        lock (_logBuf) _logBuf.Clear();
        ImportLog = "";
        outputDir ??= ImporterRunner.DefaultOutputDir(pzsFolder);
        Status = "Importing " + pzsFolder + " ...";
        try
        {
            ImportResult res;
            try
            {
                res = await ImporterRunner.RunAsync(pzsFolder, outputDir, AppendLog);
            }
            catch (Exception ex)
            {
                ShowError("Import could not be started: " + ex.Message);
                return false;
            }
            LastImport = res;
            ImportLog = res.Log;

            if (res.ExitCode is 2 or 4 || (res.ExitCode != 0 && res.ExitCode != 3) || !res.ProjectJsonWritten)
            {
                ShowError($"Import FAILED (PzsImporter exit code {res.ExitCode}: {res.ExitMeaning}). " +
                          (res.ProjectJsonWritten ? "" : "No project.json was produced. ") + "See the importer log below.");
                return false;
            }

            bool ok = LoadProjectJson(res.ProjectJsonPath, res.Log);
            if (ok && res.ExitCode == 3)
            {
                int failed = Document!.Migration?.CrossChecks.Count(c => !c.Pass) ?? -1;
                Banner = $"Import finished but {failed} cross-check(s) FAILED (exit code 3). The data was loaded, but treat it with caution - see 'Import report'.";
                BannerIsError = true;
            }
            return ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    void ShowError(string msg)
    {
        Banner = msg; BannerIsError = true; Status = msg;
    }

    /// <summary>Loads an importer output folder's project.json and rebuilds the navigation tree.</summary>
    public bool LoadProjectJson(string projectJsonPath, string? importerLog = null)
    {
        ProjectDocument doc;
        try
        {
            doc = ProjectJson.LoadProject(projectJsonPath);
        }
        catch (Exception ex)
        {
            ShowError("Could not load " + projectJsonPath + ": " + ex.Message);
            return false;
        }
        Document = doc;
        ProjectJsonPath = Path.GetFullPath(projectJsonPath);
        BuildTree(doc, importerLog);
        BuildSteering(doc);

        var warn = doc.Migration?.Warnings ?? new();
        int failed = doc.Migration?.CrossChecks.Count(c => !c.Pass) ?? 0;
        if (failed > 0)
        {
            Banner = $"{failed} importer cross-check(s) FAILED - see 'Import report'.";
            BannerIsError = true;
        }
        else if (warn.Count > 0)
        {
            Banner = $"{warn.Count} importer warning(s): " + string.Join("  |  ", warn);
            BannerIsError = false;
        }
        else Banner = null;

        Status = $"Loaded {doc.Project?.Name}: {doc.Wells.Count} wells, {doc.Models.Count} model(s), " +
                 $"{doc.Wells.Sum(w => w.Curves.Count)} curves, {doc.FileInventory.Count} files; " +
                 $"cross-checks {doc.Migration?.CrossChecks.Count(c => c.Pass)}/{doc.Migration?.CrossChecks.Count} passed.";
        return true;
    }

    void BuildTree(ProjectDocument doc, string? importerLog)
    {
        Nodes.Clear();
        string dir = Path.GetDirectoryName(ProjectJsonPath!)!;
        string jsonPath = ProjectJsonPath!;

        var root = new TreeNode("Project: " + (doc.Project?.Name ?? "(unnamed)"), "project",
            () => new ProjectDetailViewModel(doc, jsonPath)) { IsExpanded = true };
        Nodes.Add(root);

        int failed = doc.Migration?.CrossChecks.Count(c => !c.Pass) ?? 0;
        root.Add(new TreeNode(failed > 0 ? $"Import report ({failed} FAILED checks)" : "Import report", "report",
            () => new ImportReportViewModel(doc, importerLog)));

        var wells = root.Add(new TreeNode($"Wells ({doc.Wells.Count})", "wells") { IsExpanded = true });
        foreach (var w in doc.Wells)
        {
            var wn = wells.Add(new TreeNode($"{w.Name} [{WellRoles.Short(w)}] - {w.SourceFile}", "well", () => new WellDetailViewModel(w)));
            wn.Add(new TreeNode($"Survey ({w.Surveys?.StationCount ?? 0} stations)", "survey", () => new SurveyViewModel(w)));
            var cn = wn.Add(new TreeNode($"Curves ({w.Curves.Count})", "curves", () => new CurveListViewModel(w)));
            foreach (var c in w.Curves)
                cn.Add(new TreeNode($"{c.Name}  [{c.ValueUnit}]", "curve", () => new CurveViewModel(c, w, dir)));
        }

        var models = root.Add(new TreeNode($"Models ({doc.Models.Count})", "models") { IsExpanded = true });
        foreach (var m in doc.Models)
        {
            var mn = models.Add(new TreeNode(m.Name ?? m.Id, "model", () => new ModelDetailViewModel(m)) { IsExpanded = true });
            mn.Add(new TreeNode($"Targets ({m.Targets.Count})", "targets", () => new TargetsViewModel(m)));
            mn.Add(new TreeNode($"Geology model ({m.GeologyModel?.Beds.Count ?? 0} beds, {m.GeologyModel?.Markers.Count ?? 0} markers)", "geology",
                () => new GeologyViewModel(m)));
        }

        root.Add(new TreeNode($"File inventory ({doc.FileInventory.Count})", "inventory", () => new InventoryViewModel(doc)));

        root.IsSelected = true;
        SelectedNode = root;
    }

    public IEnumerable<TreeNode> AllNodes() => Nodes.SelectMany(n => n.SelfAndDescendants());

    // ------------------------------------------------------------------ steering view

    SteeringViewModel? _steering;
    /// <summary>The steering view for the project's (first) geosteering model; null if it could not be built.</summary>
    public SteeringViewModel? Steering { get => _steering; private set { if (Set(ref _steering, value)) OnPropertyChanged(nameof(HasSteering)); } }
    public bool HasSteering => _steering != null;

    string _steeringMessage = "Load a project to use the steering view.";
    public string SteeringMessage { get => _steeringMessage; private set => Set(ref _steeringMessage, value); }

    /// <summary>Overrides where the steering workspace is read/written (self-test uses its report folder).</summary>
    public string? WorkspacePathOverride { get; set; }

    void BuildSteering(ProjectDocument doc)
    {
        Steering = null;
        if (doc.Models.Count == 0) { SteeringMessage = "This project has no geosteering model (.pzscfg), so there is nothing to steer against."; return; }
        try
        {
            var sp = SteeringProject.Build(doc, ProjectDir!);
            var vm = new SteeringViewModel(sp, WorkspacePathOverride);
            vm.LoadOrDefault();
            Steering = vm;
            SteeringMessage = "";
        }
        catch (Exception ex)
        {
            SteeringMessage = "The steering view could not be built for this project:" + Environment.NewLine + ex;
        }
    }
}
