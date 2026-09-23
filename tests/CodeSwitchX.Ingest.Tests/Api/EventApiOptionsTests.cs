using System.Security.Principal;
using CodeSwitchX.Ingest.Api;

namespace CodeSwitchX.Ingest.Tests.Api;

public class EventApiOptionsTests
{
    [Fact]
    public void Default_pipe_name_is_scoped_to_the_current_users_sid()
    {
        var sid = WindowsIdentity.GetCurrent().User.ShouldNotBeNull().Value;

        new EventApiOptions().PipeName.ShouldBe("CodeSwitchX-" + sid, "a name derived from the display name collides across users and can be squatted");
    }
}
