using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SaveFileSync.Core.Service;

namespace SaveFileSync.App;

public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly Dictionary<UnitState, SolidColorBrush> Brushes = new()
    {
        [UnitState.Synced] = Make(0x2E, 0x9E, 0x5B),
        [UnitState.Waiting] = Make(0x2F, 0x7D, 0xD1),
        [UnitState.Syncing] = Make(0x2F, 0x7D, 0xD1),
        [UnitState.Pending] = Make(0x2F, 0x7D, 0xD1),
        [UnitState.GameRunning] = Make(0x6B, 0x6F, 0xD6),
        [UnitState.Stale] = Make(0xD9, 0x82, 0x1E),
        [UnitState.Conflict] = Make(0xD9, 0x82, 0x1E),
        [UnitState.Attention] = Make(0xD9, 0x82, 0x1E),
        [UnitState.Error] = Make(0xC4, 0x3B, 0x3B),
        [UnitState.RepositoryUnavailable] = Make(0xC4, 0x3B, 0x3B),
        [UnitState.LocationMissing] = Make(0xC4, 0x3B, 0x3B),
        [UnitState.Paused] = Make(0x70, 0x70, 0x70),
        [UnitState.NotSetUp] = Make(0x70, 0x70, 0x70),
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is UnitState s && Brushes.TryGetValue(s, out var brush) ? brush : Brushes[UnitState.Paused];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class StateToTextConverter : IValueConverter
{
    public static string Text(UnitState state) => state switch
    {
        UnitState.Pending => "Starting",
        UnitState.Synced => "Synced",
        UnitState.Waiting => "Waiting",
        UnitState.GameRunning => "Game running",
        UnitState.Stale => "Newer save available",
        UnitState.Syncing => "Syncing",
        UnitState.Conflict => "Conflict",
        UnitState.Attention => "Needs attention",
        UnitState.Error => "Error",
        UnitState.Paused => "Paused",
        UnitState.NotSetUp => "Not set up on this PC",
        UnitState.LocationMissing => "Folder missing",
        UnitState.RepositoryUnavailable => "Repository unavailable",
        _ => state.ToString(),
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is UnitState s ? Text(s) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
