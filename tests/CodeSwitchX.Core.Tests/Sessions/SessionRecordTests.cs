using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Tests.Sessions;

public class SessionRecordTests
{
    [Fact]
    public void The_latest_context_survives_the_record_with_its_one_hour_cache_writes()
    {
        var snapshot = new SessionSnapshot
        {
            SessionId = "s1",
            State = SessionState.Idle,
            StartedAt = DateTimeOffset.UnixEpoch,
            LastEventAt = DateTimeOffset.UnixEpoch,
            StateSince = DateTimeOffset.UnixEpoch,
            LatestContext = new TokenUsage(100, 20, 300, 3000, CacheWrite1h: 200),
        };

        SessionRecord.FromSnapshot(snapshot).ToSnapshot().LatestContext.ShouldBe(snapshot.LatestContext);
    }
}
