using CodeSwitchX.Core.Paths;

namespace CodeSwitchX.Core.Tests.Paths;

public class PathNormalizerTests
{
    [Theory]
    [InlineData(@"C:\Repo\App", @"c:\repo\app")]
    [InlineData(@"C:\Repo\App\", @"c:\repo\app")]
    [InlineData(@"C:/Repo/App/src/../", @"c:\repo\app")]
    [InlineData(@"  C:\Repo\App  ", @"c:\repo\app")]
    [InlineData(@"C:\", @"c:")]
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
    [InlineData(@"c:\repo\app", @"c:", true)]
    public void IsWithin_requires_a_separator_boundary(string candidate, string root, bool expected)
    {
        PathNormalizer.IsWithin(candidate, root).ShouldBe(expected);
    }
}
