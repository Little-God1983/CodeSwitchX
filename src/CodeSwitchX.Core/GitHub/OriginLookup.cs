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

    /// <summary>
    /// Asks the folder's own git directory, never the one git would find by walking up: a <c>.git</c> without a HEAD is no repository and
    /// has no origin, not the origin of one above it. A folder that cannot be reached is a failure; a cancelled lookup throws.
    /// </summary>
    public static async Task<OriginLookup> ReadAsync(string folder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (gitDir, valid) = await Task.Run(() => new GitInspector().GitDirOf(folder) is { } dir ? (dir, File.Exists(Path.Combine(dir, "HEAD"))) : (null, false), ct).ConfigureAwait(false);
        if (gitDir is null)
        {
            return Directory.Exists(folder) ? NoOrigin : Failure;
        }

        // git config would read an empty directory without complaint.
        if (!valid)
        {
            return NoOrigin;
        }

        var run = await GitInspector.RunProcessAsync(GitInspector.GitStartInfo(folder, $"--git-dir=\"{gitDir}\" config --get remote.origin.url"), GitInspector.DefaultTimeout, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return From(run);
    }

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
