using Microsoft.Win32;

namespace CodeSwitchX.Hosting.Browsers;

/// <summary>The real <see cref="IBrowserSource"/>: StartMenuInternet in the registry and the browsers' profile files. Never reads cookies.</summary>
public sealed class InstalledBrowsers : IBrowserSource
{
    private const string ClientsKey = @"SOFTWARE\Clients\StartMenuInternet";

    public IReadOnlyList<(string Key, string? Executable)> Installed()
    {
        var found = new List<(string, string?)>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var root = baseKey.OpenSubKey(ClientsKey);
                if (root is not null)
                {
                    found.AddRange(Collect(root.GetSubKeyNames(), name =>
                    {
                        using var command = root.OpenSubKey($@"{name}\shell\open\command");
                        return command?.GetValue(null) as string;
                    }));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A hive that cannot be read lists nothing.
            }
        }

        return found;
    }

    /// <summary>Each name with the program of its open command; a name that cannot be read is left out, not the others.</summary>
    internal static List<(string Key, string? Executable)> Collect(IEnumerable<string> names, Func<string, string?> commandOf)
    {
        var found = new List<(string, string?)>();
        foreach (var name in names)
        {
            try
            {
                found.Add((name, ExecutableOf(commandOf(name))));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // One key that cannot be read is not a reason to hide the others.
            }
        }

        return found;
    }

    public string? FirefoxProfilesIni() => ReadText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "profiles.ini"));

    public string? ChromiumLocalState(string folder) =>
        ReadText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder, "User Data", "Local State"));

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The program of a command line: the quoted part, or an unquoted path through its <c>.exe</c> (it may hold spaces), else up to the first space.</summary>
    internal static string? ExecutableOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        // An unquoted path may hold spaces: the program ends at the first ".exe" that is followed by a space or the end.
        for (var at = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); at >= 0; at = command.IndexOf(".exe", at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + 4;
            if (end == command.Length || command[end] == ' ')
            {
                return command[..end];
            }
        }

        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }
}
