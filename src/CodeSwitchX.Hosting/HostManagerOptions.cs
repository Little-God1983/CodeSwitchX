namespace CodeSwitchX.Hosting;

public sealed class HostManagerOptions
{
    public TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}
