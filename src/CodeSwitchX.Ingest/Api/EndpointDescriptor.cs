using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSwitchX.Ingest.Api;

/// <summary>
/// Written to <c>endpoint.json</c> so the relay knows where to post. Property names are part of the relay contract.
/// <paramref name="OwnerStartedAtUtc"/> is the writing process's start time as the kernel reports it: the relay tells the
/// process from one that reused its PID by it, without a clock or the write time entering the comparison.
/// </summary>
public sealed record EndpointDescriptor(
    [property: JsonPropertyName("pipeName")] string PipeName,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("startedAtUtc")] DateTimeOffset StartedAtUtc,
    [property: JsonPropertyName("ownerStartedAtUtc")] DateTimeOffset OwnerStartedAtUtc = default)
{
    public static DateTimeOffset CurrentProcessStartedAtUtc()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }
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

    /// <summary>Replaces the file in one step; a relay that still reads the old file is waited out (<see cref="AtomicFile"/>).</summary>
    public void Write(string file) => AtomicFile.Replace(file, JsonSerializer.Serialize(this, Options), ".tmp");
}
