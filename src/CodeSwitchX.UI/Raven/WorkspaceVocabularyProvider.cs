using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Voice.Dictation;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// Teaches Whisper the names of the user's workspaces and of the folders their .code-workspace files list. Asked on every
/// press of the mic, so the vocabulary is kept: read again once a workspace is registered or unregistered or the roots
/// are reloaded, and after <see cref="MaxAge"/>, since an edited .code-workspace folder list announces nothing.
/// </summary>
public sealed class WorkspaceVocabularyProvider : IDictationVocabularyProvider, IDisposable
{
    /// <summary>How long a vocabulary is kept when nothing announces a change.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly IWorkspaceStore _store;
    private readonly Func<string, IReadOnlyList<WorkspaceFolder>?> _foldersOf;
    private readonly TimeProvider _time;
    private readonly IDisposable[] _subscriptions;
    private readonly Lock _gate = new();
    private DictationVocabulary? _kept;
    private long _keptAt;

    /// <summary>Counts the changes: a read that a change overtook is not kept.</summary>
    private long _changes;

    public WorkspaceVocabularyProvider(IWorkspaceStore store, Func<string, IReadOnlyList<WorkspaceFolder>?> foldersOf, IEventBus bus,
        TimeProvider time)
    {
        _store = store;
        _foldersOf = foldersOf;
        _time = time;
        _subscriptions =
        [
            bus.Subscribe<WorkspaceRegistered>(_ => Forget()),
            bus.Subscribe<WorkspaceUnregistered>(_ => Forget()),
            bus.Subscribe<WorkspaceRootsChanged>(_ => Forget()),
        ];
    }

    public async Task<DictationVocabulary> GetAsync(CancellationToken ct)
    {
        long changes;
        lock (_gate)
        {
            if (_kept is { } kept && _time.GetElapsedTime(_keptAt) < MaxAge)
            {
                return kept;
            }

            changes = _changes;
        }

        var vocabulary = await ReadAsync(ct);
        lock (_gate)
        {
            if (_changes == changes)
            {
                _kept = vocabulary;
                _keptAt = _time.GetTimestamp();
            }
        }

        return vocabulary;
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    private void Forget()
    {
        lock (_gate)
        {
            _changes++;
            _kept = null;
        }
    }

    private async Task<DictationVocabulary> ReadAsync(CancellationToken ct)
    {
        var workspaces = await _store.GetAllAsync(ct);
        var words = await Task.Run(() =>
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();

            void Add(string? word)
            {
                if (!string.IsNullOrWhiteSpace(word) && seen.Add(word))
                {
                    list.Add(word);
                }
            }

            // "Chat" first, as in "chat three", the switch the app hears itself (#121); then every workspace name before any
            // folder label: the prompt keeps the first words when the list is long.
            Add("Chat");
            foreach (var workspace in workspaces)
            {
                Add(workspace.Name);
            }

            foreach (var workspace in workspaces)
            {
                if (workspace.WorkspaceFile is { Length: > 0 } file && _foldersOf(file) is { } folders)
                {
                    foreach (var folder in folders)
                    {
                        Add(folder.Label);
                    }
                }
            }

            return list;
        }, ct);

        return new DictationVocabulary(words, []);
    }
}
