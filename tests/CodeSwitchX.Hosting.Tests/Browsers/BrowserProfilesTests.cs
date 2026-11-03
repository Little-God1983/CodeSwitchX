using CodeSwitchX.Hosting.Browsers;

namespace CodeSwitchX.Hosting.Tests.Browsers;

public class BrowserProfilesTests
{
    [Fact]
    public void Firefox_reads_the_names_of_the_profile_sections_only()
    {
        const string ini = "[Install4F96D1932A9F858E]\nDefault=Profiles/abc.install\nLocked=1\n\n"
            + "[Profile1]\nName=work stuff\nIsRelative=1\nPath=Profiles/abc.work\n\n"
            + "[General]\nStartWithLastProfile=1\n\n"
            + "[Profile0]\r\nName=default-release\r\nIsRelative=1\r\nDefault=1\r\n";

        BrowserProfiles.Firefox(ini).ShouldBe(["work stuff", "default-release"]);
    }

    [Fact]
    public void Chromium_reads_the_profile_info_cache()
    {
        const string json = """{"profile":{"info_cache":{"Default":{"name":"Person 1"},"Profile 1":{"name":"Side project"}}}}""";

        BrowserProfiles.Chromium(json).ShouldBe([("Default", "Person 1"), ("Profile 1", "Side project")]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"profile":{"info_cache":[]}}""")]
    public void Chromium_with_a_missing_or_broken_file_gives_no_profiles(string json) =>
        BrowserProfiles.Chromium(json).ShouldBeEmpty();
}
