using CodeSwitchX.Core.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Conductor.Tests;

public sealed class ChatCompactorTests
{
    private const string Claude = @"C:\Tools\claude.exe";

    /// <summary>What CLI 2.1.294 writes for <c>-p "/compact" --output-format json</c> (captured 2026-10-09, cut down).</summary>
    private const string Compacted =
        """{"is_error":false,"num_turns":0,"session_id":"chat-1","subtype":"success","result":"","local_command":"compact","type":"result"}""";

    private readonly FakeLauncher _launcher = new() { Answer = _ => [], ExitsOnClosedInput = false }; // it ends once it has compacted
    private readonly FakeTimeProvider _time = new();
    private string? _claude = Claude;

    private ChatCompactor Compactor() => new(_launcher, () => _claude, _time, NullLogger<ChatCompactor>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Starts a compaction and lets its process write the lines and exit.</summary>
    private async Task CompactAsync(string? keep, int exit, params string[] lines)
    {
        var compact = Compactor().CompactAsync("chat-1", @"E:\Repos\App", keep, Ct);
        foreach (var line in lines)
        {
            _launcher.Last.Emit(line);
        }

        _launcher.Last.Die(exit);
        await compact;
    }

    [Fact]
    public async Task Claude_Code_compacts_the_conversation_in_the_chat_s_folder_as_VS_Code_s_own()
    {
        await CompactAsync("keep the test plan", 0, Compacted);

        var (exe, arguments, folder, process) = _launcher.Started.ShouldHaveSingleItem();
        exe.ShouldBe(Claude);
        arguments.ShouldBe(["-p", "/compact keep the test plan", "--resume", "chat-1", "--output-format", "json", "--settings", """{"disableAllHooks":true}"""],
            "its hooks would tell CodeSwitchX that the chat ended");
        folder.ShouldBe(@"E:\Repos\App", "Claude Code finds a conversation by the folder it runs in");
        _launcher.Environments[0]!["CLAUDE_CODE_ENTRYPOINT"].ShouldBe("claude-vscode", "so VS Code keeps the chat in its session list");
        process.InputClosed.ShouldBeTrue();
        process.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_chat_is_named_as_rename_in_its_tab_would()
    {
        var name = Compactor().NameAsync("chat-1", @"E:\Repos\App", "Fix the\nupload", Ct);
        _launcher.Last.Emit("""{"type":"result","subtype":"success","is_error":false,"result":"","local_command":"rename","session_id":"chat-1"}""");
        _launcher.Last.Die(0);
        await name;

        _launcher.Started.ShouldHaveSingleItem().Arguments.ShouldBe(["-p", "/rename Fix the upload", "--resume", "chat-1", "--output-format", "json", "--settings", """{"disableAllHooks":true}"""]);
    }

    [Fact]
    public async Task A_name_Claude_Code_refuses_says_so()
    {
        var name = Compactor().NameAsync("chat-1", @"E:\Repos\App", "Fix", Ct);
        _launcher.Last.Die(1);

        (await Should.ThrowAsync<YardActionException>(() => name)).Message.ShouldBe("Claude Code did not name the chat: error: something broke");
    }

    [Theory]
    [InlineData(null, "/compact")]
    [InlineData("  ", "/compact")]
    [InlineData("keep\nthe   plan ", "/compact keep the plan")]
    public void What_to_keep_goes_on_the_command_s_line(string? keep, string command) => ChatCompactor.Command(keep).ShouldBe(command);

    [Fact]
    public async Task A_compaction_Claude_Code_refuses_says_why()
    {
        var failed = """{"type":"result","subtype":"success","is_error":true,"result":"Not enough messages to compact.","session_id":"chat-1"}""";

        (await Should.ThrowAsync<YardActionException>(() => CompactAsync(null, 1, failed))).Message
            .ShouldBe("Claude Code did not compact the chat: Not enough messages to compact.");
    }

    [Fact]
    public async Task A_compaction_that_ended_with_no_result_says_the_last_error_line()
    {
        // A session it cannot find: CLI 2.1.294 writes this to standard error and no JSON at all.
        (await Should.ThrowAsync<YardActionException>(() => CompactAsync(null, 1))).Message
            .ShouldBe("Claude Code did not compact the chat: error: something broke");
    }

    [Fact]
    public async Task A_failed_result_with_errors_only_says_those()
    {
        var failed = """{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["API Error: 529 Overloaded"],"session_id":"chat-1"}""";

        (await Should.ThrowAsync<YardActionException>(() => CompactAsync(null, 1, "not json", failed))).Message
            .ShouldBe("Claude Code did not compact the chat: API Error: 529 Overloaded");
    }

    [Fact]
    public async Task No_Claude_Code_is_said()
    {
        _claude = null;

        (await Should.ThrowAsync<YardActionException>(() => Compactor().CompactAsync("chat-1", @"E:\Repos\App", null, Ct))).Message
            .ShouldStartWith("Claude Code is not installed");
        _launcher.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_compaction_that_takes_too_long_is_stopped()
    {
        var compact = Compactor().CompactAsync("chat-1", @"E:\Repos\App", null, Ct);
        for (var i = 0; i < 100 && !compact.IsCompleted; i++)
        {
            await Task.Delay(1, Ct);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        (await Should.ThrowAsync<YardActionException>(() => compact)).Message
            .ShouldBe("Compacting the chat took longer than 5 minutes and was stopped; it is as it was.");
        _launcher.Last.Disposed.ShouldBeTrue("its process is killed");
    }
}
