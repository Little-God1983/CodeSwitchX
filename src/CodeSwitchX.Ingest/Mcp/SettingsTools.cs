using System.ComponentModel;
using CodeSwitchX.Core.Yard;
using ModelContextProtocol.Server;
using static CodeSwitchX.Ingest.Mcp.ToolActs;

namespace CodeSwitchX.Ingest.Mcp;

/// <summary>
/// CodeSwitchX's own settings by voice (#126): list them, say what one is set to, change one, open Settings at a page.
/// What cannot be done comes back as a tool error in words the brain can repeat. Changing a setting or opening Settings is
/// refused a Raven chat's brain in a turn no question of its user's started, by the server's filter (<see cref="ToolActs.AskedOnly"/>, #193).
/// </summary>
[McpServerToolType]
public sealed class SettingsTools(IAppSettings settings)
{
    [McpServerTool(Name = "list_settings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists CodeSwitchX's own settings you can read and change by voice: each with its name, Settings page, what it does and "
        + "the values it takes. Call it when the user asks about a setting by a name you are not sure of. A setting with byVoice "
        + "false is not changed by voice: open_settings at its page, and tell the user why (notByVoice).")]
    public Task<IReadOnlyList<AppSetting>> ListSettings(CancellationToken cancellationToken = default) =>
        Act(() => settings.ListAsync(cancellationToken));

    [McpServerTool(Name = "get_setting", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Says what one of CodeSwitchX's settings is set to now (\"is open mic on?\", \"what voice are you using?\", \"what model "
        + "do new chats use?\"). Answer with the value in a few words.")]
    public Task<AppSettingValue> GetSetting(
        [Description("The setting's name from list_settings, as the user said it: \"open mic\", \"voice\", \"cooldown\".")] string name,
        CancellationToken cancellationToken = default) => Act(() => settings.GetAsync(name, cancellationToken));

    [McpServerTool(Name = "set_setting", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Changes one of CodeSwitchX's settings as the Settings page would (\"turn open mic off\", \"use the Kokoro voice\", \"set the "
        + "cooldown to 20 seconds\"). Only when the user asked for the change. Returns the new value: say what you set, in a few words, and "
        + "its note when it has one (a download, the voice restarting). A setting that is not changed by voice comes back as an error "
        + "saying why, with Settings opened at its page: tell the user that.")]
    public Task<AppSettingValue> SetSetting(
        [Description("The setting's name from list_settings, as the user said it.")] string name,
        [Description("The new value, one of the setting's values when it lists them: \"on\", \"off\", \"Kokoro\", \"20\".")] string value,
        CancellationToken cancellationToken = default) => Act(() => settings.SetAsync(name, value, cancellationToken));

    [McpServerTool(Name = "open_settings", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Opens CodeSwitchX's Settings (\"open settings\", \"show me the listening settings\"), at a page when the user named one. "
        + "Returns the page shown.")]
    public Task<string> OpenSettings(
        [Description("The page as the user said it: Voice, Listening, Brain & chats, Shortcuts, Claude Code, Usage, Privacy & data. Left "
            + "out: Settings as it was last shown.")] string? page = null,
        CancellationToken cancellationToken = default) => Act(async () => $"Settings is open at {await settings.OpenAsync(page, cancellationToken).ConfigureAwait(false)}.");
}
