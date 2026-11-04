using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using CodeSwitchX.UI.Tests.Infrastructure;
using CodeSwitchX.UI.Tests.Yard;

namespace CodeSwitchX.UI.Tests.Telemetry;

/// <summary>The bar as its markup lays it out, on the application's resources (#370).</summary>
public sealed class PerformanceBarLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static Grid Bar(bool hasBudget)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "CodeSwitchX.UI", "App.xaml")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var root = XDocument.Load(Path.Combine(dir.FullName, "src", "CodeSwitchX.UI", "Telemetry", "PerformanceBarView.xaml")).Root!;
        // The voice lamps need resources of their own, and sit at the far right: not what is measured here.
        root.Descendants().Where(e => e.Name.LocalName == "ModelLampView").ToList().ForEach(e => e.Remove());
        var grid = new XElement(Wpf + "Grid", root.Attributes().Where(a => a.IsNamespaceDeclaration),
            new XElement(Wpf + "Grid.Resources", GitPillsXamlTests.AppResourcesXaml()), root.Element(Wpf + "Border"));
        var xaml = GitPillsXamlTests.AssemblyQualified(grid.ToString())
            .Replace("\"clr-namespace:CodeSwitchX.UI.Voice\"", "\"clr-namespace:CodeSwitchX.UI.Voice;assembly=CodeSwitchX\"", StringComparison.Ordinal)
            .Replace("\"clr-namespace:CodeSwitchX.UI.Settings\"", "\"clr-namespace:CodeSwitchX.UI.Settings;assembly=CodeSwitchX\"", StringComparison.Ordinal);
        var bar = (Grid)XamlReader.Parse(xaml);
        bar.DataContext = new BarData(hasBudget);
        bar.Width = 2400;
        bar.Height = 40;
        bar.Measure(new Size(2400, 40));
        bar.Arrange(new Rect(0, 0, 2400, 40));
        bar.UpdateLayout();
        return bar;
    }

    /// <summary>What the bar binds to; a class, since its toggle writes IsExpanded back.</summary>
    private sealed class BarData(bool hasBudget)
    {
        public bool IsExpanded { get; set; } = true;

        public string FiveHourText => "40.9M";

        public double FiveHourPercent => 50.0;

        public bool HasBudget => hasBudget;

        public object Voice => new { SpeechName = "voice · Kokoro", ListeningName = "speech to text · Whisper Tiny" };

        public object Limits => new { FiveHourText = "5h 13% · resets in 3h 36m", WeekText = "week 47% · resets Mon 06:00", Detail = "detail" };
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child))
            {
                yield return deeper;
            }
        }
    }

    private static (double Left, double Right) Span(Grid bar, FrameworkElement element)
    {
        var left = element.TranslatePoint(new Point(0, 0), bar).X;
        return (left, left + element.ActualWidth);
    }

    private static TextBlock Text(Grid bar, string text) => Descendants(bar).OfType<TextBlock>().Single(t => t.Text == text);

    private const string ThemeText = "TextFillColorPrimaryBrush";

    private static double Brightness(Brush brush) => ((SolidColorBrush)brush).Color switch
    {
        var c => ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255,
    };

    /// <summary>The style replaces the theme's button style, so without a colour of its own its text falls back to the system's black (#377).</summary>
    [Fact]
    public async Task The_model_names_take_the_theme_s_text_colour()
    {
        await StaThread.RunAsync(() =>
        {
            var bar = Bar(hasBudget: false);
            var light = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            bar.Resources[ThemeText] = light;
            bar.UpdateLayout();

            Text(bar, "voice · Kokoro").Foreground.ShouldBe(light);
            Text(bar, "speech to text · Whisper Tiny").Foreground.ShouldBe(light);
        });
    }

    [Fact]
    public async Task The_dark_theme_s_text_colour_is_light()
    {
        await StaThread.RunAsync(() =>
        {
#pragma warning disable WPF0001 // the API App.xaml sets the same way; still marked for evaluation
            var window = new Window { ThemeMode = ThemeMode.Dark };
#pragma warning restore WPF0001

            Brightness((Brush)window.FindResource(ThemeText)).ShouldBeGreaterThan(0.7);
        });
    }

    [Fact]
    public async Task Without_a_budget_the_counted_figure_and_the_real_limit_stand_apart()
    {
        await StaThread.RunAsync(() =>
        {
            var bar = Bar(hasBudget: false);

            var gap = Span(bar, Text(bar, "5h 13% · resets in 3h 36m")).Left - Span(bar, Text(bar, "40.9M")).Right;

            gap.ShouldBe(20, 0.5, "the two 5-hour measures must not read as one value");
        });
    }

    [Fact]
    public async Task With_a_budget_the_progress_bar_sits_by_its_figure_and_apart_from_the_real_limit()
    {
        await StaThread.RunAsync(() =>
        {
            var bar = Bar(hasBudget: true);
            var progress = Descendants(bar).OfType<System.Windows.Controls.ProgressBar>().Single();

            (Span(bar, progress).Left - Span(bar, Text(bar, "40.9M")).Right).ShouldBe(6, 0.5);
            (Span(bar, Text(bar, "5h 13% · resets in 3h 36m")).Left - Span(bar, progress).Right).ShouldBe(20, 0.5);
        });
    }
}
