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

    [Fact]
    public void The_folders_of_the_chats_window_survive_the_record()
    {
        var snapshot = new SessionSnapshot
        {
            SessionId = "s1",
            State = SessionState.Idle,
            StartedAt = DateTimeOffset.UnixEpoch,
            LastEventAt = DateTimeOffset.UnixEpoch,
            StateSince = DateTimeOffset.UnixEpoch,
            WindowFolders = [@"e:\Repos\DiffusionNexus.Installer.SDK", @"e:\Repos\DiffusionNexus.Tools"],
        };

        SessionRecord.FromSnapshot(snapshot).ToSnapshot().WindowFolders.ShouldBe(snapshot.WindowFolders);
        SessionRecord.FromSnapshot(snapshot with { WindowFolders = null }).ToSnapshot().WindowFolders.ShouldBeNull();
    }

    [Fact]
    public void Each_fact_that_shows_a_chat_held_a_conversation_survives_the_record()
    {
        var bare = new SessionSnapshot
        {
            SessionId = "s1",
            State = SessionState.Ended,
            StartedAt = DateTimeOffset.UnixEpoch,
            LastEventAt = DateTimeOffset.UnixEpoch,
            StateSince = DateTimeOffset.UnixEpoch,
        };

        SessionRecord.FromSnapshot(bare).ToSnapshot().HeldConversation.ShouldBeFalse();
        SessionRecord.FromSnapshot(bare with { Title = "fix the build" }).ToSnapshot().HeldConversation.ShouldBeTrue();
        SessionRecord.FromSnapshot(bare with { LatestContext = new TokenUsage(10, 20, 0, 0, 0) }).ToSnapshot().HeldConversation.ShouldBeTrue();
        SessionRecord.FromSnapshot(bare with { LastToolName = "Bash" }).ToSnapshot().HeldConversation.ShouldBeTrue();
    }
}
