using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSwitchX.Ingest.Api;

/// <summary>Written to <c>endpoint.json</c> so the relay knows where to post. Property names are part of the relay contract.</summary>
public sealed record EndpointDescriptor(
    [property: JsonPropertyName("pipeName")] string PipeName,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("startedAtUtc")] DateTimeOffset StartedAtUtc)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static EndpointDescriptor? TryRead(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            return JsonSerializer.Deserialize<EndpointDescriptor>(File.ReadAllText(file), Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string file)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, file, overwrite: true);
    }
}
