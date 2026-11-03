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
/// A GitHub label's colour (the hex without the #) as a brush for its chip in the preview: the text in the colour, lightened
/// only as far as it takes to read on the dark surface, the border at 40 % of the colour and the background at 13 %. The
/// parameter says which: "text", "border" or anything else for the background.
/// </summary>
public sealed class GitHubLabelBrushConverter : IValueConverter
{
    /// <summary>WCAG AA for text of this size.</summary>
    private const double MinimumContrast = 4.5;

    /// <summary>The lighter of the two surfaces a chip sits on, the hover preview's; the list panel's #262626 is darker, so what reads here reads there.</summary>
    private static readonly double SurfaceLuminance = Luminance(Color.FromRgb(0x26, 0x2B, 0x33));

    private static readonly Brush Fallback = Freeze(Colors.Gray, 1);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return Fallback;
        }

        var color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return (parameter as string) switch
        {
            "text" => Freeze(Readable(color), 1),
            "border" => Freeze(color, 0.4),
            _ => Freeze(color, 0.13),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    /// <summary>The colour itself when it reads on the surface; otherwise the first step towards white that does, which keeps its hue.</summary>
    private static Color Readable(Color color)
    {
        const int Steps = 50;
        for (var step = 0; step < Steps; step++)
        {
            var lighter = TowardsWhite(color, (double)step / Steps);
            if ((Luminance(lighter) + 0.05) / (SurfaceLuminance + 0.05) >= MinimumContrast)
            {
                return lighter;
            }
        }

        return Colors.White;
    }

    private static Color TowardsWhite(Color color, double share)
    {
        static byte Mix(byte channel, double share) => (byte)Math.Round(channel + ((255 - channel) * share));

        return Color.FromRgb(Mix(color.R, share), Mix(color.G, share), Mix(color.B, share));
    }

    /// <summary>WCAG relative luminance.</summary>
    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));
    }

    private static Brush Freeze(Color color, double alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
