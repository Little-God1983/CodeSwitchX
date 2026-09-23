namespace CodeSwitchX.Telemetry.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Referenced_assembly_loads()
    {
        System.Reflection.Assembly.Load("CodeSwitchX.Telemetry").GetName().Name.ShouldBe("CodeSwitchX.Telemetry");
    }
}
