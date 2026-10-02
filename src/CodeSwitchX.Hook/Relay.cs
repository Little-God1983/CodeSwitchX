using System.Globalization;
using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeSwitchX.Hook;

/// <summary>
/// Forwards one Claude Code hook payload (stdin) to the running CodeSwitchX instance.
/// Never throws and always exits 0, so Claude Code is never disturbed. It writes to stdout only when CodeSwitchX answers
/// a tool event with a stop for the chat's turn (the user asked Raven to stop it): then it tells Claude Code to end the
/// turn, and on PreToolUse not to take the step it was about to.
/// Only the small fields the engine needs travel: long strings are cut and large nested values (tool inputs and
/// responses) are dropped, so a PostToolUse for a big file read still fits the API's body limit.
/// </summary>
internal static class Relay
{
    internal const int ConnectTimeoutMs = 150;

    /// <summary>The kernel gives both sides the same process start time; this covers the rounding of a serialised one.</summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);
    internal const int TotalTimeoutMs = 1000;
    internal const int MaxStdinBytes = 16 * 1024 * 1024;

    /// <summary>Strings in the forwarded payload are cut here: the engine only needs ids, names and the start of a prompt.</summary>
    internal const int MaxStringChars = 2000;

    /// <summary>Nested objects and arrays larger than this (raw JSON) are omitted from the forwarded payload.</summary>
    internal const int MaxNestedBytes = 8 * 1024;
    internal const int MaxParentDepth = 8;

    internal static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeSwitchX");

    /// <summary>The events whose answer may carry a stop: the turn's tool steps.</summary>
    private static bool MayStop(string eventName) => eventName is "PreToolUse" or "PostToolUse";

    /// <summary>
    /// Says this relay hands a stop on, so CodeSwitchX can tell the user when the hooks Claude Code runs are an older
    /// relay's, which drops it. The Event API reads the same name.
    /// </summary>
    internal const string StopsHeader = "X-CodeSwitchX-Relay-Stops";

    /// <summary>An answer far bigger than a stop is no stop.</summary>
    private const int MaxAnswerBytes = 4 * 1024;

    internal static async Task<int> RunAsync(string[] args, Stream stdin, TextWriter stdout, string dataDirectory)
    {
        try
        {
            var eventName = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : "Unknown";
            var endpoint = ReadEndpoint(Path.Combine(dataDirectory, "endpoint.json"));
            if (endpoint is null || !OwnerIsAlive(endpoint))
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
            var answer = await PostAsync(endpoint, token, envelope, cts.Token).ConfigureAwait(false);
            if (MayStop(eventName) && StopIn(answer) is { } reason)
            {
                await stdout.WriteAsync(StopAnswer(eventName, reason)).ConfigureAwait(false);
                await stdout.FlushAsync().ConfigureAwait(false);
            }
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
    /// is also told by its start time, which the writer read from the kernel as this does: no clock and no write time enter
    /// it. A descriptor without a start time is trusted by the PID.
    /// </summary>
    internal static bool OwnerIsAlive(EndpointInfo endpoint)
    {
        if (endpoint.Pid <= 0)
        {
            return true; // unknown owner (hand-written endpoint file): trust it
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(endpoint.Pid);
            if (process.HasExited)
            {
                return false;
            }

            return !DateTimeOffset.TryParse(endpoint.OwnerStartedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started)
                || (process.StartTime.ToUniversalTime() - started.UtcDateTime).Duration() <= StartTimeTolerance;
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
                    WriteValue(writer, document.RootElement, top: true);
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

    /// <summary>
    /// Writes the value with every string cut and read through <see cref="StringOf"/>, so half an emoji anywhere in it, in
    /// a name or a value, cannot lose the event. At the top level a nested object or array above the size cap is left out.
    /// </summary>
    private static void WriteValue(Utf8JsonWriter writer, JsonElement element, bool top)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    string name;
                    try
                    {
                        name = property.Name;
                    }
                    catch (InvalidOperationException)
                    {
                        continue; // a key with half an emoji: nothing the engine reads
                    }

                    if (top && property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && property.Value.GetRawText().Length > MaxNestedBytes)
                    {
                        continue; // tool_input / tool_response bodies: nothing the status engine needs, and the bulk of the size.
                    }

                    writer.WritePropertyName(name);
                    WriteValue(writer, property.Value, top: false);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteValue(writer, item, top: false);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(Truncate(StringOf(element)));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
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
            return Unescape(element.GetRawText());
        }
    }

    /// <summary>
    /// Decodes a JSON string token (quotes included, escapes valid: the document parsed) by hand, pairing surrogate escapes
    /// as the reader does and reading a lone one as U+FFFD. An escaped backslash followed by "u" is text, not an escape.
    /// </summary>
    private static string Unescape(string raw)
    {
        var text = new StringBuilder(raw.Length);
        var end = raw.Length - 1;
        for (var i = 1; i < end; i++)
        {
            var c = raw[i];
            if (c != '\\')
            {
                text.Append(c);
                continue;
            }

            var escape = raw[++i];
            switch (escape)
            {
                case 'u':
                    var code = (char)int.Parse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    i += 4;
                    if (char.IsHighSurrogate(code) && i + 6 < end + 1 && raw[i + 1] == '\\' && raw[i + 2] == 'u'
                        && char.IsLowSurrogate((char)int.Parse(raw.AsSpan(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)))
                    {
                        text.Append(code).Append((char)int.Parse(raw.AsSpan(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 6;
                    }
                    else
                    {
                        text.Append(char.IsSurrogate(code) ? '\uFFFD' : code);
                    }

                    break;
                case 'n':
                    text.Append('\n');
                    break;
                case 't':
                    text.Append('\t');
                    break;
                case 'r':
                    text.Append('\r');
                    break;
                case 'b':
                    text.Append('\b');
                    break;
                case 'f':
                    text.Append('\f');
                    break;
                default:
                    text.Append(escape); // \" \\ \/
                    break;
            }
        }

        return text.ToString();
    }

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

    /// <summary>The stop reason in CodeSwitchX's answer (<c>{"stop": "…"}</c>); null for none or anything else.</summary>
    internal static string? StopIn(string? answer)
    {
        if (string.IsNullOrEmpty(answer) || !TryParseJson(answer, out var document))
        {
            return null;
        }

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("stop", out var stop)
                && stop.ValueKind == JsonValueKind.String
                && stop.GetString() is { Length: > 0 } reason
                    ? Truncate(reason)
                    : null;
        }
    }

    /// <summary>
    /// What tells Claude Code to end the turn: <c>continue: false</c> with the reason, which the chat reads; on PreToolUse
    /// also a deny, so the step it was about to take is not taken but shown as stopped.
    /// </summary>
    internal static string StopAnswer(string eventName, string reason)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("continue", false);
            writer.WriteString("stopReason", reason);
            if (eventName == "PreToolUse")
            {
                writer.WriteStartObject("hookSpecificOutput");
                writer.WriteString("hookEventName", "PreToolUse");
                writer.WriteString("permissionDecision", "deny");
                writer.WriteString("permissionDecisionReason", reason);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <returns>The body of a 200 answer; null for any other.</returns>
    private static async Task<string?> PostAsync(EndpointInfo endpoint, string token, string envelope, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(endpoint.PipeName))
        {
            try
            {
                return await PostViaPipeAsync(endpoint.PipeName, token, envelope, ct).ConfigureAwait(false);
            }
            catch when (endpoint.Port > 0)
            {
                // fall through to loopback
            }
        }

        return endpoint.Port > 0 ? await PostViaLoopbackAsync(endpoint.Port, token, envelope, ct).ConfigureAwait(false) : null;
    }

    private static async Task<string?> PostViaPipeAsync(string pipeName, string token, string envelope, CancellationToken ct)
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
        return await SendAsync(client, token, envelope, ct).ConfigureAwait(false);
    }

    private static async Task<string?> PostViaLoopbackAsync(int port, string token, string envelope, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromMilliseconds(ConnectTimeoutMs) };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMilliseconds(TotalTimeoutMs) };
        return await SendAsync(client, token, envelope, ct).ConfigureAwait(false);
    }

    private static async Task<string?> SendAsync(HttpClient client, string token, string envelope, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "events")
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(StopsHeader, "1");
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentLength is > MaxAnswerBytes)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return body.Length > MaxAnswerBytes ? null : body;
    }
}
