using CodeSwitchX.UI.Shell;

namespace CodeSwitchX.UI.Tests.Shell;

/// <summary>#162: on a narrow window Raven's list folds first, then the Settings sidebar, each only as far as the page needs.</summary>
public sealed class NarrowLayoutTests
{
    [Theory]
    [InlineData(1400, true, false, false)] // the window as it opens: room for all
    [InlineData(1296, true, false, false)] // 556 + 260 + 480
    [InlineData(1295, true, true, false)]
    [InlineData(1118, true, true, false)] // 378 + 260 + 480
    [InlineData(1117, true, true, true)]
    [InlineData(900, true, true, true)] // the narrowest the window may be
    [InlineData(900, false, false, false)] // a closed panel leaves the page 582
    [InlineData(797, false, false, true)]
    public void What_folds_for_a_window_this_wide(double width, bool ravenOpen, bool list, bool sidebar) =>
        NarrowLayout.Folds(width, ravenOpen).ShouldBe((list, sidebar));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    public void A_window_not_laid_out_yet_folds_nothing(double width) => NarrowLayout.Folds(width, true).ShouldBe((false, false));
}
