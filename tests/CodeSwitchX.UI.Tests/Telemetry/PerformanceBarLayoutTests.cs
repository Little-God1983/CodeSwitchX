using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Telemetry;
using CodeSwitchX.UI.Tests.Infrastructure;
using CodeSwitchX.UI.Tests.Yard;

namespace CodeSwitchX.UI.Tests.Telemetry;

/// <summary>The bar as its markup lays it out, on the application's resources (#370).</summary>
public sealed class PerformanceBarLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly LimitBarState FiveHour = new(54, 46, "54% · 2h 41m");

    private static readonly LimitBarState Week = new(38, 32, "38% · Mon 06:00");

    private static Grid Bar(bool limits = true)
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
            .Replace("\"clr-namespace:CodeSwitchX.UI.Settings\"", "\"clr-namespace:CodeSwitchX.UI.Settings;assembly=CodeSwitchX\"", StringComparison.Ordinal)
            .Replace("\"clr-namespace:CodeSwitchX.UI.Telemetry\"", "\"clr-namespace:CodeSwitchX.UI.Telemetry;assembly=CodeSwitchX\"", StringComparison.Ordinal);
        var bar = (Grid)XamlReader.Parse(xaml);
        bar.DataContext = new BarData(limits);
        bar.Height = 40;
        Lay(bar, 2400);
        return bar;
    }

    /// <summary>What the bar binds to; a class, since its toggle writes IsExpanded back.</summary>
    private sealed class BarData(bool limits)
    {
        public bool IsExpanded { get; set; } = true;

        public string TokensTodayText => "12.3M";

        public string CostTodayText => "$4.20 est.";

        public string FiveHourText => "40.9M";

        public double FiveHourPercent => 50.0;

        public bool HasBudget => true;

        public object Voice => new { SpeechName = "voice · Kokoro", ListeningName = "speech to text · Whisper Tiny" };

        public LimitsData Limits => limits
            ? new LimitsData(FiveHour, Week, "5h 54% · resets in 2h 41m", "week 38% · resets Mon 06:00", "detail")
            : new LimitsData(null, null, null, null, null);
    }

    private sealed record LimitsData(LimitBarState? FiveHourBar, LimitBarState? WeekBar, string? FiveHourText, string? WeekText, string? Detail);

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
            var bar = Bar();
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

    private static void Lay(Grid bar, double width)
    {
        bar.Width = width;
        bar.Measure(new Size(width, 40));
        bar.Arrange(new Rect(0, 0, width, 40));
        bar.UpdateLayout();
    }

    /// <summary>The fold that holds this text.</summary>
    private static GiveWay Holding(Grid bar, string text) => Descendants(bar).OfType<GiveWay>().Single(g => g.Child is TextBlock t && t.Text == text);

    [Fact]
    public async Task The_counted_figures_and_the_sparkline_are_hidden_but_kept()
    {
        await StaThread.RunAsync(() =>
        {
            var bar = Bar();

            foreach (var label in new[] { "tokens today", "cost today", "5-hour window", "rate (60 min)" })
            {
                Text(bar, label).ActualWidth.ShouldBe(0, label);
            }

            Text(bar, "active chats").ActualWidth.ShouldBeGreaterThan(0);
        });
    }

    [Fact]
    public async Task The_limits_come_first_as_bars_then_the_chats()
    {
        await StaThread.RunAsync(() =>
        {
            var bar = Bar();
            var bars = Descendants(bar).OfType<LimitBar>().ToList();

            bars.Count.ShouldBe(2);
            bars[0].State.ShouldBe(FiveHour);
            bars[1].State.ShouldBe(Week);
            FrameworkElement[] order = [Text(bar, "5h"), bars[0], Text(bar, "54% · 2h 41m"), Text(bar, "week"), bars[1], Text(bar, "38% · Mon 06:00"), Text(bar, "active chats")];
            order.Select(e => Span(bar, e).Left).ShouldBeInOrder(SortDirection.Ascending);
            (Span(bar, Text(bar, "week")).Left - Span(bar, Text(bar, "54% · 2h 41m")).Right).ShouldBe(20, 0.5, "the two limits must not read as one");
            (Span(bar, Text(bar, "active chats")).Left - Span(bar, Text(bar, "38% · Mon 06:00")).Right).ShouldBe(20, 0.5);
        });
    }

    [Fact]
    public async Task On_a_narrow_bar_the_week_s_text_folds_before_the_five_hour_one_and_the_bars_stay()
    {
        await StaThread.RunAsync(() =>
        {
            var bar = Bar();
            var row = ((FrameworkElement)Descendants(bar).OfType<GiveWayBar>().Single().Child).DesiredSize.Width;
            var names = Descendants(bar).OfType<GiveWay>().Where(g => g.Step == 1).Sum(g => g.Natural);
            var week = Holding(bar, "38% · Mon 06:00");
            var fiveHour = Holding(bar, "54% · 2h 41m");

            Lay(bar, row - names - (week.Natural / 2) + 24); // the border's padding, 12 each side

            week.IsFolded.ShouldBeTrue();
            fiveHour.IsFolded.ShouldBeFalse();

            Lay(bar, 300);

            fiveHour.IsFolded.ShouldBeTrue();
            Descendants(bar).OfType<LimitBar>().ShouldAllBe(b => b.ActualWidth == 90);
        });
    }

    [Fact]
    public async Task Without_figures_the_limits_take_no_room()
    {
        await StaThread.RunAsync(() =>
        {
            var bar = Bar(limits: false);

            Span(bar, Text(bar, "active chats")).Left.ShouldBe(12, 0.5, "right after the border's padding");
        });
    }
}
