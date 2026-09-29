namespace CodeSwitchX.Core.Tests;

public class ProcessNamesakesTests
{
    [Fact]
    public void Only_namesakes_in_this_session_count()
    {
        // Two accounts on one workstation: A's CodeSwitchX runs on in A's disconnected session and cannot have hidden B's windows.
        (int Pid, int SessionId)[] namesakes = [(10, 1), (20, 2), (30, 2), (40, 3)];

        ProcessNamesakes.InSession(namesakes, selfPid: 20, selfSessionId: 2).ShouldBe([30]);
        ProcessNamesakes.InSession(namesakes, selfPid: 40, selfSessionId: 3).ShouldBeEmpty();
    }
}
