namespace CodeSwitchX.Ingest.ClaudeSettings;

/// <summary>Claude Code's settings.json cannot be read, used or written; the message says which and why.</summary>
public sealed class ClaudeSettingsException(string message, Exception? inner = null) : Exception(message, inner);
