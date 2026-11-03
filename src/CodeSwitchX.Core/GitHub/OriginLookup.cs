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
    /// Asks the folder's own git directory, never the one git would find by walking up. No origin only where that is known: no <c>.git</c>
    /// in a folder that exists, or a git directory that can be listed and has no HEAD (git would read it without complaint). A folder or
    /// git directory that cannot be reached, or a pointer that leads nowhere, is a failure. A cancelled lookup throws.
    /// </summary>
    public static async Task<OriginLookup> ReadAsync(string folder, CancellationToken ct)
    {
        string? gitDir;
        try
        {
            gitDir = await Task.Run(() => GitDirWithHead(folder), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure;
        }

        if (gitDir is null)
        {
            return NoOrigin;
        }

        var run = await GitInspector.RunProcessAsync(GitInspector.GitStartInfo(folder, $"--git-dir=\"{gitDir}\" remote get-url origin"), GitInspector.DefaultTimeout, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return From(run);
    }

    /// <returns>The folder's git directory; null when it has none or none with a HEAD. Throws <see cref="IOException"/> when that cannot be told.</returns>
    private static string? GitDirWithHead(string folder)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(folder);
        }

        var gitDir = GitInspector.GitDirOfFolder(folder, out var dangling);
        if (dangling)
        {
            throw new IOException("The pointer in .git leads nowhere");
        }

        return gitDir is not null && Directory.EnumerateFileSystemEntries(gitDir, "HEAD").Any() ? gitDir : null;
    }

    /// <summary>
    /// <c>git remote get-url</c> applies <c>insteadOf</c> rules and exits 2 when there is no such remote (<c>git config --get</c> would say 1).
    /// Every other way to end without an answer (timed out, did not start, git gave up on a broken config with 128) says nothing about the origin.
    /// </summary>
    internal static OriginLookup From(ProcessRunResult run) => run switch
    {
        { Output: { } url } => new OriginLookup(url),
        { ExitCode: 2 } => NoOrigin,
        _ => Failure,
    };
}
