using System.Collections.Concurrent;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Hosting.VsCode.Companion;
using CodeSwitchX.Ingest.Live;
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
}

/// <param name="Folder">The folder it runs in.</param>
/// <param name="SendTo">The name it is messaged by (<c>SendMessage</c>).</param>
public sealed record VsCodeChat(string SessionId, string Folder, string SendTo);

/// <summary>
/// A chat started through the companion extension (<c>vscode-companion/</c>) of the workspace's window. VS Code starts
/// the tab's claude.exe as a child of that window's extension host, which the companion runs in, and Claude Code records
/// it in <c>~/.claude/sessions</c> before any message: the new chat is the record that was not there before and whose
/// process that host started. Chats are started one at a time per folder, so two starts never take each other's chat or
/// each other's model.
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

    private readonly ICompanionWindows _windows;
    private readonly ICompanionInstaller _installer;
    private readonly Func<Workspace, CancellationToken, Task<string?>> _openVsCode;
    private readonly Func<IReadOnlyList<LiveChat>> _running;
    private readonly Func<int, int?> _parentOf;
    private readonly TimeProvider _time;
    private readonly ILogger<VsCodeChats> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _folders = new();

    /// <param name="openVsCode">Opens the workspace's VS Code without showing it (<c>HostManager.OpenAsync</c>); null once it runs, else why not.</param>
    /// <param name="running">The chats open in VS Code tabs right now (<see cref="ClaudeLiveSessions.RunningNow"/>).</param>
    /// <param name="parentOf">The process that started a process; null when it is gone.</param>
    public VsCodeChats(ICompanionWindows windows, ICompanionInstaller installer, Func<Workspace, CancellationToken, Task<string?>> openVsCode,
        Func<IReadOnlyList<LiveChat>> running, Func<int, int?> parentOf, TimeProvider time, ILogger<VsCodeChats> logger)
    {
        _windows = windows;
        _installer = installer;
        _openVsCode = openVsCode;
        _running = running;
        _parentOf = parentOf;
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
            var settings = StartSettings.Apply(chatFolder, model, effort);
            try
            {
                return await OpenChatAsync(workspace, window, chatFolder, ct).ConfigureAwait(false);
            }
            finally
            {
                settings?.Dispose();
                if (settings is { Restored: false })
                {
                    _logger.LogWarning("{File} was not put back as it was: it changed while the chat started", StartSettings.FileIn(chatFolder));
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
            }

            await Task.Delay(Poll, _time, ct).ConfigureAwait(false);
        }
    }

    private async Task<VsCodeChat> OpenChatAsync(Workspace workspace, CompanionWindow window, string chatFolder, CancellationToken ct)
    {
        var before = _running().Select(c => c.Pid).ToHashSet();
        var answer = await _windows.SendAsync(window, "newChat", ct).ConfigureAwait(false);
        if (!answer.Ok)
        {
            throw new YardActionException($"VS Code could not open a chat in {workspace.Name}: {answer.Error}");
        }

        var host = answer.Pid ?? window.Pid;
        var until = _time.GetUtcNow() + NewChatWait;
        while (true)
        {
            if (_running().FirstOrDefault(c => !before.Contains(c.Pid) && _parentOf(c.Pid) == host) is { } chat)
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

    private static string NameOf(string folder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;
}
