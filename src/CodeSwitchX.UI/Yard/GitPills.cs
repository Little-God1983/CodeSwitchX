using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// Whether the git lines below an element show their issue and pull request pills (#335). The tile's lines set it; Raven's
/// chat header uses the same template and leaves it unset, so it shows no pills.
/// </summary>
public static class GitPills
{
    public static readonly DependencyProperty IsShownProperty = DependencyProperty.RegisterAttached(
        "IsShown", typeof(bool), typeof(GitPills), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsShown(DependencyObject element) => (bool)element.GetValue(IsShownProperty);

    public static void SetIsShown(DependencyObject element, bool value) => element.SetValue(IsShownProperty, value);
}

/// <summary>
/// A GitHub label's colour (the hex without the #) as a brush for its chip in the preview: the text in the colour, the border
/// at 40 % of it and the background at 13 %. The parameter says which: "text", "border" or anything else for the background.
/// </summary>
public sealed class GitHubLabelBrushConverter : IValueConverter
{
    private static readonly Brush Fallback = Freeze(Colors.Gray, 1);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return Fallback;
        }

        var color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return Freeze(color, (parameter as string) switch { "text" => 1, "border" => 0.4, _ => 0.13 });
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static Brush Freeze(Color color, double alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
