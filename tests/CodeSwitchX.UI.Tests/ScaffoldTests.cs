namespace CodeSwitchX.UI.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Referenced_assembly_loads()
    {
        System.Reflection.Assembly.Load("CodeSwitchX").GetName().Name.ShouldBe("CodeSwitchX");
    }
}
