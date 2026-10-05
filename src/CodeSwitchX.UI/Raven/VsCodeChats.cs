using System.Collections.Concurrent;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Hosting.VsCode.Companion;
using CodeSwitchX.Ingest.Live;
using System.IO;
using Microsoft.Extensions.Logging;
using Path = System.IO.Path;

namespace CodeSwitchX.UI.Raven;

/// <summary>Opens Claude Code chats in VS Code.</summary>
public interface IVsCodeChats
{
    /// <summary>
    /// Opens a new chat tab in the workspace's VS Code window, starting VS Code hidden when it does not run, and returns
    /// once the chat's Claude Code runs, empty: its first message comes by <c>SendMessage</c>.
    /// </summary>
    /// <param name="folder">The folder the user named for it; null for wherever VS Code starts a chat (its first folder).</param>
    /// <param name="model">A full model id; null for VS Code's own.</param>
    /// <param name="effort">An effort level; null for VS Code's own.</param>
    /// <exception cref="YardActionException">It could not be opened; the message says why.</exception>
    Task<VsCodeChat> StartAsync(Workspace workspace, string? folder, string? model, string? effort, CancellationToken ct);

    /// <summary>
    /// Closes the chat's tab in the VS Code window it runs in, and returns once its Claude Code has ended. The conversation
    /// stays in Claude Code's session list.
    /// </summary>
    /// <exception cref="YardActionException">It could not be closed; the message says why.</exception>
    Task CloseAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Shows the chat in the workspace's VS Code window (#115): its tab comes to the front, or is opened with the chat's
    /// history when it has none there. VS Code is started hidden when it does not run.
    /// </summary>
    /// <exception cref="YardActionException">It could not be shown; the message says why.</exception>
    Task ShowAsync(Workspace workspace, string sessionId, CancellationToken ct);
}

/// <param name="Folder">The folder it runs in.</param>
/// <param name="SendTo">The name it is messaged by (<c>SendMessage</c>).</param>
public sealed record VsCodeChat(string SessionId, string Folder, string SendTo);

/// <summary>
/// A chat started through the companion extension (<c>vscode-companion/</c>) of the workspace's window. VS Code starts
/// the tab's claude.exe as a child of that window's extension host, which the companion runs in, and Claude Code records
/// it in <c>~/.claude/sessions</c> before any message: the new chat is a record that was not there before, whose process
/// that host started, and which has no conversation yet. Other tabs of the window can start their claude.exe meanwhile: a
/// window VS Code just started restores the tabs it had, and the user may open one by hand. A restored tab goes on a
/// conversation that is on disk, so it is never taken, and only a process started after the tab was asked for counts; of
/// two empty tabs that both fit, the newer process is taken, and both are empty chats in that window. Chats are started
/// one at a time per folder, so two starts never take each other's chat or model.
/// </summary>
public sealed class VsCodeChats : IVsCodeChats
{
    /// <summary>How long a start waits for the companion of a window VS Code is just opening (VS Code from cold takes a while).</summary>
    internal static readonly TimeSpan CompanionWait = TimeSpan.FromSeconds(60);

    /// <summary>How long without a companion in an open window before it is installed there: it starts within seconds of that.</summary>
    internal static readonly TimeSpan InstallAfter = TimeSpan.FromSeconds(10);

    /// <summary>How long a new tab's Claude Code may take to record itself; it does within a second or two.</summary>
    internal static readonly TimeSpan NewChatWait = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How much earlier than the request to open the tab its claude.exe may have started and still count: the process's
    /// start and this clock are not read alike. There is no bound after: in a window that just started, the tab's process
    /// may come long after VS Code has answered.
    /// </summary>
    internal static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(5);

    /// <summary>How long a closed tab's Claude Code may take to end; one in the middle of a turn takes a few seconds.</summary>
    internal static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(20);

    /// <summary>The first companion that can close a chat; a window still running an older one has not been reloaded since the update.</summary>
    internal static readonly Version ClosesSince = new(0, 2, 0);

    /// <summary>The first companion that can show a given chat.</summary>
    internal static readonly Version ShowsSince = new(0, 3, 0);

    private readonly ICompanionWindows _windows;
    private readonly ICompanionInstaller _installer;
    private readonly Func<Workspace, CancellationToken, Task<string?>> _openVsCode;
    private readonly Func<IReadOnlySet<int>?, IReadOnlyList<LiveChat>> _running;
    private readonly Func<IReadOnlyDictionary<int, int>> _parents;
    private readonly Func<string, bool> _hasConversation;
    private readonly string _pendingSettings;
    private readonly TimeProvider _time;
    private readonly ILogger<VsCodeChats> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _folders = new();

    /// <param name="openVsCode">Opens the workspace's VS Code without showing it (<c>HostManager.OpenAsync</c>); null once it runs, else why not.</param>
    /// <param name="running">The chats open in VS Code tabs right now, but those of the processes given (<see cref="ClaudeLiveSessions.RunningNow"/>).</param>
    /// <param name="parents">Every running process with its parent, from one snapshot.</param>
    /// <param name="hasConversation">Whether a session has a conversation on disk: one it goes on, not a new chat.</param>
    /// <param name="pendingSettings">Where what puts a folder's settings back is kept while a chat starts (<see cref="StartSettings"/>).</param>
    public VsCodeChats(ICompanionWindows windows, ICompanionInstaller installer, Func<Workspace, CancellationToken, Task<string?>> openVsCode,
        Func<IReadOnlySet<int>?, IReadOnlyList<LiveChat>> running, Func<IReadOnlyDictionary<int, int>> parents, Func<string, bool> hasConversation,
        string pendingSettings, TimeProvider time, ILogger<VsCodeChats> logger)
    {
        _pendingSettings = pendingSettings;
        _windows = windows;
        _installer = installer;
        _openVsCode = openVsCode;
        _running = running;
        _parents = parents;
        _hasConversation = hasConversation;
        _time = time;
        _logger = logger;
    }

    public async Task<VsCodeChat> StartAsync(Workspace workspace, string? folder, string? model, string? effort, CancellationToken ct)
    {
        var window = _windows.Find(workspace) ?? await OpenWindowAsync(workspace, ct).ConfigureAwait(false);

        // VS Code starts every new chat in the window's first folder; it has no way to start one elsewhere.
        var chatFolder = window.Folders.FirstOrDefault() ?? workspace.RootPath;
        if (folder is not null && PathNormalizer.Normalize(folder) != PathNormalizer.Normalize(chatFolder))
        {
            throw new YardActionException($"VS Code starts new chats in {NameOf(chatFolder)}, the first folder of {workspace.Name}; it cannot start one in {NameOf(folder)}.");
        }

        var gate = _folders.GetOrAdd(PathNormalizer.Normalize(chatFolder), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = StartSettings.Apply(chatFolder, model, effort, _pendingSettings);
            try
            {
                return await OpenChatAsync(workspace, window, chatFolder, ct).ConfigureAwait(false);
            }
            finally
            {
                settings?.Dispose();
                if (settings is { Restored: false })
                {
                    _logger.LogWarning("{File} could not be put back yet; the next start of CodeSwitchX does it", StartSettings.FileIn(chatFolder));
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Opens VS Code for the workspace, hidden, and waits for its companion; installs the companion when the window has none.</summary>
    private async Task<CompanionWindow> OpenWindowAsync(Workspace workspace, CancellationToken ct)
    {
        if (await _openVsCode(workspace, ct).ConfigureAwait(false) is { } failed)
        {
            throw new YardActionException($"VS Code could not be opened for {workspace.Name}: {failed}");
        }

        var started = _time.GetUtcNow();
        var installed = false;
        while (true)
        {
            if (_windows.Find(workspace) is { } window)
            {
                return window;
            }

            var waited = _time.GetUtcNow() - started;
            if (waited >= CompanionWait)
            {
                throw new YardActionException($"The VS Code window of {workspace.Name} does not run the CodeSwitchX companion. Reload that window "
                    + "(Developer: Reload Window) and try again.");
            }

            if (!installed && waited >= InstallAfter)
            {
                installed = true;
                var install = await _installer.EnsureAsync([workspace.VsCodeProfile], ct).ConfigureAwait(false);
                if (install.Failed is [var why, ..])
                {
                    throw new YardActionException($"The CodeSwitchX companion could not be installed into VS Code ({why}), so no chat can be opened there.");
                }

                // The install may have waited behind the one at startup: the companion gets its own time to start after it.
                started = _time.GetUtcNow();
            }

            await Task.Delay(Poll, _time, ct).ConfigureAwait(false);
        }
    }

    private async Task<VsCodeChat> OpenChatAsync(Workspace workspace, CompanionWindow window, string chatFolder, CancellationToken ct)
    {
        // Known from here on, and never looked at again: each look reads only the records that came since.
        var known = _running(null).Select(c => c.Pid).ToHashSet();
        var sent = _time.GetUtcNow();
        var answer = await _windows.SendAsync(window, CompanionWindows.NewChat, null, ct).ConfigureAwait(false);
        if (!answer.Ok)
        {
            throw new YardActionException($"VS Code could not open a chat in {workspace.Name}: {answer.Error}");
        }

        var host = answer.Pid ?? window.Pid;
        var since = (sent - StartSlack).ToFileTime();
        var until = _time.GetUtcNow() + NewChatWait;
        while (true)
        {
            if (NewChatOf(host, since, known) is { } chat)
            {
                _logger.LogInformation("Opened chat {Id} ({Name}) in VS Code for {Workspace}", chat.SessionId, chat.Name, workspace.Name);
                return new VsCodeChat(chat.SessionId, chatFolder, chat.Name);
            }

            if (_time.GetUtcNow() >= until)
            {
                throw new YardActionException($"VS Code opened a chat tab in {workspace.Name}, but its Claude Code did not start within "
                    + $"{NewChatWait.TotalSeconds:0} seconds. Look at the tab in VS Code.");
            }

            await Task.Delay(Poll, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The chat's window is the one whose extension host started its claude.exe; its companion closes the tab, and the
    /// close is done once that process is gone. A chat run in a terminal has no record of a VS Code tab and is not found.
    /// </summary>
    public async Task CloseAsync(string sessionId, CancellationToken ct)
    {
        var chat = _running(null).FirstOrDefault(c => string.Equals(c.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new YardActionException("That chat is not open in a VS Code tab, so there is nothing to close.");
        var window = _parents().TryGetValue(chat.Pid, out var host) ? _windows.Of(host) : null;
        if (window is null)
        {
            throw new YardActionException("The VS Code window that chat runs in does not run the CodeSwitchX companion, so it cannot be closed from "
                + "here. Close its tab in VS Code.");
        }

        Requires(window, ClosesSince, "The VS Code window that chat runs in still runs an older CodeSwitchX companion, which cannot close chats. "
            + "Reload that window (Developer: Reload Window) and try again.");

        var answer = await _windows.SendAsync(window, CompanionWindows.CloseChat, chat.SessionId, ct).ConfigureAwait(false);
        if (!answer.Ok)
        {
            throw new YardActionException($"VS Code did not close the chat: {answer.Error}");
        }

        var until = _time.GetUtcNow() + CloseWait;
        while (_running(null).Any(c => c.Pid == chat.Pid))
        {
            if (_time.GetUtcNow() >= until)
            {
                throw new YardActionException($"VS Code closed the chat's tab, but its Claude Code still runs after {CloseWait.TotalSeconds:0} seconds.");
            }

            await Task.Delay(Poll, _time, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Closed chat {Id} ({Name}) in VS Code", chat.SessionId, chat.Name);
    }

    public async Task ShowAsync(Workspace workspace, string sessionId, CancellationToken ct)
    {
        var window = _windows.Find(workspace) ?? await OpenWindowAsync(workspace, ct).ConfigureAwait(false);
        Requires(window, ShowsSince, $"The VS Code window of {workspace.Name} still runs an older CodeSwitchX companion, which cannot show a "
            + "chat. Reload that window (Developer: Reload Window) and try again.");

        // Open in a tab of another window (the same folder opened twice), it would be opened here a second time: two
        // Claude Codes on one conversation.
        var running = _running(null).FirstOrDefault(c => string.Equals(c.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        if (running is not null && _parents().TryGetValue(running.Pid, out var host) && host != window.Pid)
        {
            throw new YardActionException($"That chat is open in another VS Code window, not the one of {workspace.Name}. Look for its tab there.");
        }

        var answer = await _windows.SendAsync(window, CompanionWindows.OpenChat, sessionId, ct).ConfigureAwait(false);
        if (!answer.Ok)
        {
            throw new YardActionException($"VS Code did not show the chat: {answer.Error}");
        }

        _logger.LogInformation("Showed chat {Id} in VS Code for {Workspace}", sessionId, workspace.Name);
    }

    /// <summary>Refuses a window whose companion is older than the first that can do what is asked; one whose version cannot be read is older.</summary>
    private static void Requires(CompanionWindow window, Version since, string otherwise)
    {
        if (!Version.TryParse(window.Version, out var version) || version < since)
        {
            throw new YardActionException(otherwise);
        }
    }

    /// <summary>
    /// The newest chat that came since, that the window's host started after the tab was asked for (<paramref name="since"/>,
    /// a UTC file time), and that has no conversation yet; null for none so far. A chat seen once and found to be none
    /// is added to <paramref name="known"/>; one whose parent is not in the snapshot yet (it started after it) is looked
    /// at again.
    /// </summary>
    private LiveChat? NewChatOf(int host, long since, HashSet<int> known)
    {
        var fresh = _running(known);
        if (fresh.Count == 0)
        {
            return null;
        }

        var parents = _parents();
        var candidates = new List<LiveChat>();
        foreach (var chat in fresh)
        {
            if (!parents.TryGetValue(chat.Pid, out var parent))
            {
                continue;
            }

            if (parent == host && chat.ProcessStart >= since && !_hasConversation(chat.SessionId))
            {
                candidates.Add(chat);
            }
            else
            {
                known.Add(chat.Pid);
            }
        }

        return candidates.MaxBy(c => c.ProcessStart);
    }

    /// <summary>
    /// Whether the session has a conversation on disk: Claude Code writes <c>projects\&lt;folder&gt;\&lt;id&gt;.jsonl</c>
    /// with its first message, so a chat that goes on one has it and a new chat does not yet. Never throws; a folder that
    /// cannot be read counts as having it, so a doubtful chat is not taken for the new one.
    /// </summary>
    public static bool HasConversation(string projectsDirectory, string sessionId)
    {
        try
        {
            return Directory.Exists(projectsDirectory)
                && Directory.EnumerateDirectories(projectsDirectory).Any(project => File.Exists(Path.Combine(project, sessionId + ".jsonl")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string NameOf(string folder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;
}
