namespace CodeSwitchX.Ingest.Api;

public sealed class EventApiOptions
{
    public bool EnableNamedPipe { get; set; } = true;
    /// <summary>Per-user pipe. The SID cannot collide across users or be guessed from a display name the way an ASCII-filtered user name can.</summary>
    public string PipeName { get; set; } = DefaultPipeName();

    public static string DefaultPipeName()
    {
        try
        {
            if (System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value is { Length: > 0 } sid)
            {
                return "CodeSwitchX-" + sid;
            }
        }
        catch (System.Security.SecurityException)
        {
        }

        return "CodeSwitchX-" + Sanitize(Environment.UserName);
    }
    public bool EnableLoopback { get; set; } = true;

    /// <summary>0 lets Kestrel pick a free port; the chosen port is published in endpoint.json.</summary>
    public int LoopbackPort { get; set; }
    public long MaxBodyBytes { get; set; } = 1024 * 1024;

    private static string Sanitize(string value) =>
        new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
}
