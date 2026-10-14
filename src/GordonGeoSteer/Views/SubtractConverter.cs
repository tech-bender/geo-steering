using System.Globalization;
using System.Windows.Data;

namespace GordonGeoSteer.Views;

/// <summary>Number minus a fixed amount (never below zero); used to leave room for the TVT axis.</summary>
public sealed class SubtractConverter : IValueConverter
{
    public double Amount { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) => value is double d ? Math.Max(0, d - Amount) : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
