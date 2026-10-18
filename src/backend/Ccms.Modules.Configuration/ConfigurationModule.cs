using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ccms.Modules.Configuration;

/// <summary>وحدة الإعدادات المركزية وإدارة الخصائص — System Configuration &amp; Feature Management.</summary>
public sealed class ConfigurationModule : IModule
{
    public string Code => "ADM";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddMemoryCache();
        services.AddSingleton<FeatureStore>();
        services.AddScoped<IFeatureService, FeatureService>();
        services.AddScoped<ConnectorStatusStore>();
        services.AddScoped<IConnectorStatusStore>(sp => sp.GetRequiredService<ConnectorStatusStore>());
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<INumberingService, NumberingService>();
        services.AddScoped<SecretStore>();
        services.AddScoped<ISecretStore>(sp => sp.GetRequiredService<SecretStore>());
        services.AddSingleton<CircuitRegistry>();
        services.AddScoped<IConnectorRuntime, ConnectorRuntime>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        api.MapGet("/features", Effective).WithTags("Configuration");

        var g = api.MapGroup("/admin").WithTags("Configuration");
        g.MapGet("/features", List);
        g.MapGet("/features/history", History);
        g.MapGet("/features/{key}/impact", Impact);
        g.MapPut("/features/{key}", Update).Validate<UpdateFeatureRequest>();
        g.MapGet("/connectors", Connectors);
        g.MapPost("/connectors/{code}/check", Check);
        Secrets.Map(g);
        IntegrationsAdmin.Map(g);
    }

    public sealed record UpdateFeatureRequest(
        [property: Required(AllowEmptyStrings = true, ErrorMessage = "القيمة مطلوبة"), MaxLength(300)] string Value,
        [property: Required(ErrorMessage = "سبب التغيير مطلوب"), MinLength(5, ErrorMessage = "اذكر سببًا واضحًا (5 أحرف على الأقل)"), MaxLength(1000)] string Reason,
        bool ConfirmImpact = false);

    /// <summary>القيم الفعلية لكل مستخدم مصادق؛ الواجهة تخفي العناصر بها، لكن القرار النهائي دائمًا في الخادم.</summary>
    private static async Task<IResult> Effective(FeatureStore store, CancellationToken ct)
    {
        var all = await store.AllAsync(ct);
        return Results.Ok(all.Values.ToDictionary(f => f.Key, f => new { enabled = store.IsEffective(all, f.Key), value = store.RawValue(f) }));
    }

    private static async Task<IResult> List(FeatureStore store, Db db, IAccessService access, IEnumerable<IConnector> connectors, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsView, Scope.System, ct);
        var all = await store.AllAsync(ct);
        var statuses = (await db.QueryAsync("SELECT code, status, detail, latency_ms, checked_at FROM config.connector_status", null, r => new
        {
            code = r.GetString(0), status = r.GetString(1), detail = r.Str("detail"),
            latencyMs = r.IsDBNull(3) ? (int?)null : r.GetInt32(3), checkedAt = r.Get<DateTime>("checked_at"),
        }, ct)).ToDictionary(x => x.code);
        var names = connectors.ToDictionary(c => c.Code, c => new { c.NameAr, c.NameEn });
        return Results.Ok(all.Values.OrderBy(f => f.SortOrder).Select(f => new
        {
            key = f.Key,
            category = f.Category,
            valueType = f.ValueType,
            value = store.RawValue(f),
            storedValue = f.Value,
            defaultValue = f.DefaultValue,
            allowedValues = f.AllowedValues,
            minValue = f.MinValue,
            maxValue = f.MaxValue,
            maxLength = f.MaxLength,
            parentKey = f.ParentKey,
            // حالة الإعداد
            effectiveEnabled = store.IsEffective(all, f.Key),
            blockedBy = store.BlockedBy(all, f.Key),
            locked = store.LockedValue(f.Key) is not null,
            // حالة الاتصال — مستقلة عن الإعداد
            connector = f.ConnectorCode is { } code ? new
            {
                code,
                nameAr = names.GetValueOrDefault(code)?.NameAr ?? code,
                nameEn = names.GetValueOrDefault(code)?.NameEn ?? code,
                status = statuses.GetValueOrDefault(code)?.status ?? "not_configured",
                detail = statuses.GetValueOrDefault(code)?.detail,
                latencyMs = statuses.GetValueOrDefault(code)?.latencyMs,
                checkedAt = statuses.GetValueOrDefault(code)?.checkedAt,
            } : null,
            nameAr = f.NameAr,
            nameEn = f.NameEn,
            descriptionAr = f.DescriptionAr,
            descriptionEn = f.DescriptionEn,
            updatedAt = f.UpdatedAt,
            updatedBy = f.UpdatedByName,
        }));
    }

    private static async Task<IReadOnlyList<FeatureImpactItem>> AssessAsync(IEnumerable<IFeatureImpactProvider> providers, string key, string oldValue,
        string newValue, CancellationToken ct)
    {
        var items = new List<FeatureImpactItem>();
        foreach (var p in providers.Where(p => p.FeatureKeys.Contains(key)))
            items.AddRange(await p.AssessAsync(key, oldValue, newValue, ct));
        return items;
    }

    private static async Task<IResult> Impact(string key, string? value, FeatureStore store, IAccessService access,
        IEnumerable<IFeatureImpactProvider> providers, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsView, Scope.System, ct);
        var all = await store.AllAsync(ct);
        if (!all.TryGetValue(key, out var f)) throw new NotFoundException("مفتاح غير معروف");
        var newValue = value ?? (f.ValueType == "boolean" ? (store.RawValue(f) == "true" ? "false" : "true") : f.Value);
        var items = await AssessAsync(providers, key, store.RawValue(f), newValue, ct);
        return Results.Ok(new { key, oldValue = store.RawValue(f), newValue, requiresConfirmation = items.Any(i => i.Count > 0), items });
    }

    private static async Task<IResult> Update(string key, UpdateFeatureRequest req, FeatureStore store, Db db, IAccessService access,
        ICurrentUser me, IAuditWriter audit, IEnumerable<IFeatureImpactProvider> providers, IEnumerable<IFeatureValueValidator> validators,
        ILoggerFactory logs, CancellationToken ct)
    {
        async Task Log(string result, string? oldValue, string? detail, IReadOnlyList<FeatureImpactItem>? impact = null) =>
            await db.ExecuteAsync("""
                INSERT INTO config.feature_flag_history (id, flag_key, old_value, new_value, changed_by, actor_display, reason, result, result_detail, impact, correlation_id)
                VALUES (@id, @key, @oldValue, @newValue, @by, @actor, @reason, @result, @detail, @impact::jsonb, @corr)
                """, new
            {
                id = Guid.CreateVersion7(), key, oldValue, newValue = req.Value, by = me.UserId, actor = me.DisplayName, reason = req.Reason,
                result, detail, impact = impact is null ? null : Json.Serialize(impact), corr = me.CorrelationId,
            }, ct);

        // 1) الصلاحية في الخادم؛ المحاولة غير المخولة تُسجل ثم تُرفض
        try
        {
            await access.DemandAsync(Permissions.AdminSettingsManage, Scope.System, ct);
        }
        catch (ForbiddenException)
        {
            await Log("denied", null, "المستخدم لا يملك صلاحية admin.settings.manage");
            await audit.WriteAsync(new AuditEntry("ADM", "config.flag_change_denied", "feature_flag", key, After: new { req.Value }, Outcome: "denied"), ct);
            throw;
        }

        // 2) التحقق من المفتاح والقيمة
        var all = await store.AllAsync(ct);
        if (!all.TryGetValue(key, out var f))
        {
            await Log("rejected_validation", null, "مفتاح غير معروف");
            throw new NotFoundException("مفتاح غير معروف");
        }
        var current = store.RawValue(f);
        string? invalid =
            store.LockedValue(key) is not null ? "المفتاح مقفل من إعدادات الخادم ولا يمكن تغييره من الواجهة" :
            f.ValueType == "boolean" && req.Value is not ("true" or "false") ? "القيمة يجب أن تكون true أو false" :
            f.ValueType == "enum" && !(f.AllowedValues ?? []).Contains(req.Value) ? $"القيمة غير مسموحة؛ المسموح: {string.Join("، ", f.AllowedValues ?? [])}" :
            f.ValueType == "integer" && !long.TryParse(req.Value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _) ? "القيمة يجب أن تكون عددًا صحيحًا" :
            f.ValueType == "integer" && f.MinValue is { } min && long.Parse(req.Value, System.Globalization.CultureInfo.InvariantCulture) < min ? $"أقل قيمة مسموحة: {min}" :
            f.ValueType == "integer" && f.MaxValue is { } max && long.Parse(req.Value, System.Globalization.CultureInfo.InvariantCulture) > max ? $"أعلى قيمة مسموحة: {max}" :
            f.ValueType == "text" && f.MaxLength is { } len && req.Value.Length > len ? $"أقصى طول: {len} حرفًا" :
            f.ValueType == "text" && req.Value.Any(char.IsControl) ? "القيمة تحتوي محارف غير مسموحة" :
            f.ValueType != "text" && req.Value.Length == 0 ? "القيمة مطلوبة" :
            req.Value == f.Value ? "القيمة الجديدة مطابقة للقيمة الحالية" :
            null;
        foreach (var v in validators.Where(v => v.FeatureKeys.Contains(key)))
            invalid ??= await v.ValidateAsync(key, req.Value, ct);
        if (invalid is not null)
        {
            await Log("rejected_validation", current, invalid);
            throw AppValidationException.For("value", invalid);
        }

        // 3) الأثر على العمليات الجارية يتطلب تأكيدًا صريحًا
        var impact = await AssessAsync(providers, key, current, req.Value, ct);
        if (impact.Any(i => i.Count > 0) && !req.ConfirmImpact)
        {
            await Log("confirmation_required", current, "يؤثر التغيير في عمليات جارية ولم يُؤكد", impact);
            throw new ConflictException("confirmation-required", "هذا التغيير يؤثر في عمليات جارية؛ راجع الأثر وأكد التغيير",
                new Dictionary<string, object?> { ["impact"] = impact });
        }

        // 4) الحفظ مع السجل والتدقيق في معاملة واحدة
        try
        {
            await db.InTransactionAsync(async tx =>
            {
                var locked = await tx.ScalarAsync<string>("SELECT value FROM config.feature_flags WHERE key = @key FOR UPDATE", new { key }, ct);
                if (locked != f.Value)
                    throw new ConflictException("concurrency-conflict", "تغيّرت القيمة من مستخدم آخر؛ أعد تحميل الصفحة");
                await tx.ExecuteAsync("UPDATE config.feature_flags SET value = @value, updated_at = now(), updated_by = @by WHERE key = @key",
                    new { key, value = req.Value, by = me.UserId }, ct);
                await tx.ExecuteAsync("""
                    INSERT INTO config.feature_flag_history (id, flag_key, old_value, new_value, changed_by, actor_display, reason, result, impact, correlation_id)
                    VALUES (@id, @key, @old, @new, @by, @actor, @reason, 'succeeded', @impact::jsonb, @corr)
                    """, new
                {
                    id = Guid.CreateVersion7(), key, old = f.Value, @new = req.Value, by = me.UserId, actor = me.DisplayName,
                    reason = req.Reason, impact = impact.Count > 0 ? Json.Serialize(impact) : null, corr = me.CorrelationId,
                }, ct);
                await audit.WriteAsync(tx, new AuditEntry("ADM", "config.flag_changed", "feature_flag", key,
                    Before: new { value = f.Value }, After: new { value = req.Value, req.Reason, confirmedImpact = impact.Where(i => i.Count > 0) }), ct);
            }, ct);
        }
        catch (ConflictException) { throw; }
        catch (Exception e)
        {
            logs.CreateLogger<ConfigurationModule>().LogError(e, "فشل حفظ المفتاح {Key}", key);
            await Log("failed", current, e.GetType().Name);
            throw;
        }
        finally
        {
            store.Invalidate();
        }

        var fresh = await store.AllAsync(ct);
        return Results.Ok(new
        {
            key,
            oldValue = current,
            value = req.Value,
            effectiveEnabled = store.IsEffective(fresh, key),
            blockedBy = store.BlockedBy(fresh, key),
            result = "succeeded",
        });
    }

    private static async Task<IResult> History(string? key, string? result, int? page, int? pageSize, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsView, Scope.System, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { key, result, limit = p.PageSize, offset = p.Offset };
        const string where = "WHERE (@key::text IS NULL OR h.flag_key = @key) AND (@result::text IS NULL OR h.result = @result)";
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM config.feature_flag_history h " + "\n" + where, args, ct);
        var items = await db.QueryAsync("""
            SELECT h.id, h.flag_key, h.old_value, h.new_value, h.changed_at, h.changed_by, h.actor_display, h.reason, h.result,
                   h.result_detail, h.impact::text AS impact, h.correlation_id
            FROM config.feature_flag_history h
            """ + "\n" + where + " ORDER BY h.changed_at DESC LIMIT @limit OFFSET @offset", args, r => new
        {
            id = r.Get<Guid>("id"), key = r.Get<string>("flag_key"), oldValue = r.Str("old_value"), newValue = r.Str("new_value"),
            changedAt = r.Get<DateTime>("changed_at"), changedBy = r.Uuid("changed_by"), actor = r.Get<string>("actor_display"),
            reason = r.Str("reason"), result = r.Get<string>("result"), resultDetail = r.Str("result_detail"), impact = r.Str("impact"),
            correlationId = r.Str("correlation_id"),
        }, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    private static async Task<IResult> Connectors(Db db, IAccessService access, IEnumerable<IConnector> connectors, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsView, Scope.System, ct);
        var rows = await db.QueryAsync("SELECT code, status, detail, latency_ms, checked_at FROM config.connector_status ORDER BY code", null, r => new
        {
            code = r.GetString(0), status = r.GetString(1), detail = r.Str("detail"),
            latencyMs = r.IsDBNull(3) ? (int?)null : r.GetInt32(3), checkedAt = r.Get<DateTime>("checked_at"),
        }, ct);
        var names = connectors.ToDictionary(c => c.Code);
        return Results.Ok(rows.Select(r => new
        {
            r.code, r.status, r.detail, r.latencyMs, r.checkedAt,
            nameAr = names.GetValueOrDefault(r.code)?.NameAr ?? r.code,
            nameEn = names.GetValueOrDefault(r.code)?.NameEn ?? r.code,
        }));
    }

    private static async Task<IResult> Check(string code, ConnectorStatusStore status, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsManage, Scope.System, ct);
        var health = await status.CheckAsync(code, ct);
        await audit.WriteAsync(new AuditEntry("ADM", "config.connector_checked", "connector", code, After: new { status = health.StateCode, health.Detail }), ct);
        return Results.Ok(new { code, status = health.StateCode, detail = health.Detail, latencyMs = health.LatencyMs });
    }
}
