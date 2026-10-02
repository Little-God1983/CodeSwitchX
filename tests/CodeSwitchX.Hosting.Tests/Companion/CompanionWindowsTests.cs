using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.VsCode.Companion;

namespace CodeSwitchX.Hosting.Tests.Companion;

public sealed class CompanionWindowsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "csx-companion-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcesses _processes = new();
    private readonly CompanionWindows _windows;

    public CompanionWindowsTests()
    {
        Directory.CreateDirectory(_directory);
        _windows = new CompanionWindows(_directory, _processes.StartOf);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Workspace App = new() { Name = "App", RootPath = @"E:\Repos\App" };

    private static readonly Workspace Full = new()
    {
        Name = "Full", RootPath = @"E:\Repos\App", WorkspaceFile = @"E:\Repos\Full.code-workspace",
    };

    private void Record(int pid, string[] folders, string? workspaceFile = null, string? name = null, object? extra = null)
    {
        var record = new Dictionary<string, object?>
        {
            ["pid"] = pid, ["pipe"] = $@"\\.\pipe\csx-test-{pid}", ["token"] = "secret", ["version"] = "0.1.0",
            ["folders"] = folders, ["workspaceFile"] = workspaceFile,
        };
        File.WriteAllText(Path.Combine(_directory, name ?? $"{pid}.json"), JsonSerializer.Serialize(record));
    }

    [Fact]
    public void A_folder_workspace_is_the_window_whose_only_folder_it_is_whatever_the_spelling()
    {
        Record(10, [@"c:\Other"]);
        Record(11, [@"e:\repos\app\"]);

        var window = _windows.Find(App).ShouldNotBeNull();

        window.Pid.ShouldBe(11);
        window.Pipe.ShouldBe(@"\\.\pipe\csx-test-11");
        window.Folders.ShouldBe([@"e:\repos\app\"]);
    }

    [Fact]
    public void A_window_with_more_folders_or_a_workspace_file_is_not_the_folder_s()
    {
        Record(10, [@"E:\Repos\App", @"E:\Repos\Lib"]);
        Record(11, [@"E:\Repos\App"], @"E:\Repos\Full.code-workspace");

        _windows.Find(App).ShouldBeNull();
    }

    [Fact]
    public void A_code_workspace_is_the_window_that_has_its_file_open()
    {
        Record(10, [@"E:\Repos\App"]);
        Record(11, [@"E:\Repos\Lib", @"E:\Repos\App"], @"E:\Repos\Full.code-workspace");

        var window = _windows.Find(Full).ShouldNotBeNull();

        window.Pid.ShouldBe(11);
        window.Folders.ShouldBe([@"E:\Repos\Lib", @"E:\Repos\App"]);
        window.WorkspaceFile.ShouldBe(@"E:\Repos\Full.code-workspace");
    }

    [Fact]
    public void A_record_whose_process_is_gone_is_no_window_and_is_deleted()
    {
        // VS Code killed: the record stays behind.
        Record(11, [@"E:\Repos\App"]);
        _processes.Gone.Add(11);

        _windows.Find(App).ShouldBeNull();

        File.Exists(Path.Combine(_directory, "11.json")).ShouldBeFalse();
    }

    [Fact]
    public void A_record_whose_id_a_process_that_may_not_be_opened_took_is_no_window()
    {
        // Windows restarted with the window open; its id now belongs to a SYSTEM process the app may not look at.
        Record(11, [@"E:\Repos\App"]);
        _processes.Unopenable.Add(11);

        _windows.Find(App).ShouldBeNull();

        File.Exists(Path.Combine(_directory, "11.json")).ShouldBeFalse();
    }

    [Fact]
    public void A_record_whose_id_a_later_process_took_is_no_window()
    {
        Record(11, [@"E:\Repos\App"]);
        _processes.Started[11] = DateTime.UtcNow.AddMinutes(5).ToFileTimeUtc();

        _windows.Find(App).ShouldBeNull();
    }

    [Fact]
    public void A_live_window_s_record_stays()
    {
        Record(11, [@"E:\Repos\App"]);

        _windows.Find(App).ShouldNotBeNull();

        File.Exists(Path.Combine(_directory, "11.json")).ShouldBeTrue();
    }

    [Fact]
    public void Records_that_are_not_records_are_skipped()
    {
        Record(11, [@"E:\Repos\App"], name: "12.json"); // its name is another process's
        File.WriteAllText(Path.Combine(_directory, "13.json"), "{ half");
        File.WriteAllText(Path.Combine(_directory, "notes.json"), "{}");
        File.WriteAllText(Path.Combine(_directory, "14.json"), JsonSerializer.Serialize(new { pid = 14, folders = new[] { @"E:\Repos\App" } })); // no pipe

        _windows.Find(App).ShouldBeNull();
    }

    [Fact]
    public void No_folder_is_no_window()
    {
        new CompanionWindows(Path.Combine(_directory, "missing"), _processes.StartOf).Find(App).ShouldBeNull();
    }

    [Fact]
    public async Task A_command_goes_over_the_window_s_pipe_with_its_token_and_its_answer_comes_back()
    {
        var name = "csx-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(Ct);
            var reader = new StreamReader(server, Encoding.UTF8);
            var request = await reader.ReadLineAsync(Ct);
            var answer = Encoding.UTF8.GetBytes("{\"ok\":true,\"pid\":4000}\n");
            await server.WriteAsync(answer, Ct);
            await server.FlushAsync(Ct);
            return request;
        }, Ct);

        var result = await _windows.SendAsync(new CompanionWindow(4000, $@"\\.\pipe\{name}", "secret", [], null, null), "newChat", null, Ct);

        result.ShouldBe(new CompanionAnswer(true, 4000));
        using var sent = JsonDocument.Parse((await serving)!);
        sent.RootElement.GetProperty("token").GetString().ShouldBe("secret");
        sent.RootElement.GetProperty("command").GetString().ShouldBe("newChat");
    }

    [Fact]
    public void A_window_is_found_by_its_extension_host()
    {
        Record(10, [@"E:\Repos\App"]);
        Record(11, [@"E:\Repos\Lib", @"E:\Repos\App"], @"E:\Repos\Full.code-workspace");
        Record(12, [@"E:\Repos\Old"]);
        _processes.Gone.Add(12);

        _windows.Of(11).ShouldNotBeNull().Folders.ShouldBe([@"E:\Repos\Lib", @"E:\Repos\App"]);
        _windows.Of(12).ShouldBeNull("its VS Code is gone");
        _windows.Of(13).ShouldBeNull();
    }

    [Fact]
    public async Task Closing_a_chat_names_the_chat()
    {
        var name = "csx-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(Ct);
            var request = await new StreamReader(server, Encoding.UTF8).ReadLineAsync(Ct);
            await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":true}\n"), Ct);
            await server.FlushAsync(Ct);
            return request;
        }, Ct);

        var result = await _windows.SendAsync(new CompanionWindow(4000, name, "secret", [], null, null), CompanionWindows.CloseChat,
            "0b5d7153-5759-4359-8fa3-a562a130a880", Ct);

        result.Ok.ShouldBeTrue();
        using var sent = JsonDocument.Parse((await serving)!);
        sent.RootElement.GetProperty("command").GetString().ShouldBe("closeChat");
        sent.RootElement.GetProperty("sessionId").GetString().ShouldBe("0b5d7153-5759-4359-8fa3-a562a130a880");
    }

    [Fact]
    public async Task A_refusal_comes_back_with_its_reason()
    {
        var name = "csx-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(Ct);
            await new StreamReader(server, Encoding.UTF8).ReadLineAsync(Ct);
            await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"Wrong token.\"}\n"), Ct);
            await server.FlushAsync(Ct);
        }, Ct);

        var result = await _windows.SendAsync(new CompanionWindow(4000, name, "old", [], null, null), "newChat", null, Ct);
        await serving;

        result.ShouldBe(new CompanionAnswer(false, Error: "Wrong token."));
    }

    [Fact]
    public async Task A_window_nobody_listens_for_is_said_not_thrown()
    {
        var windows = new CompanionWindows(_directory, _processes.StartOf) { Timeout = TimeSpan.FromMilliseconds(300), ChatTimeout = TimeSpan.FromMinutes(5) };
        var nobody = new CompanionWindow(4000, @"\\.\pipe\csx-test-nobody-" + Guid.NewGuid().ToString("N"), "t", [], null, null);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ping = await windows.SendAsync(nobody, "ping", null, Ct);
        var chat = await windows.SendAsync(nobody, CompanionWindows.NewChat, null, Ct);

        ping.Ok.ShouldBeFalse();
        ping.Error.ShouldNotBeNull().ShouldStartWith("The CodeSwitchX companion in that VS Code window does not answer.");
        chat.Error.ShouldBe(ping.Error);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "a pipe nobody serves costs the short wait, not the long one a chat may take to open");
    }

    [Fact]
    public async Task A_chat_that_takes_longer_than_its_wait_may_still_open()
    {
        var name = "csx-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(Ct);
            await new StreamReader(server, Encoding.UTF8).ReadLineAsync(Ct); // and never answers
        }, Ct);
        var windows = new CompanionWindows(_directory, _processes.StartOf) { ChatTimeout = TimeSpan.FromMilliseconds(300) };

        var result = await windows.SendAsync(new CompanionWindow(4000, name, "t", [], null, null), CompanionWindows.NewChat, null, Ct);
        await serving;

        result.Error.ShouldBe("The VS Code window did not answer within 0 seconds. A chat tab may still open there.");
    }

    [Fact]
    public void Opening_a_chat_may_take_far_longer_than_any_other_request()
    {
        // Claude Code's extension activates first in a window VS Code just started.
        CompanionWindows.NewChatTimeout.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(60));
        CompanionWindows.RequestTimeout.ShouldBeLessThan(CompanionWindows.NewChatTimeout);
    }

    /// <summary>Every process started an hour ago, before any record, unless set otherwise.</summary>
    private sealed class FakeProcesses
    {
        public HashSet<int> Gone { get; } = [];

        /// <summary>Runs, but may not be opened: the system's lookup throws.</summary>
        public HashSet<int> Unopenable { get; } = [];

        public Dictionary<int, long> Started { get; } = [];

        /// <summary>Through the same conversion as the system's: a process that may not be opened throws there.</summary>
        public long? StartOf(int pid) => CompanionWindows.StartOf(pid, Read);

        private long? Read(int pid) =>
            Unopenable.Contains(pid) ? throw new System.ComponentModel.Win32Exception(5, "Access is denied.")
            : Gone.Contains(pid) ? null
            : Started.TryGetValue(pid, out var start) ? start
            : DateTime.UtcNow.AddHours(-1).ToFileTimeUtc();
    }
}
