using System.Security.AccessControl;
using System.Security.Principal;

namespace CodeSwitchX.Core;

/// <summary>A file that holds a secret (the access token, a config with it): readable by the current user only.</summary>
public static class SecretFile
{
    public static void RestrictToCurrentUser(string file)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No current user SID.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }
}
