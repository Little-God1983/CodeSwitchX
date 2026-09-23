using System.Text.Json.Serialization;

namespace CodeSwitchX.Hook;

[JsonSerializable(typeof(EndpointInfo))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class RelayJsonContext : JsonSerializerContext;
