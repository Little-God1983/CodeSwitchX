using System.Reflection;

namespace CodeSwitchX.Core;

/// <summary>The version the build stamps on every assembly: <c>&lt;Version&gt;</c> in Directory.Build.props, which the stable build bumps.</summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        // InformationalVersion carries <Version> as written ("0.1.0.1", "0.2.0"); the SDK may append "+<commit>", which
        // is no part of the number. Read from this assembly: every project takes the same version from the props file,
        // and a test host has no CodeSwitchX entry assembly.
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational?.Split('+', 2)[0];
        return string.IsNullOrWhiteSpace(version) ? assembly.GetName().Version?.ToString() ?? "?" : version;
    }
}
