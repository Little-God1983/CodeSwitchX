using CodeSwitchX.Hosting.Browsers;

namespace CodeSwitchX.Hosting.Tests.Browsers;

public class BrowserEntryTests
{
    [Fact]
    public void It_reads_as_its_label_not_as_a_record_dump() =>
        new BrowserEntry("edge|Default", "Edge: Person 1", @"C:\Program Files\Edge\edge.exe", []).ToString().ShouldBe("Edge: Person 1");
}
