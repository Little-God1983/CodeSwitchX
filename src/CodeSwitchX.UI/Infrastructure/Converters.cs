using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Hosting;
using CodeSwitchX.Telemetry;

namespace CodeSwitchX.UI.Infrastructure;

public sealed class SessionStateToBrushConverter : IValueConverter
{
    public static readonly IReadOnlyDictionary<SessionState, Brush> Brushes = new Dictionary<SessionState, Brush>
    {
        [SessionState.Starting] = Freeze("#60A5FA"),
        [SessionState.Idle] = Freeze("#9CA3AF"),
        [SessionState.Working] = Freeze("#22C55E"),
        [SessionState.Waiting] = Freeze("#F59E0B"),
        [SessionState.Stale] = Freeze("#6B7280"),
        [SessionState.Errored] = Freeze("#EF4444"),
        [SessionState.Ended] = Freeze("#4B5563"),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SessionState state && Brushes.TryGetValue(state, out var brush) ? brush : System.Windows.Media.Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    internal static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            return value is string hex && hex.Length > 0 ? SessionStateToBrushConverter.Freeze(hex) : System.Windows.Media.Brushes.SteelBlue;
        }
        catch (FormatException)
        {
            return System.Windows.Media.Brushes.SteelBlue;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PressureToBrushConverter : IValueConverter
{
    private static readonly Brush Normal = SessionStateToBrushConverter.Freeze("#3B82F6");
    private static readonly Brush Amber = SessionStateToBrushConverter.Freeze("#F59E0B");
    private static readonly Brush Red = SessionStateToBrushConverter.Freeze("#EF4444");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ContextPressure.Red => Red,
        ContextPressure.Amber => Amber,
        _ => Normal,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class HostStateToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HostState.Starting => "Starting…",
        HostState.Running => "VS Code open",
        HostState.Stopped => "Stopped",
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the bound enum's name equals the converter parameter.</summary>
public sealed class EnumEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string name && string.Equals(value.ToString(), name, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
