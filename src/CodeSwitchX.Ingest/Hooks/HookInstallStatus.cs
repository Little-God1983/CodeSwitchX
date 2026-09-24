namespace CodeSwitchX.Ingest.Hooks;

public enum HookInstallState
{
    NotInstalled,
    Partial,
    Outdated,
    Installed,
}

public sealed record HookInstallStatus(HookInstallState State, IReadOnlyList<string> InstalledEvents, IReadOnlyList<string> MissingEvents, string SettingsFile);

public sealed record HookInstallResult(bool Changed, string? BackupFile);
