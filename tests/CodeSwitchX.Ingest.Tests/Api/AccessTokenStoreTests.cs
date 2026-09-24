using CodeSwitchX.Core;
using CodeSwitchX.Ingest.Api;

namespace CodeSwitchX.Ingest.Tests.Api;

public class AccessTokenStoreTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-token-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    [Fact]
    public void Creates_a_64_hex_token_once_and_reuses_it()
    {
        var store = new AccessTokenStore(_paths);

        var first = store.GetOrCreate();
        var second = store.GetOrCreate();

        first.Length.ShouldBe(64);
        first.ShouldAllBe(c => Uri.IsHexDigit(c));
        second.ShouldBe(first);
        File.ReadAllText(_paths.TokenFile).Trim().ShouldBe(first);
    }

    [Fact]
    public void Token_file_is_readable_only_by_the_current_user()
    {
        new AccessTokenStore(_paths).GetOrCreate();

        var rules = new FileInfo(_paths.TokenFile).GetAccessControl()
            .GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .ToList();

        rules.ShouldHaveSingleItem().IdentityReference.ShouldBe(System.Security.Principal.WindowsIdentity.GetCurrent().User);
    }
}
