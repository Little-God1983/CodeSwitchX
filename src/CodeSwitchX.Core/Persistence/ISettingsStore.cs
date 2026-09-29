namespace CodeSwitchX.Core.Persistence;

public interface ISettingsStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
    Task<IReadOnlyList<PricingRule>> GetPricingAsync(CancellationToken ct = default);
    Task UpsertPricingAsync(IReadOnlyCollection<PricingRule> rules, CancellationToken ct = default);
}
