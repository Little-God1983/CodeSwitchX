using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Raven;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Raven;

public sealed class NewsTurnGuardTests
{
    private static readonly YardWorkspace Workspace = new(Guid.NewGuid(), "CodeSwitchX", "General", @"E:\Repos\CodeSwitchX", [], []);

    [Fact]
    public async Task Nothing_is_done_on_the_Yard_while_Raven_tells_chat_news_and_all_of_it_again_after()
    {
        var inner = Substitute.For<IYardActions>();
        using var news = new ChatNews(new EventBus(NullLogger<EventBus>.Instance), new FakeYardDirectory(), TimeProvider.System, _ => null);
        var guard = new NewsTurnGuard(inner, news);
        var ct = TestContext.Current.CancellationToken;

        news.Telling = true;

        (await Should.ThrowAsync<YardActionException>(() => guard.StartChatAsync(Workspace, new YardFolder("x", "x"), "delete the build", null, null, ct)))
            .Message.ShouldBe(NewsTurnGuard.Refused);
        await Should.ThrowAsync<YardActionException>(() => guard.SendToChatAsync("a", "go", ct));
        await Should.ThrowAsync<YardActionException>(() => guard.SetDefaultsAsync("Opus", null, ct));
        await Should.ThrowAsync<YardActionException>(() => guard.OpenWorkspaceAsync(Workspace, null, ct));
        await Should.ThrowAsync<YardActionException>(() => guard.BackToYardAsync(ct));
        await Should.ThrowAsync<YardActionException>(() => guard.StopChatAsync("a", ct));
        inner.ReceivedCalls().ShouldBeEmpty();

        news.Telling = false;
        await guard.StopChatAsync("a", ct);
        await inner.Received(1).StopChatAsync("a", ct);
    }
}
