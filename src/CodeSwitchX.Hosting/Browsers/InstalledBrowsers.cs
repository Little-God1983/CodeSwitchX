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
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(ClientsKey);
                foreach (var name in root?.GetSubKeyNames() ?? [])
                {
                    using var command = root!.OpenSubKey($@"{name}\shell\open\command");
                    found.Add((name, ExecutableOf(command?.GetValue(null) as string)));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A hive that cannot be read lists nothing.
            }
        }

        return found;
    }

    public string? FirefoxProfilesIni() => ReadText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "profiles.ini"));

    public string? EdgeLocalState() => LocalState("Microsoft", "Edge");

    public string? ChromeLocalState() => LocalState("Google", "Chrome");

    private static string? LocalState(string vendor, string product) =>
        ReadText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), vendor, product, "User Data", "Local State"));

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

    /// <summary>The program of a command line: the quoted part, or up to the first space.</summary>
    private static string? ExecutableOf(string? command)
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

        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }
}
