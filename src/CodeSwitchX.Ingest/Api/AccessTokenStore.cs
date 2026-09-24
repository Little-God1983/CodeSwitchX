using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using CodeSwitchX.Core;

namespace CodeSwitchX.Ingest.Api;

/// <summary>Per-install random secret shared by the API and the relay; stored with a current-user-only ACL.</summary>
public sealed class AccessTokenStore
{
    private readonly AppPaths _paths;
    private readonly Lock _gate = new();
    private string? _cached;

    public AccessTokenStore(AppPaths paths)
    {
        _paths = paths;
    }

    public string GetOrCreate()
    {
        lock (_gate)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (File.Exists(_paths.TokenFile))
            {
                var existing = File.ReadAllText(_paths.TokenFile).Trim();
                if (existing.Length == 64 && existing.All(Uri.IsHexDigit))
                {
                    return _cached = existing;
                }
            }

            _paths.EnsureCreated();
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            File.WriteAllText(_paths.TokenFile, token);
            RestrictToCurrentUser(_paths.TokenFile);
            return _cached = token;
        }
    }

    public static bool Matches(string token, string? candidate)
    {
        if (candidate is null || candidate.Length != token.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(token), System.Text.Encoding.ASCII.GetBytes(candidate));
    }

    private static void RestrictToCurrentUser(string file)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No current user SID.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }
}
