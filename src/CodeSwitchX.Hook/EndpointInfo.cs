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
}
