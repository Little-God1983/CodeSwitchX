namespace CodeSwitchX.Hosting.Browsers;

/// <summary>
/// One way to open an address (#335): the Windows default, or an installed browser with one of its profiles.
/// <paramref name="Id"/> carries the profile (<c>firefox|name</c>, <c>edge|Default</c>), so a saved choice survives another profile being added.
/// </summary>
public sealed record BrowserEntry(string Id, string Label, string? Executable, IReadOnlyList<string> ProfileArguments)
{
    public const string DefaultId = "default";

    /// <summary>What a combo box item reads as, to the eye and to automation: the label, not the record's fields.</summary>
    public override string ToString() => Label;

    /// <summary>The command line that opens <paramref name="url"/>; each value is one entry, so a profile name with a space stays whole.</summary>
    public IReadOnlyList<string> Arguments(string url)
    {
        // Firefox is told by "-P": it starts a window in that profile, and needs the address marked as a tab to open.
        var firefox = ProfileArguments.Count > 0 && ProfileArguments[0] == "-P";
        return [.. ProfileArguments, .. firefox ? new[] { "-new-tab", url } : [url]];
    }
}
