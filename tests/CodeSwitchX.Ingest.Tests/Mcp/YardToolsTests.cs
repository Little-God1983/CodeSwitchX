using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Ingest.Mcp;
using ModelContextProtocol;

namespace CodeSwitchX.Ingest.Tests.Mcp;

public sealed class YardToolsTests
{
    private readonly FakeYard _yard = new();
    private YardTools Tools => new(_yard);

    [Fact]
    public async Task Workspaces_list_their_track_folders_git_lines_and_chat_counts()
    {
        var workspaces = await Tools.ListWorkspaces(CancellationToken.None);

        workspaces.Select(w => w.Name).ShouldBe(["CodeSwitchX", "Diffusion-Full"]);
        var codeSwitchX = workspaces[0];
        codeSwitchX.Track.ShouldBe("Tools");
        codeSwitchX.Git.ShouldBe(["main, clean"]);
        (codeSwitchX.ChatsNeedingYou, codeSwitchX.ChatsWorking, codeSwitchX.Chats).ShouldBe((1, 1, 2));
        var diffusion = workspaces[1];
        diffusion.Folders.ShouldBe(["DiffusionNexus.Installer.SDK", "DiffusionNexus"]);
        diffusion.Git.ShouldBe(["DiffusionNexus.Installer.SDK: develop, 3 changed", "DiffusionNexus: main"]);
    }

    [Fact]
    public async Task A_folder_that_is_no_repository_says_so()
    {
        _yard.Workspaces[0] = _yard.Workspaces[0] with { Git = [new(null, null, null)] };

        (await Tools.ListWorkspaces(CancellationToken.None))[0].Git.ShouldBe(["no git"]);
    }

    [Fact]
    public async Task Diffusion_Nexus_finds_Diffusion_Full_through_its_folder()
    {
        var matches = await Tools.FindWorkspace("Diffusion Nexus", CancellationToken.None);

        matches[0].Workspace.Name.ShouldBe("Diffusion-Full");
        matches[0].MatchedName.ShouldBe("DiffusionNexus");
        matches[0].Score.ShouldBe(1);
    }

    [Fact]
    public async Task A_name_that_matches_nothing_finds_nothing()
    {
        (await Tools.FindWorkspace("Photoshop", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Needs_me_lists_the_chats_waiting_for_the_user()
    {
        var chats = await Tools.ListChats("needs_me", cancellationToken: CancellationToken.None);

        var chat = chats.ShouldHaveSingleItem();
        chat.Title.ShouldBe("Raven brain");
        chat.Workspace.ShouldBe("CodeSwitchX");
        chat.State.ShouldBe("needs you");
        chat.For.ShouldBe("5m");
        chat.Context.ShouldBe("10%");
    }

    [Theory]
    [InlineData("working", new[] { "Speech gate" })]
    [InlineData("live", new[] { "Speech gate", "Raven brain", "Installer icons" })]
    [InlineData("all", new[] { "Speech gate", "Raven brain", "Installer icons" })]
    [InlineData(" Working ", new[] { "Speech gate" })]
    public async Task The_filter_picks_the_chats(string filter, string[] titles)
    {
        (await Tools.ListChats(filter, cancellationToken: CancellationToken.None)).Select(c => c.Title).ShouldBe(titles);
    }

    [Fact]
    public async Task Live_leaves_out_ended_chats()
    {
        _yard.Chats[2] = _yard.Chats[2] with { State = SessionState.Ended };

        (await Tools.ListChats("live", cancellationToken: CancellationToken.None)).Select(c => c.Title).ShouldBe(["Speech gate", "Raven brain"]);
        (await Tools.ListChats("all", cancellationToken: CancellationToken.None)).Last().State.ShouldBe("ended");
    }

    [Fact]
    public async Task Arguments_sent_as_null_count_as_left_out()
    {
        (await Tools.ListChats(null!, null, CancellationToken.None)).Count.ShouldBe(3);
        (await Tools.FindWorkspace(null!, CancellationToken.None)).ShouldBeEmpty();
        await Should.ThrowAsync<McpException>(() => Tools.GetChat(null!, CancellationToken.None));
    }

    [Fact]
    public async Task A_chat_whose_turn_is_over_is_idle_and_not_waiting_on_the_user()
    {
        var chats = await Tools.ListChats("all", cancellationToken: CancellationToken.None);

        chats.Single(c => c.Title == "Installer icons").State.ShouldBe("idle, its turn is over");
        (await Tools.ListChats("needs_me", cancellationToken: CancellationToken.None)).ShouldNotContain(c => c.Title == "Installer icons");
    }

    [Fact]
    public async Task An_unknown_filter_is_refused_with_the_ones_that_work()
    {
        var error = await Should.ThrowAsync<McpException>(() => Tools.ListChats("busy", cancellationToken: CancellationToken.None));

        error.Message.ShouldContain("needs_me, working, live, all");
    }

    [Fact]
    public async Task The_chats_of_a_workspace_are_found_by_its_spoken_name()
    {
        var chats = await Tools.ListChats("all", "code switch x", CancellationToken.None);

        chats.Select(c => c.Title).ShouldBe(["Speech gate", "Raven brain"]);
    }

    [Fact]
    public async Task A_workspace_that_only_looks_like_the_name_lends_it_no_chats()
    {
        var older = Guid.NewGuid();
        _yard.Workspaces.Add(new(older, "CodeSwitch", "Tools", @"E:\Repos\CodeSwitch", [], []));
        _yard.Chats.Add(_yard.Chats[0] with { Id = "dddddddd-0004", Title = "Old version", WorkspaceId = older, Workspace = "CodeSwitch" });

        var chats = await Tools.ListChats("all", "CodeSwitchX", CancellationToken.None);

        chats.Select(c => c.Title).ShouldBe(["Speech gate", "Raven brain"]);
    }

    [Fact]
    public async Task A_workspace_name_that_matches_nothing_is_refused()
    {
        await Should.ThrowAsync<McpException>(() => Tools.ListChats("all", "Photoshop", CancellationToken.None));
    }

    [Fact]
    public async Task A_chat_is_found_by_the_start_of_its_id()
    {
        var chat = await Tools.GetChat("bbbbbbbb", CancellationToken.None);

        chat.Title.ShouldBe("Raven brain");
        chat.LastNotification.ShouldBe("Claude needs your permission to use Bash");
        chat.Folder.ShouldBe(@"E:\Repos\CodeSwitchX");
        chat.LastTool.ShouldBe("AskUserQuestion");
    }

    [Theory]
    [InlineData("")]
    [InlineData("zzzz")]
    public async Task No_chat_is_refused(string id)
    {
        await Should.ThrowAsync<McpException>(() => Tools.GetChat(id, CancellationToken.None));
    }

    [Fact]
    public async Task An_id_that_fits_several_chats_is_refused()
    {
        _yard.Chats.Add(_yard.Chats[0] with { Id = "aaaaaaaa-0004" });

        var error = await Should.ThrowAsync<McpException>(() => Tools.GetChat("aaaaaaaa", CancellationToken.None));

        error.Message.ShouldContain("2 chats");
    }
}
