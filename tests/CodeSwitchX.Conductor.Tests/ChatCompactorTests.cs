using CodeSwitchX.Core.Yard;
using LittleGod.Llm.Abstractions;
using LittleGod.Llm.ClaudeCli;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Conductor.Tests;

public sealed class ChatCompactorTests
{
    private const string Chat = "0b7e4f7a-3c55-4c39-9a51-2f1d6f0e8a11";

    /// <summary>The chat's folder: one that exists.</summary>
    private static readonly string Folder = AppContext.BaseDirectory;

    private readonly FakeChatClient _claude = new();
    private readonly FakeTimeProvider _time = new();

    private ChatCompactor Compactor() => new(_claude, _time, NullLogger<ChatCompactor>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task CompactAsync(string? keep = null) => Compactor().CompactAsync(Chat, Folder, keep, Ct);

    [Fact]
    public async Task Claude_Code_compacts_the_conversation_in_the_chat_s_folder_as_VS_Code_s_own()
    {
        await CompactAsync("keep the test plan");

        var (messages, options) = _claude.Calls.ShouldHaveSingleItem();
        messages.ShouldHaveSingleItem().Text.ShouldBe("/compact keep the test plan", "the command alone: it must be the whole prompt");
        options.ConversationId.ShouldBe(Chat);
        options.ModelId.ShouldBeNull("the chat's own model writes the summary, as VS Code's /compact would");
        var claude = options.GetClaudeCli().ShouldNotBeNull();
        claude.ResumeOnly.ShouldBeTrue("a chat whose conversation is gone is not started again as a new one (#324)");
        claude.OmitModel.ShouldBeTrue();
        claude.WorkingDirectory.ShouldBe(Folder, "Claude Code finds a conversation by the folder it runs in");
        claude.Environment.ShouldNotBeNull()["CLAUDE_CODE_ENTRYPOINT"].ShouldBe("claude-vscode", "so VS Code keeps the chat in its session list");
        claude.ExtraArguments.ShouldBe(["--settings", """{"disableAllHooks":true}"""], "its hooks would tell CodeSwitchX that the chat ended");
    }

    [Fact]
    public void Claude_Code_runs_with_the_user_s_settings_its_own_prompt_and_no_other_flags()
    {
        var options = ChatCompactor.Options();

        options.Isolation.ShouldBe(ClaudeIsolation.Custom, "no tool or MCP flags, as before #324: only hooks are off");
        options.DefaultSystemPrompt.ShouldBeNull("a resumed chat keeps Claude Code's own prompt");
        options.KeepApiKeyEnvironment.ShouldBeTrue("as before #324");
    }

    [Fact]
    public async Task A_chat_is_named_as_rename_in_its_tab_would()
    {
        _claude.Command = "rename";

        await Compactor().NameAsync(Chat, Folder, "Fix the\nupload", Ct);

        _claude.Calls.ShouldHaveSingleItem().Messages.ShouldHaveSingleItem().Text.ShouldBe("/rename Fix the upload");
    }

    [Theory]
    [InlineData(null, "/compact")]
    [InlineData("  ", "/compact")]
    [InlineData("keep\nthe   plan ", "/compact keep the plan")]
    public void What_to_keep_goes_on_the_command_s_line(string? keep, string command) => ChatCompactor.Command(keep).ShouldBe(command);

    [Fact]
    public async Task A_chat_whose_conversation_is_gone_says_so()
    {
        _claude.Failure = new ClaudeCliSessionGoneException(Chat, "No conversation found with session ID: " + Chat);

        (await Should.ThrowAsync<YardActionException>(() => CompactAsync())).Message
            .ShouldBe("Claude Code did not compact the chat: it no longer has the chat's conversation.");
    }

    [Fact]
    public async Task A_chat_whose_conversation_is_gone_logs_Claude_Code_s_own_reason()
    {
        var log = new ListLogger();
        _claude.Failure = new ClaudeCliSessionGoneException(Chat, "No conversation found with session ID: " + Chat);

        await Should.ThrowAsync<YardActionException>(() => new ChatCompactor(_claude, _time, log).CompactAsync(Chat, Folder, null, Ct));

        log.Lines.ShouldContain(l => l.Contains("No conversation found with session ID"), "the package says gone for any failure before the resume starts");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_chat_in_no_known_folder_is_not_resumed_somewhere_else(string folder)
    {
        (await Should.ThrowAsync<YardActionException>(() => Compactor().NameAsync(Chat, folder, "Fix", Ct))).Message
            .ShouldBe("CodeSwitchX does not know the folder the chat runs in, so it cannot name it.");
        _claude.Calls.ShouldBeEmpty("the package would resume it in its own folder, where Claude Code finds no conversation");
    }

    [Fact]
    public async Task A_chat_whose_folder_is_gone_says_so()
    {
        var gone = Path.Combine(Folder, "gone-" + Guid.NewGuid().ToString("N"));

        (await Should.ThrowAsync<YardActionException>(() => Compactor().CompactAsync(Chat, gone, null, Ct))).Message
            .ShouldBe($"The folder the chat runs in, {gone}, no longer exists, so CodeSwitchX cannot compact the chat.");
        _claude.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_name_Claude_Code_refuses_says_so()
    {
        _claude.Failure = new LlmException("error: something broke", ClaudeCliChatClient.ProviderKey);

        (await Should.ThrowAsync<YardActionException>(() => Compactor().NameAsync(Chat, Folder, "Fix", Ct))).Message
            .ShouldBe("Claude Code did not name the chat: error: something broke");
    }

    [Theory]
    [InlineData("Not enough messages to compact.", "Claude Code did not compact the chat: Not enough messages to compact.")]
    [InlineData("API Error: 529 Overloaded", "Claude Code did not compact the chat: API Error: 529 Overloaded")]
    [InlineData("The Claude CLI exited with code 1 without saying why.", "Claude Code did not compact the chat: The Claude CLI exited with code 1 without saying why.")]
    [InlineData("The Claude CLI call was interrupted.", "Claude Code did not compact the chat: The Claude CLI call was interrupted.")]
    [InlineData("Error: spawn failed\n    at resume (cli.js:10:3)\n    at main (cli.js:2:1)", "Claude Code did not compact the chat: Error: spawn failed")]
    [InlineData("\n  Not enough messages to compact.\n", "Claude Code did not compact the chat: Not enough messages to compact.")]
    [InlineData(" \n  \n", "Claude Code did not compact the chat: it gave no reason.")]
    public async Task A_compaction_Claude_Code_refuses_says_why_in_its_first_line(string reason, string said)
    {
        _claude.Failure = new LlmException(reason, ClaudeCliChatClient.ProviderKey);

        (await Should.ThrowAsync<YardActionException>(() => CompactAsync())).Message.ShouldBe(said);
    }

    [Fact]
    public async Task No_Claude_Code_is_said()
    {
        _claude.Failure = new LlmUnavailableException("Claude Code is not installed.", ClaudeCliChatClient.ProviderKey, LlmReadinessState.NotSetUp);

        (await Should.ThrowAsync<YardActionException>(() => CompactAsync())).Message.ShouldBe("Claude Code did not compact the chat: Claude Code is not installed.");
    }

    [Fact]
    public async Task An_answer_instead_of_the_command_is_no_compaction()
    {
        _claude.Command = null; // the model answered "/compact" as a question

        (await Should.ThrowAsync<YardActionException>(() => CompactAsync())).Message
            .ShouldBe("Claude Code did not compact the chat: it answered the command as a question instead of running it.");
    }

    [Fact]
    public async Task A_compaction_that_takes_too_long_is_stopped()
    {
        _claude.Hangs = true;

        var compact = CompactAsync();
        for (var i = 0; i < 100 && !compact.IsCompleted; i++)
        {
            await Task.Delay(1, Ct);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        (await Should.ThrowAsync<YardActionException>(() => compact)).Message
            .ShouldBe("Compacting the chat took longer than 5 minutes and was stopped; it is as it was.");
        _claude.Cancelled.ShouldBeTrue("the package kills its process on cancellation");
    }

    [Fact]
    public async Task A_compaction_the_caller_stops_is_cancelled_not_failed()
    {
        _claude.Hangs = true;
        using var stop = new CancellationTokenSource();

        var compact = Compactor().CompactAsync(Chat, Folder, null, stop.Token);
        await stop.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => compact);
    }

    /// <summary>Keeps each line logged, with its exception's message.</summary>
    private sealed class ListLogger : ILogger<ChatCompactor>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception.Message));
    }

    /// <summary>The package's client as the compactor sees it: it answers each call as a slash command would.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions Options)> Calls { get; } = [];

        /// <summary>The command the answer names under <see cref="ClaudeCliKeys.LocalCommand"/>; null for a model's answer.</summary>
        public string? Command { get; set; } = "compact";

        public Exception? Failure { get; set; }

        /// <summary>Whether a call waits until it is cancelled.</summary>
        public bool Hangs { get; set; }

        public bool Cancelled { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(([.. messages], options.ShouldNotBeNull()));
            if (Hangs)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    throw;
                }
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, Command is null ? "Compacting means…" : ""));
            if (Command is not null)
            {
                response.AdditionalProperties = new() { [ClaudeCliKeys.LocalCommand] = Command };
            }

            return response;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("The compactor waits for the whole response.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
