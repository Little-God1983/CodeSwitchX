using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Core.Tests.Yard;

public sealed class WorkspaceMatcherTests
{
    private static YardWorkspace Workspace(string name, params (string Name, string Path)[] folders) =>
        new(Guid.NewGuid(), name, "Track", folders.Length > 0 ? folders[0].Path : $@"E:\Repos\{name}",
            folders.Select(f => new YardFolder(f.Name, f.Path)).ToList(), []);

    private static readonly YardWorkspace CodeSwitchX = Workspace("CodeSwitchX", ("CodeSwitchX", @"E:\Repos\CodeSwitchX"));

    private static readonly YardWorkspace DiffusionFull = Workspace("Diffusion-Full",
        ("DiffusionNexus.Installer.SDK", @"E:\Repos\DiffusionNexus.Installer.SDK"), ("DiffusionNexus", @"E:\Repos\DiffusionNexus"));

    private static readonly YardWorkspace ContentAutomatorX = Workspace("ContentAutomatorX");

    private static readonly YardWorkspace[] Yard = [CodeSwitchX, DiffusionFull, ContentAutomatorX];

    [Theory]
    [InlineData("Diffusion Nexus")]
    [InlineData("diffusion nexus")]
    [InlineData("Diffusion-Nexus")]
    [InlineData("DiffusionNexus")]
    public void Diffusion_Nexus_finds_Diffusion_Full_through_its_folder(string query)
    {
        var match = WorkspaceMatcher.Find(query, Yard).ShouldHaveSingleItem();

        match.Workspace.ShouldBe(DiffusionFull);
        match.MatchedName.ShouldBe("DiffusionNexus");
        match.Score.ShouldBe(1);
    }

    [Theory]
    [InlineData("CodeSwitchX")]
    [InlineData("code switch x")]
    [InlineData("Code-Switch-X")]
    public void A_workspace_is_found_by_its_own_name_however_it_is_spelt(string query)
    {
        WorkspaceMatcher.Find(query, Yard).ShouldHaveSingleItem().Workspace.ShouldBe(CodeSwitchX);
    }

    [Theory]
    [InlineData("code switch ex")]
    [InlineData("Codeswitch")]
    [InlineData("content automator")]
    public void A_misheard_or_shortened_name_is_still_found(string query)
    {
        WorkspaceMatcher.Find(query, Yard).ShouldNotBeEmpty();
        WorkspaceMatcher.Find(query, Yard)[0].Score.ShouldBeGreaterThanOrEqualTo(WorkspaceMatcher.Threshold);
    }

    [Fact]
    public void The_words_of_a_folder_name_in_another_order_find_it()
    {
        var match = WorkspaceMatcher.Find("installer nexus", Yard).ShouldHaveSingleItem();

        match.Workspace.ShouldBe(DiffusionFull);
        match.MatchedName.ShouldBe("DiffusionNexus.Installer.SDK");
    }

    [Fact]
    public void A_folder_is_found_by_its_own_name_when_the_workspace_file_names_it_differently()
    {
        var renamed = Workspace("Site", ("Frontend", @"E:\Repos\shop-web"));

        WorkspaceMatcher.Find("shop web", [renamed]).ShouldHaveSingleItem().MatchedName.ShouldBe("shop-web");
    }

    [Fact]
    public void Several_matches_come_best_first()
    {
        var diffusion = Workspace("Diffusion");

        var matches = WorkspaceMatcher.Find("Diffusion", [DiffusionFull, diffusion]);

        matches.Select(m => m.Workspace).ShouldBe([diffusion, DiffusionFull]);
        matches[0].Score.ShouldBe(1);
        matches[1].Score.ShouldBeLessThan(1);
    }

    [Theory]
    [InlineData("Photoshop")]
    [InlineData("")]
    [InlineData("  - ")]
    [InlineData("x")]
    public void Nothing_like_any_name_finds_nothing(string query)
    {
        WorkspaceMatcher.Find(query, Yard).ShouldBeEmpty();
    }

    [Fact]
    public void Diffusion_Nexus_is_not_taken_for_Diffusion_Full_by_its_name_alone()
    {
        WorkspaceMatcher.Find("Diffusion Nexus", [Workspace("Diffusion-Full")]).ShouldBeEmpty();
    }

    [Fact]
    public void German_letters_count_as_letters()
    {
        WorkspaceMatcher.Find("Bücher Regal", [Workspace("BücherRegal")]).ShouldHaveSingleItem().Score.ShouldBe(1);
    }
}
