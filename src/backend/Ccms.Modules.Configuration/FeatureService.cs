using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Configuration;

internal sealed record FlagRow(string Key, string Category, string ValueType, string Value, string DefaultValue, string[]? AllowedValues,
    string? ParentKey, string? ConnectorCode, string NameAr, string NameEn, string DescriptionAr, string DescriptionEn, int SortOrder, DateTime UpdatedAt,
    Guid? UpdatedBy, string? UpdatedByName, long? MinValue = null, long? MaxValue = null, int? MaxLength = null);

/// <summary>قراءة المفاتيح من القاعدة على الخادم دائمًا. القيمة الفعلية = قيمة المفتاح ∧ المفتاح الأعلى، مع أقفال البيئة إن وُجدت.
/// الذاكرة المؤقتة قصيرة وتُبطل فور أي تغيير في هذه النسخة.</summary>
internal sealed class FeatureStore(IServiceScopeFactory scopes, IMemoryCache cache, IConfiguration configuration)
{
    private const string CacheKey = "config:flags";
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyDictionary<string, FlagRow>> AllAsync(CancellationToken ct) =>
        (await cache.GetOrCreateAsync(CacheKey, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheDuration;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            var rows = await db.QueryAsync("""
                SELECT f.key, f.category, f.value_type, f.value, f.default_value, f.allowed_values, f.parent_key, f.connector_code,
                       f.name_ar, f.name_en, f.description_ar, f.description_en, f.sort_order, f.updated_at, f.updated_by, u.display_name_ar AS updated_by_name,
                       f.min_value, f.max_value, f.max_length
                FROM config.feature_flags f LEFT JOIN iam.users u ON u.id = f.updated_by
                ORDER BY f.sort_order
                """, null, r => new FlagRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetFieldValue<string[]>(5), r.Str("parent_key"), r.Str("connector_code"),
                r.GetString(8), r.GetString(9), r.GetString(10), r.GetString(11), r.GetInt32(12), r.GetFieldValue<DateTime>(13), r.Uuid("updated_by"),
                r.Str("updated_by_name"), r.IsDBNull(16) ? null : r.GetInt64(16), r.IsDBNull(17) ? null : r.GetInt64(17), r.IsDBNull(18) ? null : r.GetInt32(18)), ct);
            return (IReadOnlyDictionary<string, FlagRow>)rows.ToDictionary(x => x.Key);
        }))!;

    public void Invalidate() => cache.Remove(CacheKey);

    /// <summary>قفل من إعدادات الخادم (FeatureManagement:Locks:KEY) لا تستطيع الواجهة تجاوزه (07 §2).</summary>
    public string? LockedValue(string key) => configuration[$"FeatureManagement:Locks:{key}"] is { Length: > 0 } v ? v : null;

    public string RawValue(FlagRow f) => LockedValue(f.Key) ?? f.Value;

    public bool IsEffective(IReadOnlyDictionary<string, FlagRow> all, string key, int depth = 0)
    {
        if (depth > 5 || !all.TryGetValue(key, out var f)) return false;
        var selfOn = f.ValueType == "boolean" ? RawValue(f) == "true" : true;
        return selfOn && (f.ParentKey is null || IsEffective(all, f.ParentKey, depth + 1));
    }

    public string? BlockedBy(IReadOnlyDictionary<string, FlagRow> all, string key)
    {
        var f = all[key];
        while (f.ParentKey is { } parent && all.TryGetValue(parent, out var p))
        {
            if (p.ValueType == "boolean" && RawValue(p) != "true") return p.Key;
            f = p;
        }
        return null;
    }
}

internal sealed class FeatureService(FeatureStore store) : IFeatureService
{
    public async Task<bool> IsEnabledAsync(string key, CancellationToken ct = default)
    {
        var all = await store.AllAsync(ct);
        if (!all.ContainsKey(key)) throw new InvalidOperationException($"مفتاح غير معرّف: {key}");
        return store.IsEffective(all, key);
    }

    public async Task<string> GetValueAsync(string key, CancellationToken ct = default)
    {
        var all = await store.AllAsync(ct);
        return all.TryGetValue(key, out var f) ? store.RawValue(f) : throw new InvalidOperationException($"مفتاح غير معرّف: {key}");
    }

    public async Task DemandEnabledAsync(string key, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(key, ct)) throw new FeatureDisabledException(key);
    }
}

/// <summary>حالة الاتصال بالمزوّدين: تُفحص فعليًا وتُحفظ، ولا تُستنتج من تفعيل الخاصية.</summary>
internal sealed class ConnectorStatusStore(Db db, IEnumerable<IConnector> connectors) : IConnectorStatusStore
{
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(60);

    public async Task<ConnectorHealth?> GetAsync(string code, CancellationToken ct = default)
    {
        var row = await db.QuerySingleAsync("SELECT status, detail, latency_ms FROM config.connector_status WHERE code = @code", new { code },
            r => (Status: r.GetString(0), Detail: r.Str("detail") ?? "", Latency: r.IsDBNull(2) ? (int?)null : r.GetInt32(2)), ct);
        return row == default ? null : new ConnectorHealth(Parse(row.Status), row.Detail, row.Latency);
    }

    public IConnector Find(string code) => connectors.FirstOrDefault(c => c.Code == code) ?? throw new NotFoundException($"موصل غير معروف: {code}");

    public async Task<ConnectorHealth> CheckAsync(string code, CancellationToken ct = default)
    {
        var health = await Find(code).CheckAsync(ct);
        await db.ExecuteAsync("""
            INSERT INTO config.connector_status (code, status, detail, latency_ms, checked_at) VALUES (@code, @status, @detail, @latency, now())
            ON CONFLICT (code) DO UPDATE SET status = EXCLUDED.status, detail = EXCLUDED.detail, latency_ms = EXCLUDED.latency_ms, checked_at = now()
            """, new { code, status = health.StateCode, detail = health.Detail, latency = health.LatencyMs }, ct);
        return health;
    }

    public async Task DemandAvailableAsync(string code, CancellationToken ct = default)
    {
        var checkedAt = await db.ScalarAsync<DateTime?>("SELECT checked_at FROM config.connector_status WHERE code = @code", new { code }, ct);
        var health = checkedAt is { } t && DateTime.UtcNow - t < Freshness && await GetAsync(code, ct) is { } cached
            ? cached
            : await CheckAsync(code, ct);
        if (health.State != ConnectorState.Healthy) throw new ProviderUnavailableException(code, health.StateCode);
    }

    private static ConnectorState Parse(string s) => s switch
    {
        "healthy" => ConnectorState.Healthy,
        "degraded" => ConnectorState.Degraded,
        "failing" => ConnectorState.Failing,
        "disabled" => ConnectorState.Disabled,
        _ => ConnectorState.NotConfigured,
    };
}

internal sealed class SystemClock : IClock
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    public DateTime UtcNow => DateTime.UtcNow;
    public DateOnly TodayLocal => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Riyadh));
}

internal sealed class NumberingService : INumberingService
{
    public async Task<int> NextAsync(ISql sql, string scopeKey, int year, CancellationToken ct = default) =>
        await sql.ScalarAsync<int>("""
            INSERT INTO config.numbering_sequences (scope_key, year, last_value) VALUES (@scopeKey, @year, 1)
            ON CONFLICT (scope_key, year) DO UPDATE SET last_value = config.numbering_sequences.last_value + 1
            RETURNING last_value
            """, new { scopeKey, year }, ct);
}
