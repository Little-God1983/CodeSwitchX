using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CodeSwitchX.Data;

/// <summary>Used only by <c>dotnet ef</c> when generating migrations.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CodeSwitchXDbContext>
{
    public CodeSwitchXDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CodeSwitchXDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new CodeSwitchXDbContext(options);
    }
}
