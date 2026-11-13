using System.IO;
using System.Text.RegularExpressions;
using Path = System.IO.Path;

namespace CodeSwitchX.UI.Yard;

/// <summary>
/// A repository that carries the stable-build scripts (#423): <c>scripts\build.ps1</c> next to a <c>scripts\_common.ps1</c>
/// that names the Start Menu shortcut the build writes. The tile's menu builds it and starts it.
/// </summary>
/// <param name="RepoPath">The repository's folder, as its git line has it.</param>
/// <param name="ShortcutName">The shortcut's file name in the Start Menu's Programs folder, e.g. "Some App.lnk".</param>
public sealed partial record StableBuildRepo(string RepoPath, string ShortcutName)
{
    public string BuildScript => Path.Combine(RepoPath, "scripts", "build.ps1");

    /// <summary>The repository's scripts, or null when it has none, or they cannot be read.</summary>
    public static StableBuildRepo? Find(string repoPath)
    {
        try
        {
            var scripts = Path.Combine(repoPath, "scripts");
            var common = Path.Combine(scripts, "_common.ps1");
            if (!File.Exists(Path.Combine(scripts, "build.ps1")) || !File.Exists(common))
            {
                return null;
            }

            return ShortcutLine().Match(File.ReadAllText(common)) is { Success: true } match
                ? new StableBuildRepo(repoPath, match.Groups[1].Value)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex("""^\s*\$script:ShortcutName\s*=\s*['"]([^'"]+)['"]""", RegexOptions.Multiline)]
    private static partial Regex ShortcutLine();
}

/// <summary>Whether a line's git state lets a stable build start (#423): the user's precheck, before the script's own stricter ones.</summary>
public static class StableBuildCheck
{
    /// <summary>Null when the build may start; otherwise why not, for the greyed entry's tooltip.</summary>
    public static string? BuildBlock(GitLine line)
    {
        if (line.Branch is null || line.DirtyCount is null)
        {
            return "Git state unknown";
        }

        if (line.Branch is not ("main" or "develop"))
        {
            return $"On {line.Branch} - a stable build runs from main or develop";
        }

        return line.DirtyCount switch
        {
            0 => null,
            1 => "1 uncommitted change - commit or stash first",
            var n => $"{n} uncommitted changes - commit or stash first",
        };
    }
}
