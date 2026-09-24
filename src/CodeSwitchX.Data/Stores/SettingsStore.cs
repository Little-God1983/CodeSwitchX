using System.Text.Json;
using CodeSwitchX.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeSwitchX.Data.Stores;

public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<CodeSwitchXDbContext> _factory;

    public SettingsStore(IDbContextFactory<CodeSwitchXDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        return setting is null ? default : JsonSerializer.Deserialize<T>(setting.ValueJson, JsonOptions);
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.Settings.Add(new Setting { Key = key, ValueJson = json });
        }
        else
        {
            setting.ValueJson = json;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PricingRule>> GetPricingAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.PricingRules.AsNoTracking().OrderBy(p => p.Model).ToListAsync(ct);
    }

    public async Task UpsertPricingAsync(IReadOnlyCollection<PricingRule> rules, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var models = rules.Select(r => r.Model).ToArray();
        var existing = await db.PricingRules.Where(p => models.Contains(p.Model)).ToDictionaryAsync(p => p.Model, ct);
        foreach (var rule in rules)
        {
            if (existing.TryGetValue(rule.Model, out var row))
            {
                db.Entry(row).CurrentValues.SetValues(rule);
            }
            else
            {
                db.PricingRules.Add(rule);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task EnsurePricingDefaultsAsync(IReadOnlyCollection<PricingRule> defaults, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var present = await db.PricingRules.Select(p => p.Model).ToHashSetAsync(ct);
        foreach (var rule in defaults.Where(r => !present.Contains(r.Model)))
        {
            db.PricingRules.Add(rule);
        }

        await db.SaveChangesAsync(ct);
    }
}
