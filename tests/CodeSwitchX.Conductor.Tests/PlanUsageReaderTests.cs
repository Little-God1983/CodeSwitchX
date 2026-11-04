using CodeSwitchX.Core.Usage;
using LittleGod.Llm.Abstractions;
using LittleGod.Llm.ClaudeCli;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Conductor.Tests;

public sealed class PlanUsageReaderTests
{
    private const string Text = """
        Current session: 54% used · resets Nov 3, 10:39pm (Europe/Berlin)
        Current week (all models): 38% used · resets Nov 9, 6am (Europe/Berlin)
        """;

    private readonly FakeClaude _claude = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly CountingLogger _log = new();

    private PlanUsageReader Reader() => new(_claude, _time, _log);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_limits_are_read_from_usage_with_nothing_saved_and_the_user_s_hooks_left_out()
    {
        _claude.Answer = Text;

        var reading = await Reader().ReadAsync(Ct);

        reading.Kind.ShouldBe(PlanReadingKind.Read);
        reading.Limits!.FiveHour!.Percent.ShouldBe(54);
        reading.Limits.Week!.Percent.ShouldBe(38);
        var (messages, options) = _claude.Calls.ShouldHaveSingleItem();
        messages.ShouldHaveSingleItem().Text.ShouldBe("/usage", "the command alone: it must be the whole prompt");
        options.GetClaudeCli().ShouldNotBeNull().ExtraArguments.ShouldBe(["--setting-sources", "project,local"], "the user's hooks would start a Yard chat, and take longer");
    }

    [Fact]
    public void Claude_Code_runs_so_that_a_slip_stays_cheap_and_nothing_is_kept()
    {
        var options = PlanUsageReader.Options();

        options.Isolation.ShouldBe(ClaudeIsolation.Custom, "only the flags the reading gives");
        options.PersistSessions.ShouldBeFalse("a saved session is a chat of its own");
        options.DefaultModel.ShouldBe("haiku", "if the command ever reached the model, the cheapest one answers");
        options.KeepApiKeyEnvironment.ShouldBeTrue("the limits of what the chats run on: a key has none");
        options.WallClockTimeout.ShouldBe(PlanUsageReader.Limit);
    }

    [Fact]
    public async Task A_text_without_the_limits_gives_no_figures_and_says_so_once_in_the_log()
    {
        _claude.Answer = "Total cost: $0.0000";
        var reader = Reader();

        (await reader.ReadAsync(Ct)).Kind.ShouldBe(PlanReadingKind.NoFigures);
        (await reader.ReadAsync(Ct)).Kind.ShouldBe(PlanReadingKind.NoFigures);

        _log.Warnings.ShouldBe(1);
    }

    [Fact]
    public async Task A_limit_that_can_no_longer_be_read_is_logged_once_though_the_other_stands()
    {
        _claude.Answer = "Current session: 54% used · resets soon (Europe/Berlin)\nCurrent week (all models): 38% used · resets Nov 9, 6am (Europe/Berlin)";
        var reader = Reader();

        var reading = await reader.ReadAsync(Ct);
        await reader.ReadAsync(Ct);

        reading.Kind.ShouldBe(PlanReadingKind.Read);
        reading.Limits!.Week!.Percent.ShouldBe(38);
        _log.Warnings.ShouldBe(1);
    }

    [Fact]
    public async Task A_command_that_reached_the_model_is_called_out_though_its_answer_has_the_lines()
    {
        _claude.Answer = Text;
        _claude.Command = null;

        var reading = await Reader().ReadAsync(Ct);

        reading.Kind.ShouldBe(PlanReadingKind.SpentATurn);
        _log.Warnings.ShouldBe(1);
    }

    [Fact]
    public async Task No_Claude_Code_is_a_failed_reading_logged_only_when_it_starts_failing()
    {
        _claude.Failure = new LlmException("Claude Code was not found.", ClaudeCliChatClient.ProviderKey);
        var reader = Reader();

        (await reader.ReadAsync(Ct)).Kind.ShouldBe(PlanReadingKind.Failed);
        (await reader.ReadAsync(Ct)).Kind.ShouldBe(PlanReadingKind.Failed);
        _log.Warnings.ShouldBe(1);

        _claude.Failure = null;
        _claude.Answer = Text;
        (await reader.ReadAsync(Ct)).Kind.ShouldBe(PlanReadingKind.Read);
        _claude.Failure = new LlmException("Claude Code was not found.", ClaudeCliChatClient.ProviderKey);
        (await reader.ReadAsync(Ct)).Kind.ShouldBe(PlanReadingKind.Failed);
        _log.Warnings.ShouldBe(2);
    }

    [Fact]
    public async Task A_timeout_of_its_own_is_a_failed_reading()
    {
        _claude.Failure = new TimeoutException();

        (await Reader().ReadAsync(Ct)).Kind.ShouldBe(PlanReadingKind.Failed);
    }

    [Fact]
    public async Task A_cancel_by_the_caller_stays_a_cancel()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        _claude.Failure = new OperationCanceledException(stop.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => Reader().ReadAsync(stop.Token));
    }

    private sealed class FakeClaude : IChatClient
    {
        public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions Options)> Calls { get; } = [];

        public string Answer { get; set; } = "";

        /// <summary>The command Claude Code says it ran itself; null when the prompt went to the model.</summary>
        public string? Command { get; set; } = "usage";

        public Exception? Failure { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(([.. messages], options!));
            if (Failure is not null)
            {
                throw Failure;
            }

            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer));
            if (Command is not null)
            {
                response.AdditionalProperties = new AdditionalPropertiesDictionary { [ClaudeCliKeys.LocalCommand] = Command };
            }

            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CountingLogger : ILogger<PlanUsageReader>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }
}
