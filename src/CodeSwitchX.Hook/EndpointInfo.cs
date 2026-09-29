using System.Text.Json.Serialization;

namespace CodeSwitchX.Hook;

internal sealed class EndpointInfo
{
    [JsonPropertyName("pipeName")]
    public string? PipeName { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("startedAtUtc")]
    public string? StartedAtUtc { get; set; }

    /// <summary>The owner's process start time as the kernel reports it; the relay compares it with the process that holds the PID now.</summary>
    [JsonPropertyName("ownerStartedAtUtc")]
    public string? OwnerStartedAtUtc { get; set; }
}
