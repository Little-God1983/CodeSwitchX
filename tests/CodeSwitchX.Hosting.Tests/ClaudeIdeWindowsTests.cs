using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Hosting.Tests;

public sealed class ClaudeIdeWindowsTests : IDisposable
{
    private readonly string _locks = Directory.CreateTempSubdirectory("csx-ide-").FullName;
    private readonly Dictionary<int, int> _listeners = [];
    private readonly Dictionary<int, (int Parent, string Name)> _processes = [];
    private readonly FakeTimeProvider _time = new();
    private int _listenerReads;
    private int _processReads;
    private Exception? _failure;

    public void Dispose() => Directory.Delete(_locks, recursive: true);

    private ClaudeIdeWindows Windows(string? lockDirectory = null) => new(lockDirectory ?? _locks,
        () =>
        {
            _listenerReads++;
            return _failure is null ? _listeners : throw _failure;
        },
        () =>
        {
            _processReads++;
            return _processes;
        },
        _time);

    private void Lock(int port, string json) => File.WriteAllText(Path.Combine(_locks, $"{port}.lock"), json);

    /// <summary>What the extension writes: <c>pid</c> is VS Code's main process, the same for every window.</summary>
    private void Window(int port, int extensionHost, params string[] folders)
    {
        Lock(port, $$"""{"pid":500,"workspaceFolders":[{{string.Join(",", folders.Select(f => $"\"{f.Replace(@"\", @"\\")}\""))}}],"ideName":"Visual Studio Code","transport":"ws","authToken":"x"}""");
        _listeners[port] = extensionHost;
    }

    /// <summary>A process line from <paramref name="pid"/> up: each process's parent is the next one.</summary>
    private void Line(params int[] pids)
    {
        for (var i = 0; i < pids.Length; i++)
        {
            _processes[pids[i]] = (i + 1 < pids.Length ? pids[i + 1] : 0, "p.exe");
        }
    }

    private static IReadOnlyList<string>? FoldersOf(ClaudeIdeWindows windows, int pid, IReadOnlyList<int>? ancestors = null)
    {
        windows.TryFoldersOf(pid, ancestors, out var folders).ShouldBeTrue();
        return folders;
    }

    [Fact]
    public void A_claude_is_in_the_window_whose_extension_host_started_it()
    {
        Window(14108, extensionHost: 31, @"e:\Repos\SDK", @"e:\Repos\Nexus");
        Window(18501, extensionHost: 41, @"e:\Repos\SDK", @"e:\Repos\Tools", @"e:\Repos\Catalog");
        Line(1000, 41, 500, 1);
        Line(2000, 31, 500);
        var windows = Windows();

        FoldersOf(windows, 1000).ShouldBe([@"e:\Repos\SDK", @"e:\Repos\Tools", @"e:\Repos\Catalog"]);
        FoldersOf(windows, 2000).ShouldBe([@"e:\Repos\SDK", @"e:\Repos\Nexus"]);
    }

    [Fact]
    public void The_processes_a_hook_event_names_spare_reading_every_process()
    {
        Window(14108, extensionHost: 31, @"e:\Repos\App");

        FoldersOf(Windows(), 1000, ancestors: [31, 500]).ShouldBe([@"e:\Repos\App"]);

        _processReads.ShouldBe(0);
    }

    [Fact]
    public void A_claude_started_by_another_chats_claude_is_in_that_chats_window()
    {
        Window(14108, extensionHost: 31, @"e:\Repos\App");
        Line(3000, 2900, 2800, 2000, 31, 500);

        FoldersOf(Windows(), 3000).ShouldBe([@"e:\Repos\App"]);
    }

    [Fact]
    public void A_claude_in_a_terminal_is_in_no_window()
    {
        Window(14108, extensionHost: 31, @"e:\Repos\App");
        Line(1000, 900, 77, 500); // the shell, VS Code's terminal host, VS Code

        FoldersOf(Windows(), 1000).ShouldBeNull();
    }

    [Fact]
    public void The_lock_of_a_closed_window_whose_port_went_to_another_process_is_no_match()
    {
        Window(14108, extensionHost: 31, @"e:\Repos\Old");
        Lock(26044, """{"pid":500,"workspaceFolders":["e:\\Repos\\Gone"]}"""); // nobody listens there any more
        Line(1000, 41, 500);

        FoldersOf(Windows(), 1000).ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"pid":500}""")]
    [InlineData("""{"workspaceFolders":"e:\\Repos\\App"}""")]
    [InlineData("""{"workspaceFolders":[]}""")]
    [InlineData("""{"workspaceFolders":[1, "", null]}""")]
    public void A_lock_without_folders_tells_no_window(string json)
    {
        Lock(14108, json);
        _listeners[14108] = 31;

        FoldersOf(Windows(), 1000, ancestors: [31, 500]).ShouldBeNull();
    }

    [Fact]
    public void A_process_that_is_gone_or_a_missing_lock_folder_tells_no_window()
    {
        Window(14108, extensionHost: 31, @"e:\Repos\App");

        FoldersOf(Windows(), 1000).ShouldBeNull();
        FoldersOf(Windows(Path.Combine(_locks, "missing")), 1000, ancestors: [31]).ShouldBeNull();
    }

    [Fact]
    public void A_system_table_that_cannot_be_read_tells_nothing_rather_than_no_window()
    {
        Window(14108, extensionHost: 31, @"e:\Repos\App");
        _failure = new Win32Exception(5);

        Windows().TryFoldersOf(1000, [31, 500], out var folders).ShouldBeFalse();
        folders.ShouldBeNull();
    }

    [Fact]
    public void One_read_of_the_system_serves_the_lookups_that_follow_it_closely()
    {
        Window(14108, extensionHost: 31, @"e:\Repos\App");
        Line(1000, 31, 500);
        Line(2000, 31, 500);
        var windows = Windows();

        FoldersOf(windows, 1000);
        FoldersOf(windows, 2000);
        _listenerReads.ShouldBe(1);
        _processReads.ShouldBe(1);

        _time.Advance(ClaudeIdeWindows.MaxAge);
        Window(18501, extensionHost: 41, @"e:\Repos\New");
        Line(3000, 41, 500);

        FoldersOf(windows, 3000).ShouldBe([@"e:\Repos\New"]);
        _listenerReads.ShouldBe(2);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::")]
    public void The_listener_table_names_this_process_for_a_port_it_listens_on(string address)
    {
        var listener = new TcpListener(IPAddress.Parse(address), 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            ProcessTable.Listeners()[port].ShouldBe(Environment.ProcessId);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void The_process_table_holds_this_process_and_its_parent()
    {
        var processes = ProcessTable.Processes();

        processes.ShouldContainKey(Environment.ProcessId);
        processes[Environment.ProcessId].Parent.ShouldNotBe(0);
    }
}
