using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using GordonGeoSteer.Engine.Steering;
using GordonGeoSteer.Engine.Workspace;

namespace GordonGeoSteer.ViewModels.Steering;

/// <summary>One curve in a track, with its user-selectable style and shading.</summary>
public sealed class TrackCurveViewModel : ObservableObject
{
    public TrackViewModel Track { get; }
    public CurveRef? Ref { get; }

    public TrackCurveViewModel(TrackViewModel track, CurveRef? r, CurveSpec spec)
    {
        Track = track; Ref = r;
        _source = spec.Source;
        _color = spec.Color; _thickness = spec.Thickness; _lineStyle = spec.LineStyle; _visible = spec.Visible;
        _shadeMode = spec.Shading.Mode; _shadeOther = spec.Shading.OtherCurve; _shadeColor = spec.Shading.Color;
        _shadeOpacity = spec.Shading.Opacity; _ramp = spec.Shading.Ramp;
    }

    readonly string _source;
    /// <summary>Curve id (rt:/off:/syn:/pzs: prefix + name).</summary>
    public string Source => _source;
    public string Display => Ref?.Display ?? (_source + " (not in project)");
    public string Name => Ref?.Name ?? _source;
    public string? Unit => Ref?.Unit;
    public bool Missing => Ref == null;

    string _color; double _thickness; LineStyleKind _lineStyle; bool _visible;
    ShadingMode _shadeMode; string? _shadeOther; string _shadeColor; double _shadeOpacity; ColorRampKind _ramp;

    public string Color { get => _color; set => Set(ref _color, value); }
    public double Thickness { get => _thickness; set => Set(ref _thickness, Math.Clamp(value, 0.25, 12)); }
    public LineStyleKind LineStyle { get => _lineStyle; set => Set(ref _lineStyle, value); }
    public bool Visible { get => _visible; set => Set(ref _visible, value); }
    public ShadingMode ShadeMode { get => _shadeMode; set { if (Set(ref _shadeMode, value)) OnPropertyChanged(nameof(NeedsOtherCurve)); } }
    public string? ShadeOther { get => _shadeOther; set => Set(ref _shadeOther, value); }
    public string ShadeColor { get => _shadeColor; set => Set(ref _shadeColor, value); }
    public double ShadeOpacity { get => _shadeOpacity; set => Set(ref _shadeOpacity, Math.Clamp(value, 0, 1)); }
    public ColorRampKind Ramp { get => _ramp; set => Set(ref _ramp, value); }
    public bool NeedsOtherCurve => _shadeMode == ShadingMode.ToCurve;

    public CurveSpec ToSpec() => new()
    {
        Source = Source, Color = Color, Thickness = Thickness, LineStyle = LineStyle, Visible = Visible,
        Shading = new ShadingSpec { Mode = ShadeMode, OtherCurve = ShadeOther, Color = ShadeColor, Opacity = ShadeOpacity, Ramp = Ramp },
    };

    public override string ToString() => Display;
}

/// <summary>A vertical (TVT) or horizontal (vertical-section) log track.</summary>
public sealed class TrackViewModel : ObservableObject
{
    public TrackViewModel(TrackSpec spec)
    {
        _title = spec.Title; Orientation = spec.Orientation; _size = spec.Size;
        _scaleMin = spec.ScaleMin; _scaleMax = spec.ScaleMax; _log = spec.LogScale;
        Curves.CollectionChanged += Curves_CollectionChanged;
    }

    void Curves_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (TrackCurveViewModel c in e.NewItems) c.PropertyChanged += Curve_PropertyChanged;
        if (e.OldItems != null) foreach (TrackCurveViewModel c in e.OldItems) c.PropertyChanged -= Curve_PropertyChanged;
        OnPropertyChanged(nameof(Curves));
        OnPropertyChanged(nameof(Summary));
    }

    void Curve_PropertyChanged(object? sender, PropertyChangedEventArgs e) => CurveChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised when any curve style in this track changes.</summary>
    public event EventHandler? CurveChanged;

    public TrackOrientation Orientation { get; }
    public bool IsVertical => Orientation == TrackOrientation.Vertical;
    public ObservableCollection<TrackCurveViewModel> Curves { get; } = new();

    string _title; double _size, _scaleMin, _scaleMax; bool _log;
    public string Title { get => _title; set { if (Set(ref _title, value)) OnPropertyChanged(nameof(Summary)); } }
    public double Size { get => _size; set => Set(ref _size, Math.Clamp(value, 60, 1200)); }
    public double ScaleMin { get => _scaleMin; set => Set(ref _scaleMin, value); }
    public double ScaleMax { get => _scaleMax; set => Set(ref _scaleMax, value); }
    public bool LogScale { get => _log; set => Set(ref _log, value); }

    public string Summary => $"{(IsVertical ? "Vertical" : "Horizontal")}: {Title} ({Curves.Count} curve{(Curves.Count == 1 ? "" : "s")})";

    /// <summary>Effective scale for drawing: log scales need positive limits.</summary>
    public (double min, double max) EffectiveScale()
    {
        double a = ScaleMin, b = ScaleMax;
        if (LogScale)
        {
            if (a <= 0) a = 0.1;
            if (b <= a) b = a * 1000;
        }
        else if (b == a) b = a + 1;
        return (a, b);
    }

    /// <summary>Fraction 0..1 across the track for a value (unclamped; NaN for invalid log values).</summary>
    public double Fraction(double v)
    {
        var (a, b) = EffectiveScale();
        if (LogScale)
        {
            if (!(v > 0)) return double.NaN;
            return (Math.Log10(v) - Math.Log10(a)) / (Math.Log10(b) - Math.Log10(a));
        }
        return (v - a) / (b - a);
    }

    public TrackSpec ToSpec() => new()
    {
        Title = Title, Orientation = Orientation, Size = Size, ScaleMin = ScaleMin, ScaleMax = ScaleMax, LogScale = LogScale,
        Curves = Curves.Select(c => c.ToSpec()).ToList(),
    };

    public override string ToString() => Summary;
}

/// <summary>A bed boundary: imported (from the PZS model, style-only) or user-defined (stratigraphic offset from the reference bed).</summary>
public sealed class BedBoundaryViewModel : ObservableObject
{
    public BedBoundaryViewModel(BedBoundarySpec s, bool isGrid = false)
    {
        _name = s.Name; Kind = s.Kind; _offset = s.Offset; _color = s.Color; _thickness = s.Thickness;
        _visible = s.Visible; _showLabel = s.ShowLabel; IsGridBed = isGrid;
    }

    public BedKind Kind { get; }
    public bool IsUser => Kind == BedKind.User;
    public bool IsImported => Kind == BedKind.Imported;
    /// <summary>True for a gridded model bed (as opposed to a marker pick).</summary>
    public bool IsGridBed { get; }
    public string KindText => IsUser ? "User" : IsGridBed ? "Imported grid bed" : "Imported marker";

    string _name; double _offset; string _color; double _thickness; bool _visible, _showLabel;
    double _offsetTvt = double.NaN;

    public string Name { get => _name; set { if (IsUser) Set(ref _name, value); } }
    /// <summary>User beds: TVT offset below the reference bed (positive down).</summary>
    public double Offset { get => _offset; set { if (IsUser && Set(ref _offset, value)) { OnPropertyChanged(nameof(Tvt)); OnPropertyChanged(nameof(TvtText)); } } }
    public string Color { get => _color; set => Set(ref _color, value); }
    public double Thickness { get => _thickness; set => Set(ref _thickness, Math.Clamp(value, 0.25, 12)); }
    public bool Visible { get => _visible; set => Set(ref _visible, value); }
    public bool ShowLabel { get => _showLabel; set => Set(ref _showLabel, value); }

    /// <summary>Position on the TVT axis of the tracks: user beds = Offset, imported beds = TVT at the offset well.</summary>
    public double Tvt { get => IsUser ? _offset : _offsetTvt; }
    internal void SetImportedTvt(double v) { _offsetTvt = v; OnPropertyChanged(nameof(Tvt)); OnPropertyChanged(nameof(TvtText)); }
    public string TvtText => double.IsNaN(Tvt) ? "" : Tvt.ToString("0.##");

    public BedBoundarySpec ToSpec() => new()
    {
        Name = Name, Kind = Kind, Offset = IsUser ? Offset : 0, Color = Color, Thickness = Thickness, Visible = Visible, ShowLabel = ShowLabel,
    };
}
