using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GordonGeoSteer.Services;
using GordonGeoSteer.ViewModels;
using GordonGeoSteer.Views;
using Microsoft.Win32;

namespace GordonGeoSteer;

public partial class MainWindow : Window
{
    public MainViewModel VM { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = VM;
    }

    void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        VM.SelectedNode = e.NewValue as TreeNode;

    void LogBox_TextChanged(object sender, TextChangedEventArgs e) => logBox.ScrollToEnd();

    async void OpenPzsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (VM.IsBusy) return;
        var dlg = new OpenFolderDialog { Title = "Select a PZS project folder to import" };
        if (dlg.ShowDialog(this) != true) return;
        string src = dlg.FolderName;
        string outDir = ImporterRunner.DefaultOutputDir(src);
        logExpander.IsExpanded = true;
        bool ok = await VM.ImportAndLoadAsync(src, outDir);
        if (ok) logExpander.IsExpanded = VM.HasBanner && VM.BannerIsError;
    }

    void OpenJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open an imported project.json",
            Filter = "GordonGeoSteer project (project.json)|project.json|JSON files (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) == true) VM.LoadProjectJson(dlg.FileName);
    }

    void Dump_Click(object sender, RoutedEventArgs e)
    {
        if (VM.Document == null) { MessageBox.Show(this, "No project is loaded.", "Dump loaded data"); return; }
        var dlg = new SaveFileDialog
        {
            Title = "Dump loaded view-model data",
            FileName = "gordongeosteer-dump.txt",
            InitialDirectory = VM.ProjectDir,
            Filter = "Text (*.txt)|*.txt",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            DebugDump.Write(VM, dlg.FileName);
            VM.Status = "Wrote " + dlg.FileName;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Dump failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "GordonGeoSteer (0.2.0) - Gordon Technologies\n\n" +
        "Imports a PZS project folder via the external PzsImporter tool and browses the resulting\n" +
        "gordon.geosteering.project JSON: wells, curves, surveys, targets, geology model, file inventory.\n\n" +
        "Steering view: curtain section, TVT log tracks, synthetic logs, curve styling/shading and bed\n" +
        "boundaries, saved to a Gordon workspace file. No real-time feed or geomod editing yet.",
        "About GordonGeoSteer");

    void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ steering view

    public void ShowSteering() { mainTabs.SelectedItem = steeringTab; }
    public void ShowBrowse() { mainTabs.SelectedIndex = 0; }
    public SteeringView SteeringViewControl => steeringView;

    void ShowSteering_Click(object sender, RoutedEventArgs e) => ShowSteering();

    void SaveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (VM.Steering == null) return;
        try { VM.Steering.Save(); VM.Status = VM.Steering.Status; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save workspace failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void SaveWorkspaceAs_Click(object sender, RoutedEventArgs e)
    {
        if (VM.Steering == null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Save GordonGeoSteer workspace", Filter = "Gordon workspace (*.gws.json)|*.gws.json",
            FileName = Path.GetFileName(VM.Steering.WorkspacePath), InitialDirectory = Path.GetDirectoryName(VM.Steering.WorkspacePath),
        };
        if (dlg.ShowDialog(this) != true) return;
        try { VM.Steering.Save(dlg.FileName); VM.Status = VM.Steering.Status; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save workspace failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void LoadWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (VM.Steering == null) return;
        var dlg = new OpenFileDialog { Title = "Load GordonGeoSteer workspace", Filter = "Gordon workspace (*.gws.json;*.json)|*.gws.json;*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try { VM.Steering.Load(dlg.FileName); ShowSteering(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Load workspace failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void ResetWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (VM.Steering == null) return;
        VM.Steering.ApplyWorkspace(VM.Steering.DefaultWorkspace());
        VM.Steering.Status = "Layout reset to defaults (not saved yet).";
        ShowSteering();
    }

    void Validation_Click(object sender, RoutedEventArgs e)
    {
        if (VM.Steering == null) return;
        string text;
        try { text = Engine.Validation.EngineValidation.Run(VM.Steering.Project).Text; }
        catch (Exception ex) { text = "Validation failed: " + ex; }
        var w = new Window
        {
            Title = "Engine validation", Width = 1000, Height = 640, Owner = this,
            Content = new TextBox { Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        w.Show();
    }

    // ------------------------------------------------------------------ self-test support

    /// <summary>Selects a node as if clicked (expanding its ancestors) and waits until the UI has rendered it.</summary>
    public async Task SelectAndRenderAsync(TreeNode node)
    {
        var path = Ancestors(node).ToHashSet();
        // Keep the tree readable: expand only the path to the node (plus top-level groups).
        foreach (var n in VM.AllNodes())
            if (n.Children.Count > 0) n.IsExpanded = path.Contains(n) || n.Kind is "project" or "wells" or "models";
        node.IsSelected = true;
        VM.SelectedNode = node;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    IEnumerable<TreeNode> Ancestors(TreeNode target)
    {
        foreach (var root in VM.Nodes)
        {
            var path = new List<TreeNode>();
            if (FindPath(root, target, path)) return path.Take(path.Count - 1);
        }
        return Enumerable.Empty<TreeNode>();
    }

    static bool FindPath(TreeNode cur, TreeNode target, List<TreeNode> path)
    {
        path.Add(cur);
        if (cur == target) return true;
        foreach (var c in cur.Children) if (FindPath(c, target, path)) return true;
        path.RemoveAt(path.Count - 1);
        return false;
    }

    /// <summary>Renders the window's current visual tree to a PNG (WPF RenderTargetBitmap).</summary>
    public void SaveWindowPng(string path)
    {
        var content = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
