namespace CodeSwitchX.Core.Paths;

/// <summary>Canonical form used for every path comparison: full path, backslashes, no trailing separator, lower-case.</summary>
public static class PathNormalizer
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path.Trim()).Replace('/', '\\');
        full = full.TrimEnd('\\');
        return full.ToLowerInvariant();
    }

    /// <summary>True when <paramref name="candidate"/> equals <paramref name="root"/> or lies below it. Both must already be normalised.</summary>
    public static bool IsWithin(string candidate, string root)
    {
        if (candidate.Length == root.Length)
        {
            return string.Equals(candidate, root, StringComparison.Ordinal);
        }

        return candidate.Length > root.Length
            && candidate.StartsWith(root, StringComparison.Ordinal)
            && candidate[root.Length] == '\\';
    }
}
