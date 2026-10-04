using System.Text.Json;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Conductor.Tests;

/// <summary>Each Raven chat's own brain (#123): its conversation, kept and picked up again, its tools on its window, and few processes.</summary>
public sealed class ChatBrainsTests : IDisposable
{
    private const string Claude = @"C:\Tools\claude.exe";
    private static readonly Guid Window3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-chatbrains-" + Guid.NewGuid().ToString("N")));
    private readonly BrainSettings _settings = new();
    private readonly FakeLauncher _launcher = new();
    private readonly FakeTimeProvider _time = new();
    private readonly BrainSessionFile _sessions;

    public ChatBrainsTests()
    {
        _paths.EnsureCreated();
        File.WriteAllText(_paths.McpConfigFile, """
            {"mcpServers":{"codeswitchx":{"type":"http","url":"http://127.0.0.1:5000/mcp","headers":{"Authorization":"Bearer secret"}}}}
            """);
        _sessions = new BrainSessionFile(Path.Combine(_paths.RavenDirectory, "sessions.json"));
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private ClaudeCliBrain BrainOf(Guid? window, IBrainSessionStore? sessions = null) => new(_paths, _settings, _launcher, () => Claude, _time,
        NullLogger<ClaudeCliBrain>.Instance, chat: BrainChat.Of(window, sessions ?? _sessions));

    private static async Task AskAsync(IConductorBrain brain, string text)
    {
        await foreach (var _ in brain.AskAsync(text, TestContext.Current.CancellationToken))
        {
        }
    }

    private static string? Value(IReadOnlyList<string> arguments, string option)
    {
        var index = arguments.ToList().IndexOf(option);
        return index < 0 ? null : arguments[index + 1];
    }

    [Fact]
    public async Task A_chat_s_brain_keeps_its_conversation_as_a_session()
    {
        await AskAsync(BrainOf(null), "Hi");

        var arguments = _launcher.Started.ShouldHaveSingleItem().Arguments;
        arguments.ShouldNotContain("--no-session-persistence");
        var id = Value(arguments, "--session-id").ShouldNotBeNull();
        Guid.TryParse(id, out _).ShouldBeTrue();
        _sessions.Load("yard").ShouldNotBeNull().Id.ShouldBe(id);
    }

    [Fact]
    public async Task A_rested_chat_brain_picks_its_conversation_up_again_on_its_next_question()
    {
        var brain = BrainOf(Window3);
        await AskAsync(brain, "Start a chat that fixes the retry");
        var id = Value(_launcher.Started[0].Arguments, "--session-id");

        brain.Rest();
        await WaitUntil(() => _launcher.Started[0].Process.Disposed);
        await AskAsync(brain, "What did I ask you before?");

        _launcher.Started.Count.ShouldBe(2);
        Value(_launcher.Started[1].Arguments, "--resume").ShouldBe(id);
        Value(_launcher.Started[1].Arguments, "--session-id").ShouldBeNull();
    }

    [Fact]
    public async Task A_conversation_is_picked_up_again_after_an_app_restart()
    {
        await AskAsync(BrainOf(Window3), "One");
        var id = Value(_launcher.Started[0].Arguments, "--session-id");
        _time.Advance(TimeSpan.FromMinutes(5));

        var restarted = BrainOf(Window3, new BrainSessionFile(Path.Combine(_paths.RavenDirectory, "sessions.json")));
        await AskAsync(restarted, "Two");

        Value(_launcher.Started[1].Arguments, "--resume").ShouldBe(id);
    }

    [Fact]
    public async Task A_conversation_quiet_for_the_reset_is_not_picked_up_again()
    {
        var brain = BrainOf(Window3);
        await AskAsync(brain, "One");
        var id = Value(_launcher.Started[0].Arguments, "--session-id");
        brain.Rest();
        await WaitUntil(() => _launcher.Started[0].Process.Disposed);

        _time.Advance(ClaudeCliBrain.QuietReset);
        await AskAsync(brain, "Two");

        Value(_launcher.Started[1].Arguments, "--resume").ShouldBeNull();
        Value(_launcher.Started[1].Arguments, "--session-id").ShouldNotBe(id);
    }

    [Fact]
    public async Task A_model_set_while_the_brain_rested_starts_a_new_conversation_and_says_so()
    {
        var brain = BrainOf(Window3);
        await AskAsync(brain, "One");
        brain.Rest();
        await WaitUntil(() => _launcher.Started[0].Process.Disposed);

        _settings.Model = "claude-opus-5-5";
        var events = new List<BrainEvent>();
        await foreach (var e in brain.AskAsync("Two", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        Value(_launcher.Started[1].Arguments, "--resume").ShouldBeNull();
        events.OfType<BrainNotice>().ShouldHaveSingleItem().Text.ShouldContain("claude-opus-5-5");
    }

    [Fact]
    public async Task A_conversation_that_cannot_be_picked_up_again_starts_a_new_one_for_the_next_question()
    {
        var brain = BrainOf(Window3);
        await AskAsync(brain, "One");
        brain.Rest();
        await WaitUntil(() => _launcher.Started[0].Process.Disposed);
        _launcher.Answer = line => []; // the resumed process says nothing and goes, as one does without its session
        var failing = AskAsync(brain, "Two");
        await WaitUntil(() => _launcher.Started.Count == 2 && _launcher.Last.Written.Count == 1);
        _launcher.Last.Die(1);

        var next = new List<BrainEvent>();
        await failing;
        _launcher.Answer = StreamJson.Reply("Hi.");
        await foreach (var e in brain.AskAsync("Three", TestContext.Current.CancellationToken))
        {
            next.Add(e);
        }

        _launcher.Started.Count.ShouldBe(3);
        Value(_launcher.Started[2].Arguments, "--resume").ShouldBeNull("it starts a new conversation");
        Value(_launcher.Started[2].Arguments, "--session-id").ShouldNotBeNull();
        next.OfType<BrainNotice>().ShouldBeEmpty("the failed turn said so already");
    }

    [Fact]
    public async Task A_conversation_claude_code_no_longer_has_fails_the_question_once_and_the_next_starts_a_new_one()
    {
        var brain = BrainOf(Window3);
        await AskAsync(brain, "One");
        brain.Rest();
        await WaitUntil(() => _launcher.Started[0].Process.Disposed);
        _launcher.Answer = _ => [StreamJson.NoConversation];

        var events = new List<BrainEvent>();
        await foreach (var e in brain.AskAsync("Two", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.OfType<BrainFailed>().ShouldHaveSingleItem().Reason.ShouldBe(
            "Raven could not pick this chat's conversation up again, so it starts a new one. Ask again.");
        _launcher.Answer = StreamJson.Reply("Hi.");
        await AskAsync(brain, "Three");
        Value(_launcher.Started[2].Arguments, "--resume").ShouldBeNull();
        _sessions.Load(Window3.ToString("N")).ShouldNotBeNull().Id.ShouldBe(Value(_launcher.Started[2].Arguments, "--session-id"));
    }

    [Fact]
    public async Task A_window_chat_s_tools_name_its_window_in_a_config_of_its_own_and_the_yard_s_use_the_app_s()
    {
        await AskAsync(BrainOf(Window3), "Hi");
        await AskAsync(BrainOf(null), "Hi");

        var own = Value(_launcher.Started[0].Arguments, "--mcp-config").ShouldNotBeNull();
        own.ShouldNotBe(_paths.McpConfigFile);
        own.ShouldStartWith(_paths.RavenDirectory);
        using var config = JsonDocument.Parse(File.ReadAllText(own));
        var server = config.RootElement.GetProperty("mcpServers").GetProperty(YardMcp.ServerName);
        server.GetProperty("url").GetString().ShouldBe("http://127.0.0.1:5000/mcp");
        server.GetProperty("headers").GetProperty("Authorization").GetString().ShouldBe("Bearer secret");
        server.GetProperty("headers").GetProperty(YardMcp.ChatHeader).GetString().ShouldBe(Window3.ToString("D"));
        Value(_launcher.Started[1].Arguments, "--mcp-config").ShouldBe(_paths.McpConfigFile);
    }

    [Fact]
    public async Task The_teller_keeps_no_session_even_when_given_a_chat()
    {
        var teller = new ClaudeCliBrain(_paths, _settings, _launcher, () => Claude, _time, NullLogger<ClaudeCliBrain>.Instance, BrainRole.Teller,
            BrainChat.Of(Window3, _sessions));

        await AskAsync(teller, "News");

        _launcher.Last.ShouldNotBeNull();
        _launcher.Started[0].Arguments.ShouldContain("--no-session-persistence");
        _sessions.Load(Window3.ToString("N")).ShouldBeNull();
    }

    [Fact]
    public void Each_chat_has_one_brain_and_the_yard_its_own()
    {
        var made = new List<Guid?>();
        using var pool = new ChatBrains(window =>
        {
            made.Add(window);
            return new CountingBrain();
        });

        pool.For(Window3).ShouldBeSameAs(pool.For(Window3));
        pool.For(null).ShouldNotBeSameAs(pool.For(Window3));
        made.ShouldBe([Window3, null]);
    }

    [Fact]
    public async Task With_six_chats_in_use_only_the_three_used_last_keep_their_process()
    {
        var brains = new Dictionary<Guid, CountingBrain>();
        using var pool = new ChatBrains(window => brains[window ?? Guid.Empty] = new CountingBrain());
        var windows = Enumerable.Range(1, 6).Select(i => new Guid(i, 0, 0, new byte[8])).ToList();

        foreach (var window in windows)
        {
            await AskAsync(pool.For(window), "Hi");
        }

        windows.Select(w => brains[w].Rests).ShouldBe([1, 1, 1, 0, 0, 0]);
        windows.Count(w => brains[w].Running).ShouldBe(3);
    }

    [Fact]
    public async Task A_chat_used_again_is_warm_again_and_rested_only_once_while_it_lies_cold()
    {
        var brains = new Dictionary<Guid, CountingBrain>();
        using var pool = new ChatBrains(window => brains[window ?? Guid.Empty] = new CountingBrain());
        var w = Enumerable.Range(1, 5).Select(i => new Guid(i, 0, 0, new byte[8])).ToList();

        foreach (var window in w[..4])
        {
            await AskAsync(pool.For(window), "Hi"); // the first falls behind
        }

        await AskAsync(pool.For(w[4]), "Hi"); // the second too; the first is not rested again
        brains[w[0]].Rests.ShouldBe(1);
        brains[w[1]].Rests.ShouldBe(1);

        pool.For(w[0]).WarmUp(); // used again: warm, and w[2] falls behind
        brains[w[0]].Running.ShouldBeTrue();
        brains[w[2]].Rests.ShouldBe(1);
        brains[w[3]].Rests.ShouldBe(0);
    }

    [Fact]
    public void Disposing_the_pool_disposes_every_brain_and_a_brain_it_handed_out_does_not()
    {
        var brains = new List<CountingBrain>();
        var pool = new ChatBrains(_ =>
        {
            var brain = new CountingBrain();
            brains.Add(brain);
            return brain;
        });
        var handed = pool.For(Window3);
        pool.For(null);

        handed.DisposeAsync();
        brains[0].Disposed.ShouldBeFalse("the pool owns it");
        pool.Dispose();

        brains.ShouldAllBe(b => b.Disposed);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }

    /// <summary>A brain that runs a process from a question or warm-up until it is rested.</summary>
    private sealed class CountingBrain : IConductorBrain
    {
        public int Rests { get; private set; }

        public bool Running { get; private set; }

        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<BrainEvent> AskAsync(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Running = true;
            await Task.Yield();
            yield return new BrainText("Hi.");
        }

        public void WarmUp() => Running = true;

        public void Rest()
        {
            Rests++;
            Running = false;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
