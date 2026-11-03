using CodeSwitchX.Hosting.Browsers;

namespace CodeSwitchX.Hosting.Tests.Browsers;

public class InstalledBrowsersTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\X\x.exe"" -url ""%1""", @"C:\Program Files\X\x.exe")]
    [InlineData(@"  ""C:\X\x.exe""  ", @"C:\X\x.exe")]
    [InlineData(@"C:\X\x.exe -url ""%1""", @"C:\X\x.exe")]
    [InlineData(@"C:\Program Files\X\x.exe -url ""%1""", @"C:\Program Files\X\x.exe")]
    [InlineData(@"C:\Program Files\X\x.EXE", @"C:\Program Files\X\x.EXE")]
    [InlineData(@"C:\X\x.cmd", @"C:\X\x.cmd")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData(@"""C:\X\x.exe", null)]
    [InlineData(@"""""", null)]
    public void The_program_of_a_command_line_is_found(string? command, string? expected) =>
        InstalledBrowsers.ExecutableOf(command).ShouldBe(expected);

    [Fact]
    public void One_name_that_cannot_be_read_leaves_the_others_listed()
    {
        var found = InstalledBrowsers.Collect(["Alpha", "Broken", "Gamma"], name => name switch
        {
            "Broken" => throw new UnauthorizedAccessException("denied"),
            _ => $@"""C:\{name}\{name}.exe"" ""%1""",
        });

        found.ShouldBe([("Alpha", @"C:\Alpha\Alpha.exe"), ("Gamma", @"C:\Gamma\Gamma.exe")]);
    }
}
