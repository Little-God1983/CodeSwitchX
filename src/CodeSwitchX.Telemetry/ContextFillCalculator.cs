using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Telemetry;

public enum ContextPressure
{
    Normal,
    Amber,
    Red,
}

public static class ContextFillCalculator
{
    public const double AmberThreshold = 0.8;
    public const double RedThreshold = 0.9;

    public static double Fill(TokenUsage latest, PricingRule rule)
    {
        if (rule.ContextWindow <= 0)
        {
            return 0.0;
        }

        return Math.Clamp((double)latest.ContextTokens / rule.ContextWindow, 0.0, 1.0);
    }

    public static ContextPressure Level(double fill) => fill switch
    {
        >= RedThreshold => ContextPressure.Red,
        >= AmberThreshold => ContextPressure.Amber,
        _ => ContextPressure.Normal,
    };
}
