using System.Text.RegularExpressions;
using CodeSwitchX.Core;

namespace CodeSwitchX.Core.Tests;

public class AppVersionTests
{
    [Fact]
    public void The_version_is_the_one_the_build_stamps_from_Directory_Build_props()
    {
        // The stable build bumps <Version> there; the title bar must show that number, not one typed in by hand.
        // A build with -p:Version=... (build.ps1 one-offs) stamps something else, but build.ps1 never runs the tests.
        AppVersion.Current.ShouldBe(VersionDeclaredInProps());
    }

    [Fact]
    public void The_version_carries_no_commit_suffix()
    {
        // The SDK appends "+<commit>" to InformationalVersion; a one-off build's "-oneoff.<commit>" is part of the version and stays.
        AppVersion.Current.ShouldNotContain("+");
        AppVersion.Current.ShouldNotBe("unknown");
    }

    [Fact]
    public void Anything_but_a_Release_build_says_so()
    {
#if DEBUG
        AppVersion.Display.ShouldBe($"{AppVersion.Current} (Debug)");
#else
        AppVersion.Display.ShouldBe(AppVersion.Current);
#endif
    }

    /// <summary>
    /// The nearest Directory.Build.props above the test binaries that declares a version: tests\ has one of its own without.
    /// Skipped when none is found: binaries built with an artifacts path, or copied out of the repository, have no props above them.
    /// </summary>
    private static string VersionDeclaredInProps()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(candidate) && Regex.Match(File.ReadAllText(candidate), "<Version>([^<]+)</Version>") is { Success: true } match)
            {
                return match.Groups[1].Value.Trim();
            }
        }

        Assert.Skip("No Directory.Build.props with a <Version> above " + AppContext.BaseDirectory);
        return string.Empty;
    }
}
