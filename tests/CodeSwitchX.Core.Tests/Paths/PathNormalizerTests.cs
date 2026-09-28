using CodeSwitchX.Core.Paths;

namespace CodeSwitchX.Core.Tests.Paths;

public class PathNormalizerTests
{
    [Theory]
    [InlineData(@"C:\Repo\App", @"c:\repo\app")]
    [InlineData(@"C:\Repo\App\", @"c:\repo\app")]
    [InlineData(@"C:/Repo/App/src/../", @"c:\repo\app")]
    [InlineData(@"  C:\Repo\App  ", @"c:\repo\app")]
    [InlineData(@"C:\", @"c:\")]
    [InlineData(@"c:\", @"c:\")]
    [InlineData(@"\\?\C:\Repo\App", @"c:\repo\app")]
    [InlineData(@"\\?\UNC\server\share\app\", @"\\server\share\app")]
    [InlineData(@"\\.\C:\Repo\App", @"c:\repo\app")]
    [InlineData(@"\\.\UNC\server\share\app", @"\\server\share\app")]
    [InlineData(@"\\?\Volume{b75e2c83-0000-0000-0000-602200000000}\Repo", @"\\?\volume{b75e2c83-0000-0000-0000-602200000000}\repo")] // a volume without a drive letter is left as it is
    [InlineData(@"\\?\C:", @"\\?\c:")] // drive-relative behind the prefix: stripping it would resolve against the current directory
    public void Normalize_produces_a_canonical_form(string input, string expected)
    {
        PathNormalizer.Normalize(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"c:\repo\app", @"c:\repo\app", true)]
    [InlineData(@"c:\repo\app\src", @"c:\repo\app", true)]
    [InlineData(@"c:\repo\app2", @"c:\repo\app", false)]
    [InlineData(@"c:\repo\app2\src", @"c:\repo\app", false)]
    [InlineData(@"c:\repo", @"c:\repo\app", false)]
    [InlineData(@"c:\repo\app", @"c:\", true)]
    [InlineData(@"d:\x", @"c:\", false)]
    public void IsWithin_requires_a_separator_boundary(string candidate, string root, bool expected)
    {
        PathNormalizer.IsWithin(candidate, root).ShouldBe(expected);
    }

    [Fact]
    public void Normalize_is_idempotent_for_drive_roots()
    {
        // "c:" alone would be drive-relative and resolve to the process's current directory on that drive.
        PathNormalizer.Normalize(PathNormalizer.Normalize(@"D:\")).ShouldBe(@"d:\");
    }

    [Theory]
    [InlineData(@"C:/Repo/App/", @"C:\Repo\App")]
    [InlineData(@"  C:\Repo\App  ", @"C:\Repo\App")]
    [InlineData(@"D:\", @"D:\")]
    public void Canonical_keeps_the_real_casing_for_display_and_launch(string input, string expected)
    {
        PathNormalizer.Canonical(input).ShouldBe(expected);
    }
}
