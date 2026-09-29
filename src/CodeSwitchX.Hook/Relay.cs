using System.Globalization;
using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeSwitchX.Hook;

/// <summary>
/// Forwards one Claude Code hook payload (stdin) to the running CodeSwitchX instance.
/// Never writes to stdout, never throws, always exits 0, so Claude Code is never disturbed.
/// Only the small fields the engine needs travel: long strings are cut and large nested values (tool inputs and
/// responses) are dropped, so a PostToolUse for a big file read still fits the API's body limit.
/// </summary>
internal static partial class Relay
{
    internal const int ConnectTimeoutMs = 150;
    internal const int TotalTimeoutMs = 1000;
    internal const int MaxStdinBytes = 16 * 1024 * 1024;

    /// <summary>Strings in the forwarded payload are cut here: the engine only needs ids, names and the start of a prompt.</summary>
    internal const int MaxStringChars = 2000;

    /// <summary>Nested objects and arrays larger than this (raw JSON) are omitted from the forwarded payload.</summary>
    internal const int MaxNestedBytes = 8 * 1024;
    internal const int MaxParentDepth = 8;

    internal static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeSwitchX");

    internal static async Task<int> RunAsync(string[] args, Stream stdin, string dataDirectory)
    {
        try
        {
            var eventName = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : "Unknown";
            var endpoint = ReadEndpoint(Path.Combine(dataDirectory, "endpoint.json"));
            if (endpoint is null || !OwnerIsAlive(endpoint.Pid, endpoint.StartedAtUtc))
            {
                // A stale endpoint.json (crash, kill, missed shutdown) must not cost every hook a connect timeout.
                return 0;
            }

            var token = ReadToken(Path.Combine(dataDirectory, "token"));
            if (token is null)
            {
                return 0;
            }

            var payload = await ReadPayloadAsync(stdin).ConfigureAwait(false);
            var envelope = BuildEnvelope(eventName, payload, DateTimeOffset.UtcNow, Environment.ProcessId, ProcessChain.Ancestors(MaxParentDepth));

            using var cts = new CancellationTokenSource(TotalTimeoutMs);
            await PostAsync(endpoint, token, envelope, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Swallow everything: the relay must never affect the hook's exit code.
        }

        return 0;
    }

    internal static EndpointInfo? ReadEndpoint(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var info = JsonSerializer.Deserialize(File.ReadAllText(file), RelayJsonContext.Default.EndpointInfo);
            return info is null || (info.Port <= 0 && string.IsNullOrEmpty(info.PipeName)) ? null : info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the process that wrote the descriptor still runs. A PID is reused after a crash, so the process holding it
    /// counts only if it started no later than the descriptor was written; a descriptor without a time is trusted by the PID.
    /// </summary>
    internal static bool OwnerIsAlive(int pid, string? startedAtUtc)
    {
        if (pid <= 0)
        {
            return true; // unknown owner (hand-written endpoint file): trust it
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            return !DateTimeOffset.TryParse(startedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var written)
                || process.StartTime.ToUniversalTime() <= written.UtcDateTime;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true; // exists but cannot be queried
        }
    }

    internal static string? ReadToken(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var token = File.ReadAllText(file).Trim();
            return token.Length == 0 ? null : token;
        }
        catch
        {
            return null;
        }
    }

    internal static async Task<string> ReadPayloadAsync(Stream stdin)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while (buffer.Length < MaxStdinBytes && (read = await stdin.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    internal static string BuildEnvelope(string eventName, string payload, DateTimeOffset now, int relayPid, IReadOnlyList<ProcessInfo> chain)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("event", eventName);
            writer.WriteString("receivedAtUtc", now);
            writer.WriteNumber("relayPid", relayPid);
            writer.WriteStartArray("parentChain");
            foreach (var process in chain)
            {
                writer.WriteStartObject();
                writer.WriteNumber("pid", process.Pid);
                writer.WriteString("name", process.Name);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("payload");
            if (TryParseJson(payload, out var document))
            {
                using (document)
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        WriteTrimmedObject(writer, document.RootElement);
                    }
                    else if (LoneSurrogateEscape().IsMatch(payload))
                    {
                        writer.WriteStringValue(Truncate(payload));
                    }
                    else
                    {
                        document.RootElement.WriteTo(writer);
                    }
                }
            }
            else
            {
                writer.WriteStringValue(Truncate(payload));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static void WriteTrimmedObject(Utf8JsonWriter writer, JsonElement obj)
    {
        writer.WriteStartObject();
        foreach (var property in obj.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    writer.WriteString(property.Name, Truncate(StringOf(property.Value)));
                    break;
                case JsonValueKind.Object or JsonValueKind.Array:
                    // tool_input / tool_response bodies: nothing the status engine needs, and the bulk of the size. One that holds
                    // a lone surrogate escape cannot be written at all.
                    var raw = property.Value.GetRawText();
                    if (raw.Length <= MaxNestedBytes && !LoneSurrogateEscape().IsMatch(raw))
                    {
                        property.WriteTo(writer);
                    }

                    break;
                default:
                    property.WriteTo(writer);
                    break;
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// The element's string. A lone surrogate escape (half an emoji, cut by whoever wrote the payload) cannot be read, and the
    /// writer refuses it; it becomes U+FFFD, so the ids and names around it still reach the engine.
    /// </summary>
    private static string StringOf(JsonElement element)
    {
        try
        {
            return element.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            using var repaired = JsonDocument.Parse(LoneSurrogateEscape().Replace(element.GetRawText(), @"\uFFFD"));
            return repaired.RootElement.GetString() ?? string.Empty;
        }
    }

    /// <summary>A \uD800-\uDBFF escape not followed by a \uDC00-\uDFFF one, or such a low one not preceded by a high one.</summary>
    [GeneratedRegex(@"\\u[dD][89abAB][0-9a-fA-F]{2}(?!\\u[dD][c-fC-F][0-9a-fA-F]{2})|(?<!\\u[dD][89abAB][0-9a-fA-F]{2})\\u[dD][c-fC-F][0-9a-fA-F]{2}")]
    private static partial Regex LoneSurrogateEscape();

    private static string Truncate(string value)
    {
        if (value.Length <= MaxStringChars)
        {
            return value;
        }

        var length = MaxStringChars;
        if (char.IsHighSurrogate(value[length - 1]))
        {
            length--; // never split a surrogate pair; the writer rejects lone surrogates
        }

        return value[..length];
    }

    private static bool TryParseJson(string text, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static async Task PostAsync(EndpointInfo endpoint, string token, string envelope, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(endpoint.PipeName))
        {
            try
            {
                await PostViaPipeAsync(endpoint.PipeName, token, envelope, ct).ConfigureAwait(false);
                return;
            }
            catch when (endpoint.Port > 0)
            {
                // fall through to loopback
            }
        }

        if (endpoint.Port > 0)
        {
            await PostViaLoopbackAsync(endpoint.Port, token, envelope, ct).ConfigureAwait(false);
        }
    }

    private static async Task PostViaPipeAsync(string pipeName, string token, string envelope, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancel) =>
            {
                // CurrentUserOnly: refuse a server owned by another account, so the token and prompt text never reach a squatted pipe.
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(ConnectTimeoutMs, cancel).ConfigureAwait(false);
                return pipe;
            },
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://pipe/"), Timeout = TimeSpan.FromMilliseconds(TotalTimeoutMs) };
        await SendAsync(client, token, envelope, ct).ConfigureAwait(false);
    }

    private static async Task PostViaLoopbackAsync(int port, string token, string envelope, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromMilliseconds(ConnectTimeoutMs) };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMilliseconds(TotalTimeoutMs) };
        await SendAsync(client, token, envelope, ct).ConfigureAwait(false);
    }

    private static async Task SendAsync(HttpClient client, string token, string envelope, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "events")
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
    }
}
