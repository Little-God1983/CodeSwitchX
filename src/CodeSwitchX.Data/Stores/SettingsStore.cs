using System.Text.Json;
using CodeSwitchX.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodeSwitchX.Data.Stores;

public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<CodeSwitchXDbContext> _factory;
    private string? _upsert;

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
        // One statement: a read followed by an insert let two first saves of a key both find no row and both insert, and
        // the second failed on the key.
        await db.Database.ExecuteSqlRawAsync(_upsert ??= Upsert(db.Model), [key, json], ct);
    }

    /// <summary>The upsert with the table and column names the model maps, so a rename in a migration reaches it.</summary>
    private static string Upsert(IModel model)
    {
        var entity = model.FindEntityType(typeof(Setting)) ?? throw new InvalidOperationException("Setting is not mapped");
        var table = entity.GetTableName() ?? throw new InvalidOperationException("Setting is not mapped to a table");
        var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
        var key = entity.GetProperty(nameof(Setting.Key)).GetColumnName(store);
        var json = entity.GetProperty(nameof(Setting.ValueJson)).GetColumnName(store);
        return $$"""INSERT INTO "{{table}}" ("{{key}}", "{{json}}") VALUES ({0}, {1}) ON CONFLICT("{{key}}") DO UPDATE SET "{{json}}" = "excluded"."{{json}}";""";
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
}
