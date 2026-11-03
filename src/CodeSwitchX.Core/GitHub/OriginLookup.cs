using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.GitHub;

/// <summary>
/// What a folder's git says its <c>origin</c> is. A folder without one has no repository any more; a git that gave no answer
/// says nothing about it, so the folder keeps the repository it had.
/// </summary>
public sealed record OriginLookup(string? Url, bool Failed = false)
{
    public static readonly OriginLookup NoOrigin = new(null, Failed: false);

    public static readonly OriginLookup Failure = new(null, Failed: true);

    public static async Task<OriginLookup> ReadAsync(string folder, CancellationToken ct) =>
        From(await GitInspector.RunProcessAsync(GitInspector.GitStartInfo(folder, "config --get remote.origin.url"), GitInspector.DefaultTimeout, ct).ConfigureAwait(false));

    /// <summary>
    /// <c>git config --get</c> exits 1 when the key is not set. Every other way to end without an answer (timed out, did not
    /// start, cancelled, git gave up on a broken config with 128) says nothing about the origin.
    /// </summary>
    internal static OriginLookup From(ProcessRunResult run) => run switch
    {
        { Output: { } url } => new OriginLookup(url),
        { ExitCode: 1 } => NoOrigin,
        _ => Failure,
    };
}
