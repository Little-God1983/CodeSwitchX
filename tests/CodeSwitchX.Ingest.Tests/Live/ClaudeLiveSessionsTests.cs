using CodeSwitchX.Core;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Ingest.Live;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Ingest.Tests.Live;

public class ClaudeLiveSessionsTests : IDisposable
{
    private const string Issues = "73f0e76c-7f9e-4604-b203-5cf034f93a0e";
    private readonly string _home = Path.Combine(Path.GetTempPath(), "csx-live-" + Guid.NewGuid().ToString("N"));
    private readonly string _sessions;
    private readonly FakeProbe _probe = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
    private readonly ClaudeLiveSessions _live;

    public ClaudeLiveSessionsTests()
    {
        _sessions = Path.Combine(_home, ".claude", "sessions");
        Directory.CreateDirectory(_sessions);
        _live = new ClaudeLiveSessions(new ClaudeCodePaths(_home), _probe, _time);
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void A_running_chat_has_the_name_its_record_gives()
    {
        Record(21688, Issues, "codeswitchx-ea");

        _live.NameOf(Issues).ShouldBe("codeswitchx-ea");
    }

    [Fact]
    public void The_session_id_is_matched_whatever_its_case()
    {
        Record(21688, Issues, "codeswitchx-ea");

        _live.NameOf(Issues.ToUpperInvariant()).ShouldBe("codeswitchx-ea");
    }

    [Fact]
    public void A_chat_without_a_record_has_no_name()
    {
        Record(21688, Issues, "codeswitchx-ea");

        _live.NameOf("00000000-0000-0000-0000-000000000000").ShouldBeNull();
    }

    [Fact]
    public void A_record_whose_process_is_gone_gives_no_name()
    {
        // Claude Code leaves a record behind when it is killed.
        Record(21688, Issues, "codeswitchx-ea");
        _probe.Gone.Add(21688);

        _live.NameOf(Issues).ShouldBeNull();
    }

    [Fact]
    public void Of_two_records_for_a_chat_the_one_whose_process_runs_counts()
    {
        // Reopened in VS Code: the old tab's record is still there, its process is not.
        Record(21688, Issues, "codeswitchx-ea");
        Record(30000, Issues, "codeswitchx-09");
        _probe.Gone.Add(21688);

        _live.NameOf(Issues).ShouldBe("codeswitchx-09");
    }

    [Fact]
    public void Of_two_running_records_for_a_chat_the_newer_counts()
    {
        Record(21688, Issues, "codeswitchx-ea", updatedAt: 1_000);
        Record(30000, Issues, "codeswitchx-09", updatedAt: 2_000);

        _live.NameOf(Issues).ShouldBe("codeswitchx-09");
    }

    [Fact]
    public void A_record_that_cannot_be_read_is_skipped()
    {
        File.WriteAllText(Path.Combine(_sessions, "111.json"), "{ not json");
        File.WriteAllText(Path.Combine(_sessions, "222.json"), "[1, 2]");
        File.WriteAllText(Path.Combine(_sessions, "333.json"), """{"pid":333,"sessionId":"x"}""");
        Record(21688, Issues, "codeswitchx-ea");

        _live.NameOf(Issues).ShouldBe("codeswitchx-ea");
        _live.NameOf("x").ShouldBeNull("a record without a name names nothing");
    }

    [Fact]
    public void Only_the_records_are_read_not_the_key_files_next_to_them()
    {
        Record(21688, Issues, "codeswitchx-ea");
        var key = Path.Combine(_sessions, "21688.ce78ef01.key");
        File.WriteAllText(key, "secret");
        using var held = new FileStream(key, FileMode.Open, FileAccess.Read, FileShare.None);

        _live.NameOf(Issues).ShouldBe("codeswitchx-ea");
    }

    [Fact]
    public void No_sessions_folder_means_no_names()
    {
        Directory.Delete(_sessions, recursive: true);

        _live.NameOf(Issues).ShouldBeNull();
    }

    [Fact]
    public void One_read_serves_a_moment_and_then_the_folder_is_read_again()
    {
        _live.NameOf(Issues).ShouldBeNull();
        Record(21688, Issues, "codeswitchx-ea");

        _live.NameOf(Issues).ShouldBeNull("list_chats asks for every chat at once: one read serves them all");
        _time.Advance(ClaudeLiveSessions.MaxAge);
        _live.NameOf(Issues).ShouldBe("codeswitchx-ea");
    }

    private void Record(int pid, string sessionId, string name, long updatedAt = 1_000) => File.WriteAllText(Path.Combine(_sessions, $"{pid}.json"),
        $$"""{"pid":{{pid}},"sessionId":"{{sessionId}}","cwd":"e:\\Repos\\CodeSwitchX","kind":"interactive","entrypoint":"claude-vscode","name":"{{name}}","updatedAt":{{updatedAt}},"status":"idle"}""");

    private sealed class FakeProbe : IProcessProbe
    {
        public HashSet<int> Gone { get; } = [];

        public bool IsAlive(int pid, DateTimeOffset seenAt) => !Gone.Contains(pid);
    }
}
