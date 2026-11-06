using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodeSwitchX.UI.Telemetry;
using CodeSwitchX.UI.Tests.Infrastructure;

namespace CodeSwitchX.UI.Tests.Telemetry;

/// <summary>A plan limit's bar as it draws (#384).</summary>
public sealed class LimitBarTests
{
    private static readonly Color Fill = Color.FromRgb(0x3B, 0x82, 0xF6);
    private static readonly Color Ahead = Color.FromRgb(0xF5, 0x9E, 0x0B);
    private static readonly Color Mark = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color Track = Color.FromRgb(0x37, 0x41, 0x51);

    /// <summary>The bar drawn 100 wide and 10 high, so a percent is a pixel's x.</summary>
    private static Color[,] Draw(LimitBarState? state)
    {
        var bar = new LimitBar { State = state, Width = 100, Height = 10, MarkBrush = new SolidColorBrush(Mark) };
        bar.Measure(new Size(100, 10));
        bar.Arrange(new Rect(0, 0, 100, 10));
        var bitmap = new RenderTargetBitmap(100, 10, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(bar);
        var pixels = new byte[100 * 10 * 4];
        bitmap.CopyPixels(pixels, 100 * 4, 0);
        var colors = new Color[100, 10];
        for (var x = 0; x < 100; x++)
        {
            for (var y = 0; y < 10; y++)
            {
                var i = ((y * 100) + x) * 4;
                colors[x, y] = Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]);
            }
        }

        return colors;
    }

    [Fact]
    public async Task A_bar_on_pace_fills_in_its_colour_up_to_its_percent_and_marks_the_pace()
    {
        await StaThread.RunAsync(() =>
        {
            var bar = Draw(new LimitBarState(40, 60, "40%"));

            bar[20, 5].ShouldBe(Fill);
            bar[38, 5].ShouldBe(Fill);
            bar[45, 5].ShouldBe(Track);
            bar[60, 0].ShouldBe(Mark, "the mark stands the bar's full height, above the track");
            bar[60, 9].ShouldBe(Mark);
            bar[45, 0].A.ShouldBe((byte)0, "nothing above the track but the mark");
        });
    }

    [Fact]
    public async Task A_bar_spent_past_its_mark_fills_amber()
    {
        await StaThread.RunAsync(() =>
        {
            var bar = Draw(new LimitBarState(54, 46, "54%"));

            bar[20, 5].ShouldBe(Ahead);
            bar[52, 5].ShouldBe(Ahead);
        });
    }

    [Fact]
    public async Task A_bar_without_a_pace_has_no_mark()
    {
        await StaThread.RunAsync(() =>
        {
            var bar = Draw(new LimitBarState(54, null, "54%"));

            bar[20, 5].ShouldBe(Fill);
            Enumerable.Range(0, 100).ShouldAllBe(x => bar[x, 0].A == 0);
        });
    }

    [Fact]
    public async Task Without_a_state_only_the_track_shows()
    {
        await StaThread.RunAsync(() =>
        {
            var bar = Draw(null);

            bar[50, 5].ShouldBe(Track);
        });
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(50, 50)]
    [InlineData(130, 100)] // a figure over the limit fills the bar, no more
    public void The_fill_stays_on_the_bar(double percent, double width) =>
        LimitBar.FillWidth(new LimitBarState(percent, null, ""), 100).ShouldBe(width);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 49)]
    [InlineData(100, 98)] // the whole window passed: the mark stays inside the bar's end
    public void The_mark_is_centred_on_its_pace_and_stays_on_the_bar(double pace, double left) =>
        LimitBar.MarkLeft(new LimitBarState(0, pace, ""), 100).ShouldBe(left);

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)] // the BenQ's scaling
    [InlineData(1.5)]
    public void The_mark_sits_on_a_whole_device_pixel(double scale)
    {
        var left = LimitBar.MarkLeft(new LimitBarState(0, 139 / 300.0 * 100, ""), 90, scale)!.Value;

        (left * scale).ShouldBe(Math.Round(left * scale), 1e-9);
        left.ShouldBe((90 * 139 / 300.0) - 1, 1 / scale, "within a device pixel of the pace");
    }
}
