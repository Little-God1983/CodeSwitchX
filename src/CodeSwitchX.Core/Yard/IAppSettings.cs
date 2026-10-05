namespace CodeSwitchX.Core.Yard;

/// <summary>
/// The app's own settings as Raven's brain reads and changes them by voice (#126): one list, each setting with its page,
/// plain name, allowed values and a description. Changes go through the same code the Settings page uses, so the page
/// and the panel show them at once. Throws <see cref="YardActionException"/> with words for the user for an unknown
/// name, a value not allowed, or a setting that is not changed by voice.
/// </summary>
public interface IAppSettings
{
    /// <summary>Every setting the brain may ask about, in the order of the Settings pages.</summary>
    IReadOnlyList<AppSetting> Settings { get; }

    /// <summary>The setting's value now. Any thread.</summary>
    Task<AppSettingValue> GetAsync(string name, CancellationToken ct);

    /// <summary>Changes the setting as the Settings page would, and returns its new value. Any thread.</summary>
    Task<AppSettingValue> SetAsync(string name, string value, CancellationToken ct);

    /// <summary>Opens Settings at the page (by its name, as the sidebar lists it; null for the first); returns the page's name.</summary>
    Task<string> OpenAsync(string? page, CancellationToken ct);

    /// <summary>The pages, as the sidebar lists them.</summary>
    IReadOnlyList<string> Pages { get; }
}

/// <param name="Name">What the user calls it: "open mic", "cooldown".</param>
/// <param name="Page">The Settings page it is on.</param>
/// <param name="Description">What it does, for the brain.</param>
/// <param name="Values">The values it may take; null when free (a model id, a number in a range).</param>
/// <param name="ByVoice">False for a setting that is only read, never changed, by voice; <see cref="NotByVoice"/> says why.</param>
public sealed record AppSetting(string Name, string Page, string Description, IReadOnlyList<string>? Values, bool ByVoice = true, string? NotByVoice = null);

/// <param name="Value">The value now, as the user would say it.</param>
/// <param name="Note">What else happens: a download, a restart of the voice. Null when nothing.</param>
public sealed record AppSettingValue(string Name, string Page, string Value, IReadOnlyList<string>? Values, string? Note = null);
