using CodeSwitchX.Core.Sessions;
using LittleGod.Llm.Abstractions;
using LittleGod.Llm.ClaudeCli;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Conductor.Tests;

public sealed class KeepAliveSenderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "csx-keepwarm-" + Guid.NewGuid().ToString("N"), "keepwarm");
    private readonly FakeChatClient _claude = new();

    private KeepAliveSender Sender() => new(_claude, _folder, NullLogger<KeepAliveSender>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(_folder)))
        {
            Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true);
        }
    }

    [Fact]
    public async Task The_ping_is_sent_to_the_chat_by_its_name_word_for_word()
    {
        (await Sender().SendAsync("codeswitchx-52", Ct)).ShouldBeTrue();

        var (messages, _) = _claude.Calls.ShouldHaveSingleItem();
        var prompt = messages.ShouldHaveSingleItem().Text;
        prompt.ShouldContain("\"codeswitchx-52\"");
        prompt.ShouldContain(KeepAlive.Text);
    }

    [Fact]
    public async Task Claude_Code_runs_with_the_send_tool_alone_in_a_folder_of_its_own()
    {
        await Sender().SendAsync("codeswitchx-52", Ct);

        var claude = _claude.Calls.ShouldHaveSingleItem().Options.GetClaudeCli().ShouldNotBeNull();
        claude.Tools.ShouldBe(["SendMessage"]);
        claude.AllowedTools.ShouldBe(["SendMessage"]);
        claude.WorkingDirectory.ShouldBe(_folder, "the chat shows the sender by its folder's name: keepwarm");
        Directory.Exists(_folder).ShouldBeTrue("Claude Code runs only in a folder that is there");
    }

    [Fact]
    public void The_sender_is_a_small_Claude_Code_that_leaves_nothing_behind()
    {
        var options = KeepAliveSender.Options();

        options.Isolation.ShouldBe(ClaudeIsolation.UserSettingsNoHooks, "with hooks it would show as a chat on the Yard");
        options.DefaultModel.ShouldBe("claude-haiku-5-5", "passing a message on needs no more");
        options.DefaultSystemPrompt.ShouldNotBeNullOrWhiteSpace("Claude Code's own prompt would be read, and paid for, at every ping");
        options.PersistSessions.ShouldBeFalse();
        options.WallClockTimeout.ShouldBe(KeepAliveSender.Limit);
    }

    [Theory]
    [InlineData("sent", true)]
    [InlineData("Sent.", true)]
    [InlineData("failed: No agent named codeswitchx-52 is reachable", false)]
    [InlineData("I could not find that session.", false)]
    [InlineData("", false)]
    public async Task What_the_sender_answers_tells_whether_it_was_sent(string answer, bool sent)
    {
        _claude.Answer = answer;

        (await Sender().SendAsync("codeswitchx-52", Ct)).ShouldBe(sent);
    }

    [Fact]
    public async Task A_Claude_Code_that_fails_sent_nothing()
    {
        _claude.Failure = new LlmException("claude exited with code 1", ClaudeCliChatClient.ProviderKey);

        (await Sender().SendAsync("codeswitchx-52", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancelled_send_is_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _claude.Failure = new OperationCanceledException(cancelled.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => Sender().SendAsync("codeswitchx-52", cancelled.Token));
    }

    private sealed class FakeChatClient : IChatClient
    {
        public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions Options)> Calls { get; } = [];

        public string Answer { get; set; } = "sent";

        public Exception? Failure { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(([.. messages], options.ShouldNotBeNull()));
            return Failure is not null ? Task.FromException<ChatResponse>(Failure) : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("The sender waits for the whole response.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
