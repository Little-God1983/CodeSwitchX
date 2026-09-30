using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Voice.Dictation;

namespace CodeSwitchX.UI.Raven;

/// <summary>Teaches Whisper the names of the user's workspaces and of the folders their .code-workspace files list.</summary>
public sealed class WorkspaceVocabularyProvider(IWorkspaceStore store, Func<string, IReadOnlyList<WorkspaceFolder>?> foldersOf)
    : IDictationVocabularyProvider
{
    public async Task<DictationVocabulary> GetAsync(CancellationToken ct)
    {
        var workspaces = await store.GetAllAsync(ct);
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

            foreach (var workspace in workspaces)
            {
                Add(workspace.Name);
                if (workspace.WorkspaceFile is { Length: > 0 } file && foldersOf(file) is { } folders)
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
