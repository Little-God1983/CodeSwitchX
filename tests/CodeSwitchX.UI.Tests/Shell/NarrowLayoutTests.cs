using CodeSwitchX.UI.Shell;

namespace CodeSwitchX.UI.Tests.Shell;

/// <summary>#162: on a narrow window Raven's list folds first, then the Settings sidebar, each only as far as the page needs.</summary>
public sealed class NarrowLayoutTests
{
    [Theory]
    [InlineData(1384, true, false, false)] // the window as it opens: room for all
    [InlineData(1314, true, false, false)] // 556 + 260 + 480, the panel's edge and a 17 px scroll bar
    [InlineData(1313, true, true, false)]
    [InlineData(1136, true, true, false)] // 378 + 260 + 480 + 18
    [InlineData(1135, true, true, true)]
    [InlineData(884, true, true, true)] // the narrowest window's content
    [InlineData(884, false, false, false)] // a closed panel leaves the page room
    [InlineData(815, false, false, false)] // 58 + 260 + 480 + 17: a closed panel's edge is inside its width
    [InlineData(814, false, false, true)]
    public void What_folds_for_a_window_this_wide(double width, bool ravenOpen, bool list, bool sidebar) =>
        NarrowLayout.Folds(width, ravenOpen, scrollBar: 17).ShouldBe((list, sidebar));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    public void A_window_not_laid_out_yet_folds_nothing(double width) => NarrowLayout.Folds(width, true, 17).ShouldBe((false, false));

    /// <summary>In the Cab the list folds only when VS Code would be short of its room: a 1080p screen keeps it.</summary>
    [Theory]
    [InlineData(1920, true, false)] // 1080p at 100 %
    [InlineData(1536, true, false)] // 1080p at 125 %
    [InlineData(1517, true, false)] // 556 + 960 and the panel's edge
    [InlineData(1516, true, true)]
    [InlineData(1366, true, true)] // a small laptop
    [InlineData(1280, true, true)] // 1080p at 150 %
    [InlineData(1280, false, false)] // a closed panel has no list to fold
    [InlineData(double.NaN, true, false)] // not laid out yet
    public void What_folds_in_the_cab_for_a_window_this_wide(double width, bool ravenOpen, bool list) =>
        NarrowLayout.FoldsInCab(width, ravenOpen).ShouldBe(list);
}
