using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Telemetry;

public readonly record struct UsageTotals(TokenUsage Tokens, decimal Cost)
{
    /// <summary>True when the tokens include usage of a model without a pricing rule, which added nothing to the cost.</summary>
    public bool Unpriced { get; init; }

    public static UsageTotals Zero => default;
}
