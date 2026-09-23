using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Telemetry;

public readonly record struct UsageTotals(TokenUsage Tokens, decimal Cost)
{
    public static UsageTotals Zero => default;
}
