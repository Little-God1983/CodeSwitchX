namespace CodeSwitchX.UI.Settings;

/// <summary>The pages of the Settings window: every setting is on exactly one of them.</summary>
public enum SettingsPage
{
    Voice,
    Listening,
    Brain,
    Shortcuts,
    ClaudeCode,
    Usage,
    Privacy,
}

/// <summary>A page as the sidebar lists it.</summary>
/// <param name="Group">The heading it is listed under: Raven, or CodeSwitchX.</param>
/// <param name="Icon">A Segoe Fluent Icons glyph.</param>
/// <param name="Subtitle">One line under the page's title.</param>
/// <param name="Keywords">What the settings on it are called, for the search box.</param>
public sealed record SettingsPageItem(SettingsPage Page, string Group, string Title, string Icon, string Subtitle, IReadOnlyList<string> Keywords)
{
    /// <summary>Whether the search finds the page: by its title or a setting on it. A blank search finds every page.</summary>
    public bool Matches(string? search) =>
        string.IsNullOrWhiteSpace(search)
        || Title.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || Keywords.Any(k => k.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<SettingsPageItem> All { get; } =
    [
        new(SettingsPage.Voice, "Raven", "Voice", "", "How Raven speaks. Pick an engine, hear its voices and install it right here.",
            ["engine", "Kokoro", "Qwen3-TTS", "voice", "install", "sample", "speak chat news", "text only"]),
        new(SettingsPage.Listening, "Raven", "Listening", "", "How Raven hears you: the microphone and the speech-to-text model.",
            ["microphone", "mic", "level", "speech to text", "Whisper", "download", "talk over", "barge in"]),
        new(SettingsPage.Brain, "Raven", "Brain & chats", "", "The model Raven thinks with, and what the chats it starts run with.",
            ["model", "brain", "effort", "model names", "alias", "new chats", "Opus", "Sonnet", "Haiku", "Fable"]),
        new(SettingsPage.Shortcuts, "Raven", "Shortcuts", "\uE765", "Keys that switch Raven's chat from anywhere, VS Code too.",
            ["hotkey", "shortcut", "keys", "chat", "switch", "F12", "previous", "next", "taken"]),
        new(SettingsPage.ClaudeCode, "CodeSwitchX", "Claude Code", "", "The hooks that let every Claude Code session report its state.",
            ["hooks", "install", "remove", "settings file", "relay", "csx-hook"]),
        new(SettingsPage.Usage, "CodeSwitchX", "Usage", "", "A reminder line for the tokens of the 5-hour window.",
            ["budget", "tokens", "5-hour", "telemetry"]),
        new(SettingsPage.Privacy, "CodeSwitchX", "Privacy & data", "", "What CodeSwitchX keeps, and where.",
            ["payloads", "privacy", "data folder", "logs", "database"]),
    ];
}
