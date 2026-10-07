using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Core.Tests.Yard;

public sealed class AskedChatsTests
{
    private const string Chat = "4f0c1b9e-2d3a-4c5b-8e6f-7a8b9c0d1e2f";
    private readonly AskedChats _asked = new();

    [Fact]
    public void A_chat_is_asked_until_as_many_ends_as_begins()
    {
        _asked.Begin(Chat);
        _asked.Begin(Chat);
        _asked.End(Chat);
        _asked.IsAsked(Chat).ShouldBeTrue();
        _asked.IsAsked(Chat.ToUpperInvariant()).ShouldBeTrue("the header is a guid, whatever its case");
        _asked.End(Chat);
        _asked.IsAsked(Chat).ShouldBeFalse();
        _asked.End(Chat); // one too many is nothing
        _asked.IsAsked(Chat).ShouldBeFalse();
    }

    [Fact]
    public void A_chat_is_unverified_until_it_is_not()
    {
        _asked.Unverified(Chat, true);
        _asked.IsUnverified(Chat.ToUpperInvariant()).ShouldBeTrue();
        _asked.Unverified(Chat, false);
        _asked.IsUnverified(Chat).ShouldBeFalse();
    }

    [Fact]
    public void A_warning_is_said_once()
    {
        _asked.FirstTime("NoIds").ShouldBeTrue();
        _asked.FirstTime("NoIds").ShouldBeFalse();
        _asked.FirstTime("other").ShouldBeTrue();
    }
}
