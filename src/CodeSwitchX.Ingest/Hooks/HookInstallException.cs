namespace CodeSwitchX.Ingest.Hooks;

public sealed class HookInstallException(string message, Exception? inner = null) : Exception(message, inner);
