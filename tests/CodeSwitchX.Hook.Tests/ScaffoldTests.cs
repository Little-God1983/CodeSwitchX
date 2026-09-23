namespace CodeSwitchX.Hook.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Referenced_assembly_loads()
    {
        System.Reflection.Assembly.Load("csx-hook").GetName().Name.ShouldBe("csx-hook");
    }
}
