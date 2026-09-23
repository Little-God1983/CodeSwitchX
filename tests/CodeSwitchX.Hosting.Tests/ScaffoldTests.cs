namespace CodeSwitchX.Hosting.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Referenced_assembly_loads()
    {
        System.Reflection.Assembly.Load("CodeSwitchX.Hosting").GetName().Name.ShouldBe("CodeSwitchX.Hosting");
    }
}
