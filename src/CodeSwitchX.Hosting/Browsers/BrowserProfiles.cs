using System.Text.Json;

namespace CodeSwitchX.Hosting.Browsers;

/// <summary>The profiles a browser keeps, read from its own files' text. Cookies and history are never read.</summary>
public static class BrowserProfiles
{
    /// <summary>The <c>Name=</c> of each <c>[Profile*]</c> section of profiles.ini; <c>[Install*]</c>'s <c>Default=</c> names a folder, not a profile.</summary>
    public static IReadOnlyList<string> Firefox(string profilesIni)
    {
        var names = new List<string>();
        var inProfile = false;
        foreach (var raw in profilesIni.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inProfile = line.StartsWith("[Profile", StringComparison.OrdinalIgnoreCase);
            }
            else if (inProfile && line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase) && line.Length > 5)
            {
                names.Add(line[5..]);
            }
        }

        return names;
    }

    /// <summary>The folder and shown name of each profile in a Chromium browser's Local State (<c>profile.info_cache</c>); empty when it is missing or broken.</summary>
    public static IReadOnlyList<(string Directory, string Name)> Chromium(string localStateJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(localStateJson);
            if (!doc.RootElement.TryGetProperty("profile", out var profile)
                || !profile.TryGetProperty("info_cache", out var cache) || cache.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return [.. cache.EnumerateObject().Select(p =>
                (p.Name, p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name ? name : p.Name))];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
