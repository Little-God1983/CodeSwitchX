using CodeSwitchX.Hosting.Browsers;

namespace CodeSwitchX.Hosting.Tests.Browsers;

public class BrowserEntryTests
{
    [Fact]
    public void It_reads_as_its_label_not_as_a_record_dump() =>
        new BrowserEntry("edge|Default", "Edge: Person 1", @"C:\Program Files\Edge\edge.exe", []).ToString().ShouldBe("Edge: Person 1");

    [Fact]
    public void Firefox_is_told_by_its_kind_not_by_its_first_argument()
    {
        new BrowserEntry("firefox|a b", "Firefox", @"C:\ff\firefox.exe", ["-P", "a b"], IsFirefox: true)
            .Arguments("https://github.com/x").ShouldBe(["-P", "a b", "-new-tab", "https://github.com/x"]);
        new BrowserEntry("other|x", "Other", @"C:\o\o.exe", ["-P", "x"])
            .Arguments("https://github.com/x").ShouldBe(["-P", "x", "https://github.com/x"]);
    }
}
