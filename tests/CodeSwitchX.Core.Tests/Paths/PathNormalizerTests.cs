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
