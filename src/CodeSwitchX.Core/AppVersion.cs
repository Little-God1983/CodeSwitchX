using System.Reflection;

namespace CodeSwitchX.Core;

/// <summary>The version the build stamps on every assembly: <c>&lt;Version&gt;</c> in Directory.Build.props, which the stable build bumps.</summary>
public static class AppVersion
{
    private static readonly Assembly Assembly = typeof(AppVersion).Assembly;

    /// <summary>
    /// The version as <c>&lt;Version&gt;</c> was written ("0.1.0.1", "0.2.0"), or as a build override set it ("0.1.0.1-oneoff.abc1234"),
    /// without the "+<commit>" the SDK may append. Read from this assembly: every project takes the same version from the props
    /// file, and a test host has no CodeSwitchX entry assembly. "unknown" when the build stamped none: a made-up number
    /// (AssemblyName.Version defaults to 1.0.0.0) would look real.
    /// </summary>
    public static string Current { get; } = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } informational
        && informational.Split('+', 2)[0] is { Length: > 0 } version
            ? version
            : "unknown";

    /// <summary>The build configuration the SDK stamps ("Debug", "Release"), or null when it stamped none.</summary>
    public static string? Configuration { get; } = Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;

    /// <summary>
    /// What the title bar shows. The stable build publishes version N and then bumps the props file to N, so a Debug build from
    /// main carries the same number as the stable build: anything but a Release build says so.
    /// </summary>
    public static string Display { get; } = Configuration is null or "Release" ? Current : $"{Current} ({Configuration})";
}
