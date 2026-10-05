namespace CodeSwitchX.Core.Yard;

/// <summary>
/// The app's own settings as Raven's brain reads and changes them by voice (#126): one list, each setting with its page,
/// plain name, allowed values and a description. Changes go through the same code the Settings page uses, so the page
/// and the panel show them at once. Throws <see cref="YardActionException"/> with words for the user for an unknown
/// name, a value not allowed, or a setting that is not changed by voice.
/// </summary>
public interface IAppSettings
{
    /// <summary>Every setting the brain may ask about, in the order of the Settings pages, with the values each takes now. Any thread.</summary>
    Task<IReadOnlyList<AppSetting>> ListAsync(CancellationToken ct);

    /// <summary>The setting's value now. Any thread.</summary>
    Task<AppSettingValue> GetAsync(string name, CancellationToken ct);

    /// <summary>Changes the setting as the Settings page would, and returns its new value. Any thread.</summary>
    Task<AppSettingValue> SetAsync(string name, string value, CancellationToken ct);

    /// <summary>Opens Settings at the page (by its name, as the sidebar lists it; null for the first); returns the page's name.</summary>
    Task<string> OpenAsync(string? page, CancellationToken ct);
}

/// <param name="Name">What the user calls it: "open mic", "cooldown".</param>
/// <param name="Page">The Settings page it is on.</param>
/// <param name="Description">What it does, for the brain.</param>
/// <param name="Values">The values it may take; null when free (a model id, a number in a range).</param>
/// <param name="NotByVoice">Why the setting is only read, never changed, by voice; null for one that is changed by voice.</param>
public sealed record AppSetting(string Name, string Page, string Description, IReadOnlyList<string>? Values, string? NotByVoice = null)
{
    /// <summary>Whether it is changed by voice: the brain reads it in list_settings.</summary>
    public bool ByVoice => NotByVoice is null;
}

/// <param name="Value">The value now, as the user would say it.</param>
/// <param name="Note">What else happens: a download, a restart of the voice. Null when nothing.</param>
public sealed record AppSettingValue(string Name, string Page, string Value, IReadOnlyList<string>? Values, string? Note = null);
