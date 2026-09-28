using CodeSwitchX.Core.Persistence;

namespace CodeSwitchX.Telemetry;

public sealed class PricingTable
{
    public static readonly PricingRule Fallback = new()
    {
        Model = string.Empty,
        DisplayName = "Unknown model (no pricing)",
        ContextWindow = 200_000,
    };

    private readonly PricingRule[] _rules;

    public PricingTable(IEnumerable<PricingRule> rules)
    {
        _rules = rules
            .GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderByDescending(r => r.Model.Length)
            .ToArray();
    }

    public static PricingTable Default { get; } = new(DefaultPricing.Rules);

    public IReadOnlyList<PricingRule> Rules => _rules;

    /// <summary>
    /// Longest rule whose model id equals the given id or is a prefix of it that ends where a segment ends: before a "-"
    /// (a date, "claude-opus-4-5-20251101"), an "@" (Vertex AI, "claude-opus-4-5@20251101") or a "[" (a context option,
    /// "claude-opus-4-6[1m]"), never inside a number ("claude-opus-55" is not Opus 5).
    /// </summary>
    public PricingRule Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Fallback;
        }

        foreach (var rule in _rules)
        {
            if (Matches(model, rule.Model))
            {
                return rule;
            }
        }

        return Fallback;
    }

    private static bool Matches(string model, string prefix) =>
        model.Length >= prefix.Length
        && model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (model.Length == prefix.Length || !char.IsAsciiLetterOrDigit(model[prefix.Length]));
}
