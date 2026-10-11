using CodeSwitchX.UI.Yard;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>#276: which group of the Yard a tile is in, by when a chat of its window was last used.</summary>
public class LastUseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, LastUse.Today)]
    [InlineData(23.99, LastUse.Today)]
    [InlineData(24, LastUse.ThisWeek)]
    [InlineData(7 * 24 - 0.01, LastUse.ThisWeek)]
    [InlineData(7 * 24, LastUse.Older)]
    [InlineData(400, LastUse.Older)]
    public void The_group_goes_by_how_many_hours_ago_the_window_was_last_used(double hoursAgo, LastUse group)
    {
        YardGroups.Of(Now - TimeSpan.FromHours(hoursAgo), working: false, waiting: false, needsMeFirst: false, Now).ShouldBe(group);
    }

    [Fact]
    public void A_window_that_never_had_a_chat_is_older()
    {
        YardGroups.Of(null, working: false, waiting: false, needsMeFirst: false, Now).ShouldBe(LastUse.Older);
    }

    [Fact]
    public void A_working_window_is_today_however_long_ago_its_last_event_was()
    {
        YardGroups.Of(Now - TimeSpan.FromDays(30), working: true, waiting: false, needsMeFirst: false, Now).ShouldBe(LastUse.Today);
    }

    [Theory]
    [InlineData(false, LastUse.ThisWeek)]
    [InlineData(true, LastUse.Today)]
    public void A_window_waiting_on_the_user_since_days_is_today_only_with_needs_me_first(bool needsMeFirst, LastUse group)
    {
        YardGroups.Of(Now - TimeSpan.FromDays(3), working: false, waiting: true, needsMeFirst, Now).ShouldBe(group);
    }
}
