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
    [InlineData("2")]
    [InlineData("two")]
    [InlineData("number two")]
    [InlineData("Chat zwei")]
    public void A_workspace_is_found_by_its_number(string query)
    {
        var numbered = new[] { CodeSwitchX with { Number = 1 }, DiffusionFull with { Number = 2 }, ContentAutomatorX with { Number = 3 } };

        var match = WorkspaceMatcher.Find(query, numbered).ShouldHaveSingleItem();

        match.Workspace.Name.ShouldBe("Diffusion-Full");
        match.MatchedName.ShouldBe("Diffusion-Full", "a number means the workspace itself, so a chat starts in its root");
        match.Score.ShouldBe(1);
    }

    [Fact]
    public void A_number_no_workspace_has_finds_nothing()
    {
        WorkspaceMatcher.Find("seven", [CodeSwitchX with { Number = 1 }]).ShouldBeEmpty();
    }

    [Fact]
    public void A_number_no_workspace_has_is_matched_as_a_name()
    {
        var seven = Workspace("Seven") with { Number = 2 };

        WorkspaceMatcher.Find("seven", [CodeSwitchX with { Number = 1 }, seven]).ShouldHaveSingleItem().Workspace.ShouldBe(seven);
    }

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

    [Theory]
    [InlineData("installer of nexus")]
    [InlineData("nexus installer ui")]
    public void Short_words_among_the_words_said_are_left_out_not_counted_against(string query)
    {
        WorkspaceMatcher.Find(query, Yard).ShouldHaveSingleItem().MatchedName.ShouldBe("DiffusionNexus.Installer.SDK");
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
    public void A_short_folder_name_inside_what_was_said_is_no_match()
    {
        var shared = Workspace("Tools", ("code", @"E:\Repos\code"), ("switch", @"E:\Repos\switch"));
        var backend = Workspace("Shop", ("api", @"E:\Repos\api"), ("end", @"E:\Repos\end"));

        WorkspaceMatcher.Find("CodeSwitchX", [CodeSwitchX, shared]).ShouldHaveSingleItem().Workspace.ShouldBe(CodeSwitchX);
        WorkspaceMatcher.Find("backend api", [backend]).ShouldBeEmpty();
    }

    [Fact]
    public void A_name_that_is_most_of_what_was_said_matches()
    {
        WorkspaceMatcher.Find("CodeSwitchX app", Yard).ShouldHaveSingleItem().Workspace.ShouldBe(CodeSwitchX);
    }

    [Fact]
    public void The_best_matches_are_the_top_score_and_its_ties()
    {
        var older = Workspace("CodeSwitch");
        var twin = Workspace("Code Switch X");

        var matches = WorkspaceMatcher.Find("CodeSwitchX", [older, CodeSwitchX, twin]);

        matches.Count.ShouldBe(3);
        WorkspaceMatcher.Best(matches).Select(m => m.Workspace).ShouldBe([CodeSwitchX, twin], ignoreOrder: true);
        WorkspaceMatcher.Best([]).ShouldBeEmpty();
    }

    [Fact]
    public void German_letters_count_as_letters()
    {
        WorkspaceMatcher.Find("Bücher Regal", [Workspace("BücherRegal")]).ShouldHaveSingleItem().Score.ShouldBe(1);
    }

    [Fact]
    public void A_chat_for_a_folder_s_name_runs_in_that_folder()
    {
        var match = WorkspaceMatcher.Find("Diffusion Nexus", Yard)[0];

        WorkspaceMatcher.FolderOf(match).Path.ShouldBe(@"E:\Repos\DiffusionNexus");
    }

    [Fact]
    public void A_chat_for_the_workspace_s_own_name_runs_in_its_root()
    {
        var match = WorkspaceMatcher.Find("Diffusion Full", Yard)[0];

        WorkspaceMatcher.FolderOf(match).Path.ShouldBe(@"E:\Repos\DiffusionNexus.Installer.SDK");
    }

    [Fact]
    public void A_workspace_without_folders_runs_its_chats_in_its_root_path()
    {
        var match = WorkspaceMatcher.Find("ContentAutomatorX", Yard)[0];

        WorkspaceMatcher.FolderOf(match).Path.ShouldBe(@"E:\Repos\ContentAutomatorX");
    }

    [Theory]
    [InlineData("installer SDK", @"E:\Repos\DiffusionNexus.Installer.SDK")]
    [InlineData("diffusion nexus", @"E:\Repos\DiffusionNexus")]
    public void A_folder_named_apart_from_the_workspace_is_found_among_its_folders(string said, string path)
    {
        WorkspaceMatcher.FindFolder(said, DiffusionFull).ShouldNotBeNull().Path.ShouldBe(path);
    }

    [Fact]
    public void A_folder_the_workspace_does_not_have_is_none()
    {
        WorkspaceMatcher.FindFolder("Photoshop", DiffusionFull).ShouldBeNull();
        WorkspaceMatcher.FindFolder("", DiffusionFull).ShouldBeNull();
    }
}
