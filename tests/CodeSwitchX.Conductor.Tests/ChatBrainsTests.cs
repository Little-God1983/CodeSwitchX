using CodeSwitchX.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Conductor.Tests;

/// <summary>
/// Each Raven chat's own brain (#123), and few processes. How a brain keeps its conversation and names its window to the
/// tools is <see cref="ClaudeCliBrainTests"/>'s; how its session picks the conversation up again is the package's (#314).
/// </summary>
public sealed class ChatBrainsTests : IDisposable
{
    private static readonly Guid Window3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-chatbrains-" + Guid.NewGuid().ToString("N")));
    private readonly BrainSettings _settings = new();
    private readonly List<FakeClaudeSession> _started = [];
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

    private ClaudeCliBrain BrainOf(Guid? window) => new(_paths, _settings, NullLogger<ClaudeCliBrain>.Instance, (options, cli) =>
    {
        var session = new FakeClaudeSession(options, cli);
        _started.Add(session);
        return session;
    }, chat: BrainChat.Of(window, _sessions));

    private static async Task AskAsync(IConductorBrain brain, string text)
    {
        await foreach (var _ in brain.AskAsync(text, TestContext.Current.CancellationToken))
        {
        }
    }

    [Fact]
    public async Task A_brain_handed_out_disposes_synchronously_without_touching_the_pool_s()
    {
        var inner = new CountingBrain();
        using var pool = new ChatBrains(_ => inner);

        var handed = pool.For(null).ShouldBeAssignableTo<IDisposable>("the app's host disposes its singletons synchronously");
        handed.Dispose();

        inner.Disposed.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_brain_asked_for_after_the_pool_is_disposed_says_so_instead_of_throwing()
    {
        var pool = new ChatBrains(_ => new CountingBrain());
        pool.Dispose();

        var brain = pool.For(Window3);
        brain.WarmUp();
        var events = new List<BrainEvent>();
        await foreach (var e in brain.AskAsync("Hi", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.ShouldBe([new BrainFailed("Raven's brain has shut down with CodeSwitchX.")]);
    }

    [Fact]
    public void Configs_left_by_an_earlier_run_are_cleared_as_the_app_starts()
    {
        var folder = ClaudeCliBrain.McpDirectory(_paths);
        Directory.CreateDirectory(Path.Combine(folder, "a1b2"));
        File.WriteAllText(Path.Combine(folder, Window3.ToString("N") + ".json"), "{}"); // as brains before #314 kept them
        File.WriteAllText(Path.Combine(folder, "a1b2", "mcp-config.json"), "{}"); // as a session's process keeps it

        ChatMcpConfig.Reset(folder);

        Directory.GetFileSystemEntries(folder).ShouldBeEmpty("a crash leaves them, with the token in them");
    }

    [Fact]
    public void The_folder_the_brains_processes_keep_the_token_in_is_made_and_the_user_s_alone()
    {
        var folder = ClaudeCliBrain.McpDirectory(_paths);

        ChatMcpConfig.Reset(folder);

        var security = new DirectoryInfo(folder).GetAccessControl();
        security.AreAccessRulesProtected.ShouldBeTrue("nothing inherited from above");
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
        rules.ShouldHaveSingleItem().IdentityReference.ShouldBe(System.Security.Principal.WindowsIdentity.GetCurrent().User);
        rules[0].InheritanceFlags.ShouldBe(System.Security.AccessControl.InheritanceFlags.ContainerInherit
            | System.Security.AccessControl.InheritanceFlags.ObjectInherit, "each process's folder and config inherit it");
    }

    [Fact]
    public async Task A_window_s_brain_is_retired_with_its_window_its_session_and_conversation_gone()
    {
        var made = new List<ClaudeCliBrain>();
        using var pool = new ChatBrains(window =>
        {
            var brain = BrainOf(window);
            made.Add(brain);
            return brain;
        }, sessions: _sessions);
        await AskAsync(pool.For(Window3), "Hi");
        _sessions.Save(Window3.ToString("N"), new BrainSession("s-1", BrainSettings.DefaultModel, DateTimeOffset.UtcNow));

        pool.Retire(Window3);

        // Off the caller's thread, the UI's: the session kills its process tree, and its files go with the process.
        await WaitUntil(() => _started.ShouldHaveSingleItem().Disposed);
        await WaitUntil(() => _sessions.Load(Window3.ToString("N")) is null);
        pool.For(Window3);
        made.Count.ShouldBe(2, "a window back on the Yard gets a new brain");
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
