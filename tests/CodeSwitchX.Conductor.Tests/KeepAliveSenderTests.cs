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
        options.DefaultModel.ShouldBe(BrainSettings.DefaultModel, "the cheapest, as the brain's: passing a message on needs no more");
        options.DefaultSystemPrompt.ShouldNotBeNullOrWhiteSpace("Claude Code's own prompt would be read, and paid for, at every ping");
        options.PersistSessions.ShouldBeFalse();
        options.WallClockTimeout.ShouldBe(KeepAliveSender.Limit);
    }

    /// <summary>
    /// The sender runs with the user's settings, their output style among them: on screen (2026-11-06) it answered
    /// "**TL;DR:** The keep-alive message was sent to `z-scratch386-54` once." where a bare "sent" was asked for. So it is
    /// asked for a word no style rewrites, and told by that word wherever it stands.
    /// </summary>
    [Theory]
    [InlineData("PING_SENT", true)]
    [InlineData("**TL;DR:** The keep-alive message was sent to `codeswitchx-52` once. PING_SENT", true)]
    [InlineData("ping_sent.", true)]
    [InlineData("PING_FAILED No agent named codeswitchx-52 is reachable", false)]
    [InlineData("**TL;DR:** It was not sent. PING_FAILED: the tool said PING_SENT is not possible", false)]
    [InlineData("**TL;DR:** The keep-alive message was sent to `codeswitchx-52` once.", false)]
    [InlineData("I could not find that session.", false)]
    [InlineData("", false)]
    public async Task What_the_sender_answers_tells_whether_it_was_sent(string answer, bool sent)
    {
        _claude.Answer = answer;

        (await Sender().SendAsync("codeswitchx-52", Ct)).ShouldBe(sent);
    }

    [Fact]
    public void The_sender_is_asked_for_the_word_it_is_told_by()
    {
        var request = KeepAliveSender.Request("codeswitchx-52");

        request.ShouldContain(KeepAliveSender.SentWord);
        request.ShouldContain(KeepAliveSender.FailedWord);
    }

    [Fact]
    public async Task A_Claude_Code_that_fails_sent_nothing()
    {
        _claude.Failure = new LlmException("claude exited with code 1", ClaudeCliChatClient.ProviderKey);

        (await Sender().SendAsync("codeswitchx-52", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_Claude_Code_that_cannot_start_sent_nothing()
    {
        _claude.Failure = new System.ComponentModel.Win32Exception("The system cannot find the file specified");

        (await Sender().SendAsync("codeswitchx-52", Ct)).ShouldBeFalse("the keep-warm check must go on with its other chats");
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

        public string Answer { get; set; } = "PING_SENT";

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
