namespace CodeSwitchX.Data.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Referenced_assembly_loads()
    {
        System.Reflection.Assembly.Load("CodeSwitchX.Data").GetName().Name.ShouldBe("CodeSwitchX.Data");
    }
}
