namespace CodeSwitchX.Core.Sessions;

public sealed class SessionEngineOptions
{
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);
    public int TitleMaxLength { get; set; } = 60;
}
