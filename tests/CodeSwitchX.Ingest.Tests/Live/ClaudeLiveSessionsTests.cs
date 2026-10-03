using CodeSwitchX.Core;
using CodeSwitchX.Ingest.Live;
using CodeSwitchX.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Ingest.Tests.Live;

public class ClaudeLiveSessionsTests : IDisposable
{
    private const string Issues = "73f0e76c-7f9e-4604-b203-5cf034f93a0e";
    private readonly string _home = Path.Combine(Path.GetTempPath(), "csx-live-" + Guid.NewGuid().ToString("N"));
    private readonly string _sessions;
    private readonly FakeProcesses _processes = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
    private readonly ListLogger<ClaudeLiveSessions> _logger = new();
    private readonly ClaudeLiveSessions _live;

    public ClaudeLiveSessionsTests()
    {
        _sessions = Path.Combine(_home, ".claude", "sessions");
        Directory.CreateDirectory(_sessions);
        _live = new ClaudeLiveSessions(new ClaudeCodePaths(_home), _processes.StartOf, _time, _logger);
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
        _processes.Gone.Add(21688);

        _live.NameOf(Issues).ShouldBeNull();
    }

    [Fact]
    public void Of_two_records_for_a_chat_the_one_whose_process_runs_counts()
    {
        // Reopened in VS Code: the old tab's record is still there, its process is not.
        Record(21688, Issues, "codeswitchx-ea");
        Record(30000, Issues, "codeswitchx-09");
        _processes.Gone.Add(21688);

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

    [Fact]
    public void A_record_whose_process_id_another_process_took_gives_no_name()
    {
        // Killed, and Windows gave its id to a process that started later.
        Record(21688, Issues, "codeswitchx-ea");
        _processes.Started[21688] = FakeProcesses.StartOfEach + ClaudeLiveSessions.StartTolerance;

        _live.NameOf(Issues).ShouldBeNull();
    }

    [Fact]
    public void A_record_whose_process_may_not_be_opened_gives_no_name()
    {
        // A protected or SYSTEM process took the id: a chat of the user's could be opened.
        Record(21688, Issues, "codeswitchx-ea");
        _processes.Started[21688] = null;

        _live.NameOf(Issues).ShouldBeNull();
    }

    [Theory]
    [InlineData("cli", "interactive")]
    [InlineData("sdk-cli", "print")]
    [InlineData("claude-vscode", "print")]
    public void A_chat_run_outside_a_VS_Code_tab_gives_no_name(string entrypoint, string kind)
    {
        // Resumed in a terminal, or by a claude -p: a message would not reach the tab.
        Record(21688, Issues, "codeswitchx-ea", entrypoint: entrypoint, kind: kind);

        _live.NameOf(Issues).ShouldBeNull();
    }

    [Theory]
    [InlineData("procStart")]
    [InlineData("entrypoint")]
    [InlineData("kind")]
    public void A_record_without_what_tells_its_process_is_skipped_and_said_once(string missing)
    {
        string[] fields = [$"\"pid\":21688", $"\"sessionId\":\"{Issues}\"", "\"name\":\"codeswitchx-ea\"",
            $"\"procStart\":\"{FakeProcesses.StartOfEach}\"", "\"entrypoint\":\"claude-vscode\"", "\"kind\":\"interactive\""];
        var record = "{" + string.Join(",", fields.Where(f => !f.StartsWith($"\"{missing}\"", StringComparison.Ordinal))) + "}";
        File.WriteAllText(Path.Combine(_sessions, "21688.json"), record);
        File.WriteAllText(Path.Combine(_sessions, "30000.json"), record.Replace("21688", "30000", StringComparison.Ordinal));

        _live.NameOf(Issues).ShouldBeNull("without it, a chat in a tab cannot be told from another process");
        _time.Advance(ClaudeLiveSessions.MaxAge);
        _live.NameOf(Issues).ShouldBeNull();

        _logger.Entries.Count(e => e.Level == LogLevel.Warning).ShouldBe(1, "a new format is what this looks like: said once, not per record or read");
    }

    [Fact]
    public void A_record_of_a_chat_outside_a_tab_is_not_taken_for_a_new_format()
    {
        Record(21688, Issues, "codeswitchx-ea", entrypoint: "cli");

        _live.NameOf(Issues).ShouldBeNull();
        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void A_process_start_a_little_off_the_records_still_counts()
    {
        // How Claude Code takes the start is not documented: a coarser clock than the process's own must not lose the chat.
        Record(21688, Issues, "codeswitchx-ea");
        _processes.Started[21688] = FakeProcesses.StartOfEach + ClaudeLiveSessions.StartTolerance - 1;

        _live.NameOf(Issues).ShouldBe("codeswitchx-ea");
    }

    [Fact]
    public void Only_the_processes_of_the_chats_asked_for_are_looked_up()
    {
        Record(21688, Issues, "codeswitchx-ea");
        Record(30000, "dad99026-7394-4bab-a46a-acc38f04593e", "codeswitchx-c2");

        _live.NameOf(Issues);
        _live.NameOf(Issues);

        _processes.Asked.ShouldBe([21688], "one look per read, and none for a chat nobody asked about");
    }

    [Fact]
    public void The_process_start_is_read_as_a_number_too()
    {
        File.WriteAllText(Path.Combine(_sessions, "21688.json"),
            $$"""{"pid":21688,"sessionId":"{{Issues}}","kind":"interactive","entrypoint":"claude-vscode","name":"codeswitchx-ea","procStart":{{FakeProcesses.StartOfEach}}}""");

        _live.NameOf(Issues).ShouldBe("codeswitchx-ea");
    }

    [Fact]
    public void The_running_chats_are_read_afresh_each_time_and_only_those_in_VS_Code_tabs_whose_process_runs()
    {
        _live.NameOf(Issues).ShouldBeNull(); // a read that would still serve
        Record(21688, Issues, "codeswitchx-ea");
        Record(30000, "dad99026-7394-4bab-a46a-acc38f04593e", "codeswitchx-c2");
        Record(31000, "e1", "cli-chat", entrypoint: "cli");
        Record(32000, "e2", "gone-chat");
        _processes.Gone.Add(32000);

        _live.RunningNow().OrderBy(c => c.Pid).ShouldBe([
            new LiveChat(21688, Issues, "codeswitchx-ea", FakeProcesses.StartOfEach),
            new LiveChat(30000, "dad99026-7394-4bab-a46a-acc38f04593e", "codeswitchx-c2", FakeProcesses.StartOfEach),
        ]);
    }

    [Fact]
    public void The_processes_a_caller_knows_are_neither_read_nor_looked_up()
    {
        Record(21688, Issues, "codeswitchx-ea");
        Record(30000, "dad99026-7394-4bab-a46a-acc38f04593e", "codeswitchx-c2");

        _live.RunningNow(new HashSet<int> { 21688 }).ShouldHaveSingleItem().Pid.ShouldBe(30000);

        _processes.Asked.ShouldBe([30000]);
    }

    [Fact]
    public void A_fresh_read_of_the_running_chats_leaves_the_names_read_alone()
    {
        // A start waiting for its chat reads every quarter second: the Yard's names must not be read again each time.
        _live.NameOf(Issues).ShouldBeNull();
        Record(21688, Issues, "codeswitchx-ea");

        _live.RunningNow().ShouldHaveSingleItem();

        _live.NameOf(Issues).ShouldBeNull("the names' own read still serves");
    }

    private void Record(int pid, string sessionId, string name, long updatedAt = 1_000, string entrypoint = "claude-vscode", string kind = "interactive",
        string status = "\"status\":\"idle\"") =>
        File.WriteAllText(Path.Combine(_sessions, $"{pid}.json"),
            $$"""{"pid":{{pid}},"sessionId":"{{sessionId}}","cwd":"e:\\Repos\\CodeSwitchX","procStart":"{{FakeProcesses.StartOfEach}}","kind":"{{kind}}","entrypoint":"{{entrypoint}}","name":"{{name}}","updatedAt":{{updatedAt}},{{status}}}""");

    [Theory]
    [InlineData("\"status\":\"waiting\",\"waitingFor\":\"permission prompt\"", true)]
    [InlineData("\"status\":\"busy\"", false)]
    [InlineData("\"status\":\"idle\"", false)]
    [InlineData("\"other\":1", null)]
    public void A_chat_s_tab_shows_a_prompt_while_its_record_waits_on_the_user(string status, bool? shows)
    {
        // As seen with 2.1.287: a permission prompt open in the tab, held by a hook or not, makes the record wait.
        Record(21688, Issues, "codeswitchx-ea", status: status);

        _live.ShowsPrompt(Issues).ShouldBe(shows);
    }

    [Fact]
    public void A_chat_not_open_in_a_tab_tells_nothing_of_its_prompts()
    {
        Record(21688, Issues, "codeswitchx-ea", status: "\"status\":\"waiting\"");
        _processes.Gone.Add(21688);

        _live.ShowsPrompt(Issues).ShouldBeNull();
        _live.ShowsPrompt("not-a-chat").ShouldBeNull();
    }

    /// <summary>Every process started at <see cref="StartOfEach"/>, as its record says, unless set otherwise.</summary>
    private sealed class FakeProcesses
    {
        public const long StartOfEach = 134353964719624928;

        public HashSet<int> Gone { get; } = [];

        public Dictionary<int, long?> Started { get; } = [];

        public List<int> Asked { get; } = [];

        public long? StartOf(int pid)
        {
            Asked.Add(pid);
            return Gone.Contains(pid) ? null : Started.TryGetValue(pid, out var start) ? start : StartOfEach;
        }
    }
}
