namespace CodeSwitchX.Core.Sessions;

public sealed class SessionEngineOptions
{
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A Working/Waiting session without hook evidence drops to Idle when its transcript has been quiet this long.</summary>
    public TimeSpan InferredIdleAfter { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int TitleMaxLength { get; set; } = 60;
}
