using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Tests.Sessions;

public class WorkSessionsTests
{
    private static readonly DateTimeOffset Morning = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    private static UsageBucket At(string chat, DateTimeOffset minute) => new() { SessionId = chat, Model = "m", MinuteUtc = minute, Output = 1 };

    private static IEnumerable<UsageBucket> Minutes(string chat, DateTimeOffset from, int count) =>
        Enumerable.Range(0, count).Select(i => At(chat, from.AddMinutes(i)));

    [Fact]
    public void While_the_user_works_the_last_session_is_the_one_before()
    {
        // Yesterday 9:00-9:30 (a) and, after a lunch hour, 10:30-10:40 (b); today since 8:50 (c), going on.
        var buckets = Minutes("a", Morning, 30).Concat(Minutes("b", Morning.AddMinutes(90), 10)).Concat(Minutes("c", Morning.AddHours(23).AddMinutes(50), 10));

        var last = WorkSessions.Last(buckets, _ => true, Morning.AddDays(1).AddMinutes(5)).ShouldNotBeNull();

        last.Start.ShouldBe(Morning);
        last.End.ShouldBe(Morning.AddMinutes(100));
        last.MinutesByChat.ShouldBe(new Dictionary<string, int> { ["a"] = 30, ["b"] = 10 }, ignoreOrder: true);
    }

    [Fact]
    public void After_a_break_the_last_session_is_the_latest()
    {
        var buckets = Minutes("a", Morning, 30).Concat(Minutes("b", Morning.AddDays(1), 5));

        WorkSessions.Last(buckets, _ => true, Morning.AddDays(1).AddHours(5)).ShouldNotBeNull().MinutesByChat.Keys.ShouldBe(["b"]);
    }

    [Fact]
    public void A_break_of_four_hours_idle_ends_a_session_and_one_minute_less_does_not()
    {
        // The last minute with work is 9:00-9:01: idle from 9:01.
        var exactly = new[] { At("a", Morning), At("b", Morning.AddHours(4).AddMinutes(1)) };
        var less = new[] { At("a", Morning), At("b", Morning.AddHours(4)) };
        var later = Morning.AddDays(1);

        WorkSessions.Last(exactly, _ => true, later).ShouldNotBeNull().MinutesByChat.Keys.ShouldBe(["b"]);
        WorkSessions.Last(less, _ => true, later).ShouldNotBeNull().MinutesByChat.Count.ShouldBe(2);
    }

    [Fact]
    public void Minutes_that_do_not_count_and_a_lone_session_going_on_give_none()
    {
        var buckets = Minutes("raven", Morning, 30).Concat(Minutes("a", Morning.AddDays(1), 5));

        WorkSessions.Last(buckets, id => id != "raven", Morning.AddDays(1).AddMinutes(10)).ShouldBeNull("only today's session counts, and it goes on");
        WorkSessions.Last([], _ => true, Morning).ShouldBeNull();
    }
}
