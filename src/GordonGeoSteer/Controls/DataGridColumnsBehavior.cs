using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace GordonGeoSteer.Controls;

/// <summary>
/// Attached property that rebuilds a DataGrid's columns from a list of headers: first column binds to
/// "North", column i+1 binds to "Cells[i]". Used for the numeric bed-grid table.
/// </summary>
public static class DataGridColumnsBehavior
{
    public static readonly DependencyProperty CellHeadersProperty =
        DependencyProperty.RegisterAttached("CellHeaders", typeof(IList<string>), typeof(DataGridColumnsBehavior),
            new PropertyMetadata(null, OnChanged));

    public static void SetCellHeaders(DependencyObject o, IList<string>? v) => o.SetValue(CellHeadersProperty, v);
    public static IList<string>? GetCellHeaders(DependencyObject o) => (IList<string>?)o.GetValue(CellHeadersProperty);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid dg) return;
        dg.Columns.Clear();
        if (e.NewValue is not IList<string> headers || headers.Count == 0) return;
        dg.Columns.Add(new DataGridTextColumn { Header = "North \\ East", Binding = new Binding("North"), FontWeight = FontWeights.SemiBold });
        for (int i = 0; i < headers.Count; i++)
            dg.Columns.Add(new DataGridTextColumn { Header = headers[i], Binding = new Binding($"Cells[{i}]") });
    }
}
