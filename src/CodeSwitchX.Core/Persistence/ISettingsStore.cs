namespace CodeSwitchX.Core.Persistence;

public interface ISettingsStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
    Task<IReadOnlyList<PricingRule>> GetPricingAsync(CancellationToken ct = default);
    Task UpsertPricingAsync(IReadOnlyCollection<PricingRule> rules, CancellationToken ct = default);

    /// <summary>Inserts the given rules whose <see cref="PricingRule.Model"/> is not present yet; existing rows are never touched.</summary>
    Task EnsurePricingDefaultsAsync(IReadOnlyCollection<PricingRule> defaults, CancellationToken ct = default);
}
