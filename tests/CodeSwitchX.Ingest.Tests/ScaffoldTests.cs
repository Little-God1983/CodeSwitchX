namespace CodeSwitchX.Ingest.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Referenced_assembly_loads()
    {
        System.Reflection.Assembly.Load("CodeSwitchX.Ingest").GetName().Name.ShouldBe("CodeSwitchX.Ingest");
    }
}
