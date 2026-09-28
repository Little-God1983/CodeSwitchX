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
        var full = Path.GetFullPath(WithoutDevicePrefix(path.Trim())).Replace('/', '\\');
        return Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>
    /// The <c>\\?\</c> and <c>\\.\</c> prefixes in front of a drive path or a share (<c>UNC\</c>) name the same folder;
    /// nothing shows or compares them. Anything else behind them (a volume GUID, a drive without a root) is left as it is:
    /// stripped, it would resolve against the current directory.
    /// </summary>
    private static string WithoutDevicePrefix(string path)
    {
        if (path.Length < 4 || path[0] != '\\' || path[1] != '\\' || path[2] is not ('?' or '.') || path[3] != '\\')
        {
            return path;
        }

        var rest = path.AsSpan(4);
        if (rest.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(@"\\", rest[4..]);
        }

        return rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] == '\\' ? rest.ToString() : path;
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
