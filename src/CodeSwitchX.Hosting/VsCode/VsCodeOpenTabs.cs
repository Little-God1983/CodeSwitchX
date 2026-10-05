using System.Text.Json;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace CodeSwitchX.Hosting.VsCode;

/// <summary>A Claude Code chat tab of a VS Code window.</summary>
/// <param name="Title">The tab's title as VS Code keeps it, cut short by Claude Code; null for a chat nothing was said in yet.</param>
public sealed record OpenChatTab(string SessionId, string? Title);

/// <summary>The Claude Code chat tabs of a workspace's VS Code window, as VS Code last wrote them down.</summary>
/// <param name="WrittenAt">When VS Code wrote the list: a tab opened or closed since is not in it yet.</param>
public sealed record OpenChatTabs(DateTimeOffset WrittenAt, IReadOnlyList<OpenChatTab> Tabs);

/// <summary>The Claude Code chat tabs open in the workspaces' VS Code windows (#164).</summary>
public interface IVsCodeOpenTabs
{
    /// <summary>
    /// The tabs of each workspace VS Code keeps a list for, by workspace id; a workspace VS Code never opened has none.
    /// Reads files: not for the UI thread. Never throws.
    /// </summary>
    IReadOnlyDictionary<Guid, OpenChatTabs> Read(IReadOnlyList<Workspace> workspaces);
}

/// <summary>
/// Reads the tabs from what VS Code restores a window from: <c>workspaceStorage\&lt;hash&gt;\state.vscdb</c>, a SQLite file
/// per workspace, whose <c>workspace.json</c> names the folder or .code-workspace file. Its <c>memento/workbench.parts.editor</c>
/// row lists every open editor; a Claude Code chat is a webview editor of Claude's panel whose state carries the chat's
/// session id. VS Code writes the file when its tabs change (within about a minute) and as it closes, so a window that is
/// closed keeps the tabs it will come back with. A folder deleted and made again has a store of its own: the one written
/// last counts. A store is read again only when its file changed.
/// </summary>
public sealed class VsCodeOpenTabs : IVsCodeOpenTabs
{
    private const string EditorsKey = "memento/workbench.parts.editor";
    private const string WebviewEditor = "workbench.editors.webviewInput";
    private const string ClaudePanel = "claudeVSCodePanel";

    /// <summary>A workspace.json is a line or two; anything far bigger is not one.</summary>
    private const long MaxWorkspaceJsonBytes = 64 * 1024;

    private readonly string _directory;
    private readonly Lock _gate = new();

    /// <summary>What each store's workspace.json names, normalized; null for one that names nothing local. It never changes.</summary>
    private readonly Dictionary<string, string?> _targets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tabs read from each store file, with the write time they were read at.</summary>
    private readonly Dictionary<string, OpenChatTabs> _read = new(StringComparer.OrdinalIgnoreCase);

    public VsCodeOpenTabs(string directory)
    {
        _directory = directory;
    }

    /// <summary>Where VS Code keeps its workspaces' state: <c>%APPDATA%\Code\User\workspaceStorage</c>.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "workspaceStorage");

    public IReadOnlyDictionary<Guid, OpenChatTabs> Read(IReadOnlyList<Workspace> workspaces)
    {
        var result = new Dictionary<Guid, OpenChatTabs>();
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(_directory))
                {
                    return result;
                }

                // The newest store of each target: a folder made again has an older one left behind.
                var newest = new Dictionary<string, (string File, DateTime Written)>(StringComparer.Ordinal);
                foreach (var store in Directory.EnumerateDirectories(_directory))
                {
                    var file = Path.Combine(store, "state.vscdb");
                    if (TargetOf(store) is not { } target || !File.Exists(file))
                    {
                        continue;
                    }

                    var written = File.GetLastWriteTimeUtc(file);
                    if (!newest.TryGetValue(target, out var known) || written > known.Written)
                    {
                        newest[target] = (file, written);
                    }
                }

                foreach (var workspace in workspaces)
                {
                    if (Normalized(workspace.Target) is { } target && newest.TryGetValue(target, out var store)
                        && TabsOf(store.File, store.Written) is { } tabs)
                    {
                        result[workspace.Id] = tabs;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The folder went, or may not be listed: what was read so far stands.
            }
        }

        return result;
    }

    private string? TargetOf(string store)
    {
        if (_targets.TryGetValue(store, out var known))
        {
            return known;
        }

        string? target;
        try
        {
            var file = Path.Combine(store, "workspace.json");
            if (!File.Exists(file) || new FileInfo(file).Length > MaxWorkspaceJsonBytes)
            {
                return null; // not there yet: looked at again next time
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var uri = root.ValueKind != JsonValueKind.Object ? null
                : root.TryGetProperty("workspace", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString()
                : root.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()
                : null;
            target = LocalPath(uri) is { } path ? Normalized(path) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // being written: looked at again next time
        }

        _targets[store] = target;
        return target;
    }

    /// <summary>
    /// The path a <c>file:///e%3A/Repos/App</c> URI names; null for anything else (a remote or container workspace).
    /// </summary>
    internal static string? LocalPath(string? uri)
    {
        const string scheme = "file:///";
        if (uri is null || !uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = Uri.UnescapeDataString(uri[scheme.Length..]).Replace('/', Path.DirectorySeparatorChar);
        return path.Length > 2 && path[1] == ':' ? path : null;
    }

    private static string? Normalized(string path)
    {
        try
        {
            return PathNormalizer.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The store's tabs; what was read before while the file is as it was, and when it cannot be read now.</summary>
    private OpenChatTabs? TabsOf(string file, DateTime written)
    {
        var writtenAt = new DateTimeOffset(written, TimeSpan.Zero);
        if (_read.TryGetValue(file, out var known) && known.WrittenAt == writtenAt)
        {
            return known;
        }

        try
        {
            // Read only, and not kept open: VS Code writes the file while it runs.
            var connection = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var db = new SqliteConnection(connection);
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT value FROM ItemTable WHERE key = $key";
            command.Parameters.AddWithValue("$key", EditorsKey);
            var value = command.ExecuteScalar() switch
            {
                string text => text,
                byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                _ => null,
            };
            return _read[file] = new OpenChatTabs(writtenAt, value is null ? [] : ChatTabs(value));
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or IOException or UnauthorizedAccessException)
        {
            return known; // locked for a moment, or half written: the next look has it
        }
    }

    /// <summary>The Claude Code chat tabs in VS Code's list of open editors, in its order; a chat open in two tabs is one.</summary>
    internal static IReadOnlyList<OpenChatTab> ChatTabs(string editors)
    {
        var tabs = new List<OpenChatTab>();
        using var document = JsonDocument.Parse(editors);
        Collect(document.RootElement, tabs);
        return tabs;
    }

    private static void Collect(JsonElement node, List<OpenChatTab> tabs)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                Collect(item, tabs);
            }

            return;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (node.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == WebviewEditor
            && node.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
        {
            if (ChatTab(value.GetString()!) is { } tab && tabs.TrueForAll(t => !string.Equals(t.SessionId, tab.SessionId, StringComparison.OrdinalIgnoreCase)))
            {
                tabs.Add(tab);
            }

            return;
        }

        foreach (var property in node.EnumerateObject())
        {
            Collect(property.Value, tabs);
        }
    }

    /// <summary>The chat of a webview editor VS Code wrote down, when it is one of Claude Code's panel and names its chat.</summary>
    private static OpenChatTab? ChatTab(string editor)
    {
        try
        {
            using var document = JsonDocument.Parse(editor);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("viewType", out var viewType) || viewType.ValueKind != JsonValueKind.String
                || !viewType.GetString()!.Contains(ClaudePanel, StringComparison.Ordinal)
                || !root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            using var panel = JsonDocument.Parse(state.GetString()!);
            if (panel.RootElement.ValueKind != JsonValueKind.Object
                || !panel.RootElement.TryGetProperty("sessionID", out var session) || session.ValueKind != JsonValueKind.String
                || session.GetString() is not { Length: > 0 } sessionId)
            {
                return null;
            }

            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return new OpenChatTab(sessionId, string.IsNullOrWhiteSpace(title) || title == NewChatTitle ? null : title);
        }
        catch (JsonException)
        {
            return null; // one editor that cannot be read does not hide the others
        }
    }

    /// <summary>What Claude Code titles a tab nothing was said in yet.</summary>
    private const string NewChatTitle = "Claude Code";
}
