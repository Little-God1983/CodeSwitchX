namespace CodeSwitchX.Core.Paths;

/// <summary>Path forms: <see cref="Canonical"/> for storing, showing and launching; <see cref="Normalize"/> (lower-cased) for every comparison.</summary>
public static class PathNormalizer
{
    /// <summary>Comparison key: the canonical path lower-cased.</summary>
    public static string Normalize(string path) => Canonical(path).ToLowerInvariant();

    /// <summary>
    /// Full path with backslashes and no trailing separator (kept on a drive root, where "c:" alone would be
    /// drive-relative and resolve to the current directory), in the casing the user gave.
    /// </summary>
    public static string Canonical(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path.Trim()).Replace('/', '\\');
        return Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>True when <paramref name="candidate"/> equals <paramref name="root"/> or lies below it. Both must already be normalised.</summary>
    public static bool IsWithin(string candidate, string root)
    {
        if (candidate.Length == root.Length)
        {
            return string.Equals(candidate, root, StringComparison.Ordinal);
        }

        if (candidate.Length < root.Length || !candidate.StartsWith(root, StringComparison.Ordinal))
        {
            return false;
        }

        // A drive root already ends with its separator; every other root needs one right after it.
        return root.EndsWith('\\') || candidate[root.Length] == '\\';
    }
}
