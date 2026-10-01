using System.Net;
using System.Net.Sockets;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.Tests;

public sealed class ClaudeIdeWindowsTests : IDisposable
{
    private readonly string _locks = Directory.CreateTempSubdirectory("csx-ide-").FullName;
    private readonly Dictionary<int, int> _listeners = [];
    private readonly Dictionary<int, IReadOnlyList<int>> _ancestors = [];

    public void Dispose() => Directory.Delete(_locks, recursive: true);

    private ClaudeIdeWindows Windows() =>
        new(_locks, () => _listeners, (pid, _) => _ancestors.GetValueOrDefault(pid) ?? []);

    private void Lock(int port, string json) => File.WriteAllText(Path.Combine(_locks, $"{port}.lock"), json);

    /// <summary>What the extension writes: <c>pid</c> is VS Code's main process, the same for every window.</summary>
    private void Window(int port, int extensionHost, params string[] folders)
    {
        Lock(port, $$"""{"pid":500,"workspaceFolders":[{{string.Join(",", folders.Select(f => $"\"{f.Replace(@"\", @"\\")}\""))}}],"ideName":"Visual Studio Code","transport":"ws","authToken":"x"}""");
        _listeners[port] = extensionHost;
    }

    [Fact]
    public void A_claude_is_in_the_window_whose_extension_host_started_it()
    {
        Window(14108, extensionHost: 31, @"e:\Repos\SDK", @"e:\Repos\Nexus");
        Window(18501, extensionHost: 41, @"e:\Repos\SDK", @"e:\Repos\Tools", @"e:\Repos\Catalog");
        _ancestors[1000] = [41, 500, 1];
        _ancestors[2000] = [31, 500, 1];

        Windows().FoldersOf(1000).ShouldBe([@"e:\Repos\SDK", @"e:\Repos\Tools", @"e:\Repos\Catalog"]);
        Windows().FoldersOf(2000).ShouldBe([@"e:\Repos\SDK", @"e:\Repos\Nexus"]);
    }

    [Fact]
    public void A_claude_started_by_another_chats_claude_is_in_that_chats_window()
    {
        Window(14108, extensionHost: 31, @"e:\Repos\App");
        _ancestors[3000] = [2900, 2800, 2000, 31, 500];

        Windows().FoldersOf(3000).ShouldBe([@"e:\Repos\App"]);
    }

    [Fact]
    public void A_claude_in_a_terminal_is_in_no_window()
    {
        Window(14108, extensionHost: 31, @"e:\Repos\App");
        _ancestors[1000] = [900, 77, 500]; // the shell, VS Code's terminal host, VS Code

        Windows().FoldersOf(1000).ShouldBeNull();
    }

    [Fact]
    public void The_lock_of_a_closed_window_whose_port_went_to_another_process_is_no_match()
    {
        Window(14108, extensionHost: 31, @"e:\Repos\Old");
        Lock(26044, """{"pid":500,"workspaceFolders":["e:\\Repos\\Gone"]}"""); // nobody listens there any more
        _ancestors[1000] = [41, 500];

        Windows().FoldersOf(1000).ShouldBeNull();
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
        _ancestors[1000] = [31, 500];

        Windows().FoldersOf(1000).ShouldBeNull();
    }

    [Fact]
    public void A_process_that_is_gone_or_a_missing_lock_folder_tells_no_window()
    {
        Window(14108, extensionHost: 31, @"e:\Repos\App");

        Windows().FoldersOf(1000).ShouldBeNull();
        new ClaudeIdeWindows(Path.Combine(_locks, "missing"), () => _listeners, (_, _) => [31]).FoldersOf(1000).ShouldBeNull();
    }

    [Fact]
    public void The_listener_table_names_this_process_for_a_port_it_listens_on()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            ProcessTable.LoopbackListeners()[port].ShouldBe(Environment.ProcessId);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void The_process_tree_walks_up_from_a_process_without_it_and_stops_at_the_depth()
    {
        var ancestors = ProcessTable.Ancestors(Environment.ProcessId, ClaudeIdeWindows.MaxDepth);

        ancestors.ShouldNotBeEmpty();
        ancestors.ShouldNotContain(Environment.ProcessId);
        ProcessTable.Ancestors(Environment.ProcessId, 1).ShouldBe([ancestors[0]]);
        ProcessTable.Ancestors(int.MaxValue - 1, ClaudeIdeWindows.MaxDepth).ShouldBeEmpty();
    }
}
