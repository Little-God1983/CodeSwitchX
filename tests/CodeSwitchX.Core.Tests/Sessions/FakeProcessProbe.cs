using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Tests.Sessions;

/// <summary>Running processes by id and start time, judged the way <see cref="SystemProcessProbe"/> judges real ones.</summary>
internal sealed class FakeProcessProbe : IProcessProbe
{
    private readonly Dictionary<int, DateTimeOffset> _running = [];

    public FakeProcessProbe Run(int pid, DateTimeOffset startedAt)
    {
        _running[pid] = startedAt;
        return this;
    }

    public bool IsAlive(int pid, DateTimeOffset seenAt) => _running.TryGetValue(pid, out var started) && started <= seenAt;
}
