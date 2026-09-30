using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Infrastructure;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// The Yard as the board shows it, for the MCP tools: the tiles, their git lines and chat rows are read on the UI thread,
/// which alone touches them, so an answer tells what the user sees. The engine adds what a row leaves out (the model,
/// the last notification, the folder). A .code-workspace file's folders are read off the UI thread: it reads the disk.
/// </summary>
public sealed class YardDirectory : IYardDirectory
{
    private readonly YardViewModel _yard;
    private readonly Func<string, SessionSnapshot?> _sessionOf;
    private readonly IUiDispatcher _ui;
    private readonly Func<string, IReadOnlyList<WorkspaceFolder>?> _foldersOf;

    /// <param name="sessionOf">The engine's snapshot of a chat (<see cref="SessionEngine.Get"/>).</param>
    public YardDirectory(YardViewModel yard, Func<string, SessionSnapshot?> sessionOf, IUiDispatcher ui, Func<string, IReadOnlyList<WorkspaceFolder>?> foldersOf)
    {
        _yard = yard;
        _sessionOf = sessionOf;
        _ui = ui;
        _foldersOf = foldersOf;
    }

    /// <summary>How long a read waits for the UI thread: a tool call must fail, not hang, when the window is stuck.</summary>
    internal TimeSpan UiTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<YardWorkspace>> WorkspacesAsync(CancellationToken ct)
    {
        var tiles = await OnUiThreadAsync(() => _yard.Tracks
            .SelectMany(group => group.Tiles.Select(tile => (Track: group.Name, tile.Workspace, Git: tile.GitLines.ToList())))
            .ToList(), ct).ConfigureAwait(false);

        // Off the UI thread from here: OnUiThreadAsync resumes on the thread pool.
        return tiles.Select(t => new YardWorkspace(
                t.Workspace.Id,
                t.Workspace.Name,
                t.Track,
                t.Workspace.RootPath,
                FoldersOf(t.Workspace),
                t.Git.Select(l => new YardGitLine(l.Folder, l.Branch, l.GitStateLabel)).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<YardChat>> ChatsAsync(CancellationToken ct)
    {
        var rows = await OnUiThreadAsync(() => _yard.Tiles
            .SelectMany(tile => tile.Chats.Select(row => (tile.Workspace, Row: new Row(row.SessionId, row.Title, row.State, row.NeedsUser,
                row.StateSince, row.ElapsedText, row.LastToolName, row.ContextFill))))
            .ToList(), ct).ConfigureAwait(false);

        return rows.Select(r =>
            {
                var snapshot = _sessionOf(r.Row.Id);
                return new YardChat(r.Row.Id, r.Row.Title, r.Workspace.Id, r.Workspace.Name, r.Row.State, r.Row.NeedsYou, r.Row.StateSince,
                    r.Row.StateFor, snapshot?.Model, r.Row.LastTool, r.Row.ContextFill, snapshot?.LastNotification, snapshot?.Cwd);
            })
            .ToList();
    }

    /// <summary>The folders its .code-workspace file lists, the root first; the root alone when there is none or it cannot be read now.</summary>
    private IReadOnlyList<YardFolder> FoldersOf(Workspace workspace)
    {
        var root = new YardFolder(WorkspaceProbe.FolderName(workspace.RootPath), workspace.RootPath);
        if (workspace.WorkspaceFile is not { } file || _foldersOf(file) is not { Count: > 0 } folders)
        {
            return [root];
        }

        return folders.Select(f => new YardFolder(f.Label, f.Path)).ToList();
    }

    private async Task<T> OnUiThreadAsync<T>(Func<T> read, CancellationToken ct)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            try
            {
                result.TrySetResult(read());
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        });
        return await result.Task.WaitAsync(UiTimeout, ct).ConfigureAwait(false);
    }

    private sealed record Row(string Id, string Title, SessionState State, bool NeedsYou, DateTimeOffset StateSince, string StateFor, string? LastTool,
        double ContextFill);
}
