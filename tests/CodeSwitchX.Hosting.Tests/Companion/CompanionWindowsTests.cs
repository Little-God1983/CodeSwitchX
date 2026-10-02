using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.VsCode.Companion;

namespace CodeSwitchX.Hosting.Tests.Companion;

public sealed class CompanionWindowsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "csx-companion-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProbe _probe = new();
    private readonly CompanionWindows _windows;

    public CompanionWindowsTests()
    {
        Directory.CreateDirectory(_directory);
        _windows = new CompanionWindows(_directory, _probe);
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
    public void A_record_whose_process_is_gone_is_no_window()
    {
        // VS Code killed: the record stays behind.
        Record(11, [@"E:\Repos\App"]);
        _probe.Gone.Add(11);

        _windows.Find(App).ShouldBeNull();
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
        new CompanionWindows(Path.Combine(_directory, "missing"), _probe).Find(App).ShouldBeNull();
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

        var result = await _windows.SendAsync(new CompanionWindow(4000, $@"\\.\pipe\{name}", "secret", [], null, null), "newChat", Ct);

        result.ShouldBe(new CompanionAnswer(true, 4000));
        using var sent = JsonDocument.Parse((await serving)!);
        sent.RootElement.GetProperty("token").GetString().ShouldBe("secret");
        sent.RootElement.GetProperty("command").GetString().ShouldBe("newChat");
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

        var result = await _windows.SendAsync(new CompanionWindow(4000, name, "old", [], null, null), "newChat", Ct);
        await serving;

        result.ShouldBe(new CompanionAnswer(false, Error: "Wrong token."));
    }

    [Fact]
    public async Task A_window_nobody_listens_for_is_said_not_thrown()
    {
        var windows = new CompanionWindows(_directory, _probe) { Timeout = TimeSpan.FromMilliseconds(300) };

        var result = await windows.SendAsync(new CompanionWindow(4000, @"\\.\pipe\csx-test-nobody-" + Guid.NewGuid().ToString("N"), "t", [], null, null),
            "newChat", Ct);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldStartWith("The VS Code window did not answer within");
    }

    private sealed class FakeProbe : IProcessProbe
    {
        public HashSet<int> Gone { get; } = [];

        public bool IsAlive(int pid, DateTimeOffset seenAt) => !Gone.Contains(pid);
    }
}
