using System.Text.RegularExpressions;
using CodeSwitchX.Core;

namespace CodeSwitchX.Core.Tests;

public class AppVersionTests
{
    [Fact]
    public void The_version_is_the_one_the_build_stamps_from_Directory_Build_props()
    {
        // The stable build bumps <Version> there; the title bar must show that number, not one typed in by hand.
        AppVersion.Current.ShouldBe(VersionDeclaredInProps());
    }

    [Fact]
    public void The_version_is_a_bare_number_without_a_commit_suffix()
    {
        AppVersion.Current.ShouldMatch(@"^\d+\.\d+\.\d+(\.\d+)?$");
    }

    /// <summary>The nearest Directory.Build.props above the test binaries that declares a version: tests\ has one of its own without.</summary>
    private static string VersionDeclaredInProps()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(candidate) && Regex.Match(File.ReadAllText(candidate), "<Version>([^<]+)</Version>") is { Success: true } match)
            {
                return match.Groups[1].Value;
            }
        }

        throw new FileNotFoundException("No Directory.Build.props with a <Version> above " + AppContext.BaseDirectory);
    }
}
