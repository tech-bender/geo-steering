using System.Windows;
using System.Windows.Controls;
using GordonGeoSteer.Engine.Workspace;
using GordonGeoSteer.ViewModels.Steering;
using Microsoft.Win32;

namespace GordonGeoSteer.Views;

public partial class SteeringView : UserControl
{
    public SteeringView() { InitializeComponent(); }

    SteeringViewModel? VM => DataContext as SteeringViewModel;

    void ZoomXIn(object s, RoutedEventArgs e) => VM?.XRange.Zoom(1.5);
    void ZoomXOut(object s, RoutedEventArgs e) => VM?.XRange.Zoom(1 / 1.5);
    void ZoomYIn(object s, RoutedEventArgs e) => VM?.TvdRange.Zoom(1.5);
    void ZoomYOut(object s, RoutedEventArgs e) => VM?.TvdRange.Zoom(1 / 1.5);
    void ZoomTIn(object s, RoutedEventArgs e) => VM?.TvtRange.Zoom(1.5);
    void ZoomTOut(object s, RoutedEventArgs e) => VM?.TvtRange.Zoom(1 / 1.5);
    void ResetView(object s, RoutedEventArgs e) => VM?.ResetView();

    void AddVertical(object s, RoutedEventArgs e) => VM?.AddTrack(TrackOrientation.Vertical);
    void AddHorizontal(object s, RoutedEventArgs e) => VM?.AddTrack(TrackOrientation.Horizontal);
    void RemoveTrack(object s, RoutedEventArgs e) => VM?.RemoveTrack(VM.SelectedTrack);
    void AddCurve(object s, RoutedEventArgs e)
    {
        if (VM == null) return;
        if (VM.SelectedTrack == null) { VM.Status = "Select a track first."; return; }
        if (VM.CurveToAdd == null) { VM.Status = "Choose a curve in the list first."; return; }
        VM.AddCurve(VM.SelectedTrack, VM.CurveToAdd);
    }
    void RemoveCurve(object s, RoutedEventArgs e) => VM?.RemoveCurve(VM.SelectedCurve);
    void AddBed(object s, RoutedEventArgs e) => VM?.AddUserBed($"Boundary {VM.Beds.Count(b => b.IsUser) + 1}", 0);
    void RemoveBed(object s, RoutedEventArgs e) => VM?.RemoveBed(VM.SelectedBed);

    void SaveWs(object s, RoutedEventArgs e)
    {
        try { VM?.Save(); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Save workspace failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void LoadWs(object s, RoutedEventArgs e)
    {
        if (VM == null) return;
        var dlg = new OpenFileDialog { Title = "Load a GordonGeoSteer workspace", Filter = "Gordon workspace (*.gws.json;*.json)|*.gws.json;*.json" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try { VM.Load(dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Load workspace failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// <summary>The curtain control (exposed for the self-test's zoom checks).</summary>
    public Controls.Steering.CurtainControl Curtain => curtain;
}
