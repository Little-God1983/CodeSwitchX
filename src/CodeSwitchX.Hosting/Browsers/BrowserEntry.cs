namespace CodeSwitchX.Hosting.Browsers;

/// <summary>
/// One way to open an address (#335): the Windows default, or an installed browser with one of its profiles.
/// <paramref name="Id"/> carries the profile (<c>firefox|name</c>, <c>edge|Default</c>), so a saved choice survives another profile being added.
/// <paramref name="IsFirefox"/> is set by the catalog for every Firefox build, which opens an address as a tab.
/// </summary>
public sealed record BrowserEntry(string Id, string Label, string? Executable, IReadOnlyList<string> ProfileArguments, bool IsFirefox = false)
{
    public const string DefaultId = "default";

    /// <summary>The same entry as far as opening and showing it goes.</summary>
    public bool SameAs(BrowserEntry? other) =>
        other is not null && Id == other.Id && Label == other.Label && Executable == other.Executable && IsFirefox == other.IsFirefox
        && ProfileArguments.SequenceEqual(other.ProfileArguments);

    /// <summary>What a combo box item reads as, to the eye and to automation: the label, not the record's fields.</summary>
    public override string ToString() => Label;

    /// <summary>The command line that opens <paramref name="url"/>; each value is one entry, so a profile name with a space stays whole.</summary>
    public IReadOnlyList<string> Arguments(string url)
    {
        // Firefox started with a profile opens a window in it, and needs the address marked as a tab to open.
        return [.. ProfileArguments, .. IsFirefox ? new[] { "-new-tab", url } : [url]];
    }
}
