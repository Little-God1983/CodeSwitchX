namespace CodeSwitchX.Ingest.Api;

public sealed class EventApiOptions
{
    public bool EnableNamedPipe { get; set; } = true;
    public string PipeName { get; set; } = "CodeSwitchX-" + Sanitize(Environment.UserName);
    public bool EnableLoopback { get; set; } = true;

    /// <summary>0 lets Kestrel pick a free port; the chosen port is published in endpoint.json.</summary>
    public int LoopbackPort { get; set; }
    public long MaxBodyBytes { get; set; } = 1024 * 1024;

    private static string Sanitize(string value) =>
        new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
}
