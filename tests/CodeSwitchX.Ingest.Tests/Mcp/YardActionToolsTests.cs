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
    public async Task A_chat_for_Diffusion_Nexus_asks_for_Diffusion_Full_s_DiffusionNexus_folder()
    {
        var started = await Tools.StartChat("Diffusion Nexus", cancellationToken: Ct);

        var (workspace, folder, model, effort) = _actions.Started.ShouldNotBeNull();
        workspace.Name.ShouldBe("Diffusion-Full");
        folder.ShouldNotBeNull("found by that folder's name, the chat is meant for that folder: VS Code refuses it if it cannot, never starts it elsewhere")
            .Path.ShouldBe(@"E:\Repos\DiffusionNexus");
        (model, effort).ShouldBe((null, null));
        started.Chat.Workspace.ShouldBe("Diffusion-Full");
    }

    [Fact]
    public async Task A_chat_for_the_workspace_s_own_name_asks_for_no_folder()
    {
        await Tools.StartChat("Diffusion Full", cancellationToken: Ct);

        _actions.Started!.Value.Folder.ShouldBeNull();
    }

    [Fact]
    public async Task The_new_chat_s_name_comes_back_with_what_to_do_next()
    {
        var started = await Tools.StartChat("Diffusion Full", cancellationToken: Ct);

        started.Chat.SendTo.ShouldBe("diffusionnexus-4f");
        started.Next.ShouldContain("SendMessage to \"diffusionnexus-4f\"");
    }

    [Fact]
    public async Task A_folder_named_apart_is_passed_on()
    {
        await Tools.StartChat("Diffusion Full", folder: "Diffusion Nexus", cancellationToken: Ct);

        _actions.Started!.Value.Folder!.Path.ShouldBe(@"E:\Repos\DiffusionNexus");
    }

    [Fact]
    public async Task A_model_and_effort_for_this_one_chat_are_passed_on_as_said()
    {
        await Tools.StartChat("CodeSwitchX", model: "Fable", effort: "extra high", cancellationToken: Ct);

        (_actions.Started!.Value.Model, _actions.Started.Value.Effort).ShouldBe(("Fable", "extra high"));
    }

    [Theory]
    [InlineData("Photoshop", null, "No workspace matches 'Photoshop'")]
    [InlineData("Diffusion Full", "Photoshop", "Diffusion-Full has no folder like 'Photoshop'")]
    public async Task A_start_that_cannot_be_placed_is_refused_and_nothing_starts(string workspace, string? folder, string message)
    {
        var error = await Should.ThrowAsync<McpException>(() => Tools.StartChat(workspace, folder, cancellationToken: Ct));

        error.Message.ShouldContain(message);
        _actions.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_name_two_workspaces_fit_equally_is_asked_back()
    {
        _yard.Workspaces.Add(_yard.Workspaces[0] with { Id = Guid.NewGuid(), Name = "CodeSwitchX" });

        var error = await Should.ThrowAsync<McpException>(() => Tools.StartChat("CodeSwitchX", cancellationToken: Ct));

        error.Message.ShouldContain("Ask the user which one");
    }

    [Fact]
    public async Task What_the_Yard_refuses_reaches_the_brain_in_its_words()
    {
        _actions.Refusal = "'GPT' is no model Raven knows.";

        var error = await Should.ThrowAsync<McpException>(() => Tools.StartChat("CodeSwitchX", model: "GPT", cancellationToken: Ct));

        error.Message.ShouldBe("'GPT' is no model Raven knows.");
    }

    [Fact]
    public async Task A_window_too_busy_to_answer_reaches_the_brain_as_something_to_say()
    {
        _actions.Failure = new TimeoutException("The operation has timed out.");

        var error = await Should.ThrowAsync<McpException>(() => Tools.BackToYard(Ct));

        error.Message.ShouldBe("CodeSwitchX's window did not respond in time, so that was not done. Say it again in a moment.");
    }

    [Fact]
    public async Task Defaults_are_set_and_come_back()
    {
        var defaults = await Tools.SetDefaults("Opus", cancellationToken: Ct);

        defaults.ShouldBe(new ChatDefaults("Opus", null));
    }

    [Fact]
    public async Task Opening_a_chat_opens_its_workspace()
    {
        await Tools.OpenWorkspace(chat: "cccccccc", cancellationToken: Ct);

        _actions.Opened.ShouldNotBeNull().Name.ShouldBe("Diffusion-Full");
    }

    [Fact]
    public async Task Opening_a_workspace_by_name_finds_it_like_the_other_tools()
    {
        await Tools.OpenWorkspace("code switch ex", cancellationToken: Ct);

        _actions.Opened!.Name.ShouldBe("CodeSwitchX");
    }

    [Fact]
    public async Task Opening_needs_a_workspace_or_a_chat_it_can_find()
    {
        (await Should.ThrowAsync<McpException>(() => Tools.OpenWorkspace(cancellationToken: Ct))).Message.ShouldContain("Say which");
        (await Should.ThrowAsync<McpException>(() => Tools.OpenWorkspace(chat: "zzzz", cancellationToken: Ct))).Message.ShouldContain("no chat 'zzzz'");
        _actions.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Back_to_the_Yard_goes_to_the_actions()
    {
        (await Tools.BackToYard(Ct)).ShouldBe("The Yard is shown.");

        _actions.Calls.ShouldBe(["back_to_yard"]);
    }
}
