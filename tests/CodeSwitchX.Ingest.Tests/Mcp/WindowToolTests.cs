using CodeSwitchX.Core.Yard;
using CodeSwitchX.Ingest.Mcp;
using ModelContextProtocol;
using Shouldly;

namespace CodeSwitchX.Ingest.Tests.Mcp;

/// <summary>set_window (#117): how the brain may say the state, and what reaches the app.</summary>
public sealed class WindowToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("minimize", WindowRequest.Minimize)]
    [InlineData("Minimise", WindowRequest.Minimize)]
    [InlineData("hide", WindowRequest.Minimize)]
    [InlineData("maximize", WindowRequest.Maximize)]
    [InlineData("full screen", WindowRequest.Maximize)]
    [InlineData("restore", WindowRequest.Restore)]
    [InlineData("bring back", WindowRequest.Front)]
    [InlineData("bring it back", WindowRequest.Front)]
    [InlineData("maximize it", WindowRequest.Maximize)]
    [InlineData("minimize CodeSwitchX", WindowRequest.Minimize)]
    [InlineData("get out of the way", WindowRequest.Minimize)]
    [InlineData("show it", WindowRequest.Front)]
    [InlineData("get out of my way", WindowRequest.Minimize)]
    [InlineData("back to normal", WindowRequest.Restore)]
    [InlineData("normal size", WindowRequest.Restore)]
    [InlineData("bring it to the front", WindowRequest.Front)] // #222
    [InlineData("to front", WindowRequest.Front)]
    [InlineData("foreground", WindowRequest.Front)]
    [InlineData("focus", WindowRequest.Front)]
    [InlineData("switch to CodeSwitchX", WindowRequest.Front)]
    [InlineData("front", WindowRequest.Front)]
    [InlineData("minimised", WindowRequest.Minimize)]
    [InlineData("maximised", WindowRequest.Maximize)]
    public async Task The_state_as_the_brain_says_it_reaches_the_app(string said, WindowRequest request)
    {
        var actions = new FakeActions();

        (await new YardActionTools(new FakeYard(), actions).SetWindow(said, Ct)).ShouldBe($"CodeSwitchX: {request}.");

        actions.Window.ShouldBe(request);
    }

    [Fact]
    public async Task Another_state_is_refused_and_nothing_changes()
    {
        var actions = new FakeActions();

        var error = await Should.ThrowAsync<McpException>(() => new YardActionTools(new FakeYard(), actions).SetWindow("wiggle", Ct));

        error.Message.ShouldContain("minimize, maximize or restore");
        actions.Window.ShouldBeNull();
    }

    [Fact]
    public async Task A_refusal_of_the_app_comes_back_in_words()
    {
        var actions = new FakeActions { Refusal = "No window." };

        (await Should.ThrowAsync<McpException>(() => new YardActionTools(new FakeYard(), actions).SetWindow("maximize", Ct))).Message.ShouldBe("No window.");
    }
}
