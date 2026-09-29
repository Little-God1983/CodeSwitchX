using System.Text.RegularExpressions;
using CodeSwitchX.Core.Persistence;

namespace CodeSwitchX.Telemetry;

public sealed partial class PricingTable
{
    /// <summary>The rule of a model nobody priced: no cost, and no context window to measure a fill against.</summary>
    public static readonly PricingRule Fallback = new()
    {
        Model = string.Empty,
        DisplayName = "Unknown model (no pricing)",
        ContextWindow = 0,
    };

    /// <summary>The window of a model run with Claude Code's "[1m]" option.</summary>
    public const long MillionContext = 1_000_000;

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
    public PricingRule Find(string? model) => Resolve(model, out _);

    /// <summary>The rule's context window, or a million when the id carries the "[1m]" option, whether the rule is known or not.</summary>
    public long ContextWindowOf(string? model)
    {
        var rule = Resolve(model, out var million);
        return million ? Math.Max(rule.ContextWindow, MillionContext) : rule.ContextWindow;
    }

    private PricingRule Resolve(string? model, out bool million)
    {
        million = false;
        if (string.IsNullOrWhiteSpace(model))
        {
            return Fallback;
        }

        var id = Normalize(model, out million);
        foreach (var rule in _rules)
        {
            if (Matches(id, rule.Model))
            {
                return rule;
            }
        }

        return Fallback;
    }

    /// <summary>
    /// The model id as Anthropic names it, from the forms the providers write: Bedrock puts the vendor and a region in
    /// front ("us.anthropic.claude-opus-4-5-20251101-v1:0") and a version at the end, Vertex AI a date after "@"
    /// ("claude-opus-4-5@20251101"), and Claude Code a context option in brackets ("claude-sonnet-4-5[1m]").
    /// </summary>
    internal static string Normalize(string model, out bool million)
    {
        var id = model.Trim();
        million = false;
        var option = id.IndexOf('[');
        if (option >= 0)
        {
            million = id.AsSpan(option).Contains("[1m]", StringComparison.OrdinalIgnoreCase);
            id = id[..option];
        }

        var at = id.IndexOf('@');
        if (at >= 0)
        {
            id = id[..at];
        }

        id = BedrockPrefix().Replace(id, string.Empty);
        return BedrockVersion().Replace(id, string.Empty);
    }

    [GeneratedRegex(@"^(?:[a-z0-9-]+\.)*anthropic\.", RegexOptions.IgnoreCase)]
    private static partial Regex BedrockPrefix();

    [GeneratedRegex(@"-v\d+(?::\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BedrockVersion();

    private static bool Matches(string model, string prefix) =>
        model.Length >= prefix.Length
        && model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (model.Length == prefix.Length || !char.IsAsciiLetterOrDigit(model[prefix.Length]));
}
