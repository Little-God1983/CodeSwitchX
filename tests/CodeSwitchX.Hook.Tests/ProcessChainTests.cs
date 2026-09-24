using CodeSwitchX.Hook;

namespace CodeSwitchX.Hook.Tests;

public class ProcessChainTests
{
    [Fact]
    public void Walks_parents_from_a_table_and_stops_at_the_root_or_depth()
    {
        var table = new Dictionary<int, (int Parent, string Name)>
        {
            [10] = (20, "csx-hook.exe"),
            [20] = (30, "cmd.exe"),
            [30] = (40, "claude.exe"),
            [40] = (0, "Code.exe"),
        };

        ProcessChain.Ancestors(10, table, maxDepth: 8).ShouldBe([new ProcessInfo(20, "cmd.exe"), new ProcessInfo(30, "claude.exe"), new ProcessInfo(40, "Code.exe")]);
        ProcessChain.Ancestors(10, table, maxDepth: 2).ShouldBe([new ProcessInfo(20, "cmd.exe"), new ProcessInfo(30, "claude.exe")]);
        ProcessChain.Ancestors(99, table, maxDepth: 8).ShouldBeEmpty();
    }

    [Fact]
    public void Cycles_in_the_table_terminate()
    {
        var table = new Dictionary<int, (int Parent, string Name)> { [1] = (2, "a"), [2] = (1, "b") };

        ProcessChain.Ancestors(1, table, maxDepth: 8).Count.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public void Live_snapshot_finds_the_test_hosts_parent()
    {
        var chain = ProcessChain.Ancestors(maxDepth: 8);

        chain.ShouldNotBeEmpty();
        chain.ShouldAllBe(p => p.Pid != Environment.ProcessId && p.Name.Length > 0);
    }
}
