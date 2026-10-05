using System.Text.Json;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.VsCode;
using Microsoft.Data.Sqlite;

namespace CodeSwitchX.Hosting.Tests;

/// <summary>#164: the chat tabs open in a workspace's VS Code window, read from what VS Code restores the window from.</summary>
public sealed class VsCodeOpenTabsTests : IDisposable
{
    private const string Apple = "46712273-564c-4ce6-832b-2be458449c8e";
    private const string Banana = "c3470b3a-2116-4f26-aff9-199b173828ee";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-tabs-" + Guid.NewGuid().ToString("N"));
    private readonly string _storage;
    private readonly Workspace _app;
    private readonly VsCodeOpenTabs _tabs;

    public VsCodeOpenTabsTests()
    {
        _storage = Path.Combine(_root, "workspaceStorage");
        Directory.CreateDirectory(_storage);
        _app = new Workspace { Name = "App", RootPath = Path.Combine(_root, "Repos", "App") };
        _tabs = new VsCodeOpenTabs(_storage);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string FileUri(string path) => "file:///" + path.Replace('\\', '/').Replace(":", "%3A").Replace(" ", "%20");

    /// <summary>A Claude Code chat tab as VS Code writes it down: a webview editor whose state names the chat.</summary>
    private static object Chat(string? sessionId, string title = "Claude Code") => new
    {
        id = "workbench.editors.webviewInput",
        value = JsonSerializer.Serialize(new
        {
            viewType = "mainThreadWebview-claudeVSCodePanel",
            title,
            state = sessionId is null ? "{\"isFullEditor\":false}" : JsonSerializer.Serialize(new { isFullEditor = false, sessionID = sessionId }),
        }),
    };

    private static object File_(string path) => new { id = "workbench.editors.files.fileEditorInput", value = JsonSerializer.Serialize(new { resourceJSON = path }) };

    private static object Other(string viewType) => new
    {
        id = "workbench.editors.webviewInput",
        value = JsonSerializer.Serialize(new { viewType, title = "Preview", state = "{\"sessionID\":\"not-a-chat\"}" }),
    };

    /// <summary>A store as VS Code keeps it: workspace.json and state.vscdb, the editors in one group.</summary>
    private string Store(string name, object workspaceJson, params object[] editors)
    {
        var store = Path.Combine(_storage, name);
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "workspace.json"), JsonSerializer.Serialize(workspaceJson));
        var grid = new Dictionary<string, object>
        {
            ["editorpart.state"] = new { serializedGrid = new { root = new { type = "branch", data = new[] { new { type = "leaf", data = new { id = 1, editors } } } } } },
        };
        var file = Path.Combine(store, "state.vscdb");
        File.Delete(file);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB); INSERT INTO ItemTable VALUES ($k, $v), ('other', 'x')";
        command.Parameters.AddWithValue("$k", "memento/workbench.parts.editor");
        command.Parameters.AddWithValue("$v", JsonSerializer.Serialize(grid));
        command.ExecuteNonQuery();
        return file;
    }

    private OpenChatTabs? Read(Workspace? workspace = null) => _tabs.Read([workspace ?? _app]).GetValueOrDefault((workspace ?? _app).Id);

    [Fact]
    public void The_chat_tabs_of_a_folder_s_window_are_listed_in_VS_Code_s_order_with_their_titles()
    {
        var file = Store("a1", new { folder = FileUri(_app.RootPath) }, File_("Program.cs"), Chat(Banana, "Banana discussion"), Other("markdown.preview"), Chat(Apple, "Apple"));

        var tabs = Read().ShouldNotBeNull();

        tabs.Tabs.ShouldBe([new OpenChatTab(Banana, "Banana discussion"), new OpenChatTab(Apple, "Apple")]);
        tabs.WrittenAt.UtcDateTime.ShouldBe(File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void A_workspace_file_s_window_is_found_by_its_file_not_its_folder()
    {
        var file = Path.Combine(_root, "Repos", "App.code-workspace");
        var byFile = new Workspace { Name = "App", RootPath = _app.RootPath, WorkspaceFile = file };
        Store("folder", new { folder = FileUri(_app.RootPath) }, Chat(Apple, "Apple"));
        Store("file", new { workspace = FileUri(file) }, Chat(Banana, "Banana"));

        Read(byFile).ShouldNotBeNull().Tabs.ShouldBe([new OpenChatTab(Banana, "Banana")]);
        Read().ShouldNotBeNull().Tabs.ShouldBe([new OpenChatTab(Apple, "Apple")]);
    }

    [Fact]
    public void A_chat_nothing_was_said_in_has_no_title_and_a_tab_that_names_no_chat_is_none()
    {
        Store("a1", new { folder = FileUri(_app.RootPath) }, Chat(Apple), Chat(null));

        Read().ShouldNotBeNull().Tabs.ShouldBe([new OpenChatTab(Apple, null)]);
    }

    [Fact]
    public void A_window_without_chat_tabs_has_an_empty_list_and_a_workspace_VS_Code_never_opened_has_none()
    {
        Store("a1", new { folder = FileUri(_app.RootPath) }, File_("Program.cs"));
        var never = new Workspace { Name = "Never", RootPath = Path.Combine(_root, "Repos", "Never") };

        Read().ShouldNotBeNull().Tabs.ShouldBeEmpty();
        Read(never).ShouldBeNull();
    }

    /// <summary>A folder deleted and made again gets a second store: the tabs are those of the one VS Code wrote last.</summary>
    [Fact]
    public void Of_two_stores_of_one_folder_the_one_written_last_counts()
    {
        var old = Store("old", new { folder = FileUri(_app.RootPath) }, Chat(Apple, "Apple"));
        Store("new", new { folder = FileUri(_app.RootPath) }, Chat(Banana, "Banana"));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));

        Read().ShouldNotBeNull().Tabs.ShouldBe([new OpenChatTab(Banana, "Banana")]);
    }

    [Fact]
    public void The_folder_is_matched_whatever_the_case_and_spelling_of_its_path()
    {
        Store("a1", new { folder = FileUri(_app.RootPath.ToUpperInvariant()) }, Chat(Apple, "Apple"));

        Read().ShouldNotBeNull().Tabs.Count.ShouldBe(1);
    }

    [Fact]
    public void A_changed_store_is_read_again()
    {
        var file = Store("a1", new { folder = FileUri(_app.RootPath) }, Chat(Apple, "Apple"));
        Read().ShouldNotBeNull().Tabs.Count.ShouldBe(1);

        Store("a1", new { folder = FileUri(_app.RootPath) }, Chat(Apple, "Apple"), Chat(Banana, "Banana"));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));

        Read().ShouldNotBeNull().Tabs.Count.ShouldBe(2);
    }

    [Fact]
    public void A_store_that_cannot_be_read_and_a_remote_workspace_are_skipped()
    {
        var broken = Path.Combine(_storage, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "workspace.json"), JsonSerializer.Serialize(new { folder = FileUri(_app.RootPath) }));
        File.WriteAllText(Path.Combine(broken, "state.vscdb"), "not a database");
        Store("remote", new { folder = "vscode-remote://wsl%2Bubuntu/home/me/app" }, Chat(Apple, "Apple"));

        Read().ShouldBeNull();
    }

    /// <summary>A workspace no longer asked for is not given again, and what is given is the caller's to keep.</summary>
    [Fact]
    public void Each_look_gives_the_workspaces_asked_for()
    {
        Store("a1", new { folder = FileUri(_app.RootPath) }, Chat(Apple, "Apple"));
        var other = new Workspace { Name = "Other", RootPath = Path.Combine(_root, "Repos", "Other") };

        _tabs.Read([_app, other]).Keys.ShouldBe([_app.Id]);
        _tabs.Read([other]).ShouldBeEmpty();
    }

    [Fact]
    public void No_storage_folder_gives_nothing()
    {
        new VsCodeOpenTabs(Path.Combine(_root, "missing")).Read([_app]).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("file:///e%3A/Repos/App", @"e:\Repos\App")]
    [InlineData("file:///c%3A/Users/Little%20God/x.code-workspace", @"c:\Users\Little God\x.code-workspace")]
    [InlineData("vscode-remote://wsl%2Bubuntu/home", null)]
    [InlineData("file:///home/me", null)]
    [InlineData(null, null)]
    public void A_file_uri_names_its_local_path(string? uri, string? path) => VsCodeOpenTabs.LocalPath(uri).ShouldBe(path);
}
