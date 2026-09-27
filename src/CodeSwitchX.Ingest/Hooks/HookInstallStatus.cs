namespace CodeSwitchX.Ingest.Hooks;

public enum HookInstallState
{
    NotInstalled,
    Partial,
    Outdated,
    Installed,

    /// <summary>settings.json cannot be read or used (see <see cref="HookInstallStatus.Problem"/>), so whether the hooks are there is unknown.</summary>
    Unreadable,
}

public sealed record HookInstallStatus(HookInstallState State, IReadOnlyList<string> InstalledEvents, IReadOnlyList<string> MissingEvents, string SettingsFile,
    string? Problem = null);

public sealed record HookInstallResult(bool Changed, string? BackupFile);
