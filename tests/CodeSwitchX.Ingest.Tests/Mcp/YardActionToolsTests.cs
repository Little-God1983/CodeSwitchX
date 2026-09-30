using CodeSwitchX.Core.Yard;
using CodeSwitchX.Ingest.Mcp;
using ModelContextProtocol;

namespace CodeSwitchX.Ingest.Tests.Mcp;

public sealed class YardActionToolsTests
{
    private readonly FakeYard _yard = new();
    private readonly FakeActions _actions = new();
    private YardActionTools Tools => new(_yard, _actions);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_chat_for_Diffusion_Nexus_runs_in_Diffusion_Full_s_DiffusionNexus_folder()
    {
        var started = await Tools.StartChat("Diffusion Nexus", " Filter the LoRA list by base model. ", cancellationToken: Ct);

        var (workspace, folder, prompt, model, effort) = _actions.Started.ShouldNotBeNull();
        workspace.Name.ShouldBe("Diffusion-Full");
        folder.Path.ShouldBe(@"E:\Repos\DiffusionNexus");
        prompt.ShouldBe("Filter the LoRA list by base model.");
        (model, effort).ShouldBe((null, null));
        started.Chat.Folder.ShouldBe("DiffusionNexus");
        started.Chat.Workspace.ShouldBe("Diffusion-Full");
    }

    [Fact]
    public async Task A_chat_for_the_workspace_s_own_name_runs_in_its_root()
    {
        await Tools.StartChat("Diffusion Full", "Update the icons.", cancellationToken: Ct);

        _actions.Started!.Value.Folder.Path.ShouldBe(@"E:\Repos\DiffusionNexus.Installer.SDK");
    }

    [Fact]
    public async Task A_folder_named_apart_runs_the_chat_there()
    {
        await Tools.StartChat("Diffusion Full", "Update the icons.", folder: "Diffusion Nexus", cancellationToken: Ct);

        _actions.Started!.Value.Folder.Path.ShouldBe(@"E:\Repos\DiffusionNexus");
    }

    [Fact]
    public async Task A_model_and_effort_for_this_one_chat_are_passed_on_as_said()
    {
        await Tools.StartChat("CodeSwitchX", "Fix the tray.", model: "Fable", effort: "extra high", cancellationToken: Ct);

        (_actions.Started!.Value.Model, _actions.Started.Value.Effort).ShouldBe(("Fable", "extra high"));
    }

    [Theory]
    [InlineData("Photoshop", "Fix it.", null, "No workspace matches 'Photoshop'")]
    [InlineData("CodeSwitchX", "  ", null, "the prompt is empty")]
    [InlineData("Diffusion Full", "Fix it.", "Photoshop", "Diffusion-Full has no folder like 'Photoshop'")]
    public async Task A_start_that_cannot_be_placed_is_refused_and_nothing_starts(string workspace, string prompt, string? folder, string message)
    {
        var error = await Should.ThrowAsync<McpException>(() => Tools.StartChat(workspace, prompt, folder, cancellationToken: Ct));

        error.Message.ShouldContain(message);
        _actions.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_name_two_workspaces_fit_equally_is_asked_back()
    {
        _yard.Workspaces.Add(_yard.Workspaces[0] with { Id = Guid.NewGuid(), Name = "CodeSwitchX" });

        var error = await Should.ThrowAsync<McpException>(() => Tools.StartChat("CodeSwitchX", "Fix it.", cancellationToken: Ct));

        error.Message.ShouldContain("Ask the user which one");
    }

    [Fact]
    public async Task What_the_Yard_refuses_reaches_the_brain_in_its_words()
    {
        _actions.Refusal = "'GPT' is no model Raven knows.";

        var error = await Should.ThrowAsync<McpException>(() => Tools.StartChat("CodeSwitchX", "Fix it.", model: "GPT", cancellationToken: Ct));

        error.Message.ShouldBe("'GPT' is no model Raven knows.");
    }

    [Fact]
    public async Task Words_for_a_chat_are_sent_on()
    {
        var chat = await Tools.SendToChat("dddddddd", " Add tests too. ", Ct);

        _actions.Calls.ShouldBe(["send_to_chat dddddddd: Add tests too."]);
        chat.State.ShouldBe("working");
    }

    [Fact]
    public async Task Defaults_are_set_and_come_back()
    {
        var defaults = await Tools.SetDefaults("Opus", cancellationToken: Ct);

        defaults.ShouldBe(new ChatDefaults("Opus", null));
    }

    [Fact]
    public async Task Opening_a_chat_Raven_started_hands_it_to_the_actions()
    {
        _actions.Voice.Add(new VoiceChatView("dddddddd-0004", FakeYard.DiffusionId, "Diffusion-Full", @"E:\Repos\DiffusionNexus", null, null, true, "auto"));

        await Tools.OpenWorkspace(chat: "dddddddd", cancellationToken: Ct);

        _actions.Opened.ShouldBe((null, "dddddddd"));
    }

    [Fact]
    public async Task Opening_a_chat_VS_Code_runs_opens_its_workspace()
    {
        await Tools.OpenWorkspace(chat: "cccccccc", cancellationToken: Ct);

        var (workspace, chat) = _actions.Opened.ShouldNotBeNull();
        workspace!.Name.ShouldBe("Diffusion-Full");
        chat.ShouldBeNull();
    }

    [Fact]
    public async Task Opening_a_workspace_by_name_finds_it_like_the_other_tools()
    {
        await Tools.OpenWorkspace("code switch ex", cancellationToken: Ct);

        _actions.Opened!.Value.Workspace!.Name.ShouldBe("CodeSwitchX");
    }

    [Fact]
    public async Task Opening_needs_a_workspace_or_a_chat_it_can_find()
    {
        (await Should.ThrowAsync<McpException>(() => Tools.OpenWorkspace(cancellationToken: Ct))).Message.ShouldContain("Say which");
        (await Should.ThrowAsync<McpException>(() => Tools.OpenWorkspace(chat: "zzzz", cancellationToken: Ct))).Message.ShouldContain("no chat 'zzzz'");
        _actions.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Back_to_the_Yard_and_stop_go_to_the_actions()
    {
        (await Tools.BackToYard(Ct)).ShouldBe("The Yard is shown.");
        (await Tools.StopChat("dddddddd", Ct)).ShouldBe("stopped");

        _actions.Calls.ShouldBe(["back_to_yard", "stop_chat dddddddd"]);
    }
}
