using System.Text.Json;

namespace CodeSwitchX.Ingest;

internal static class JsonStrings
{
    /// <summary>
    /// The element's string, or null for anything but a string and for text .NET cannot hold: JSON.stringify writes half of
    /// a surrogate pair (a string cut inside an emoji) as an escape, which is valid JSON, and <see cref="JsonElement.GetString"/>
    /// throws on it. One rule for the transcripts and the hook payloads.
    /// </summary>
    public static string? TryRead(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
