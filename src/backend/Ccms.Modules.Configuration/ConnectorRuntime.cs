using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Ccms.Modules.Configuration;

/// <summary>حالة قاطع الدائرة لكل موصل (مشتركة على مستوى العملية).</summary>
internal sealed class CircuitRegistry
{
    private sealed class State { public int Failures; public DateTime? OpenUntil; }
    private readonly ConcurrentDictionary<string, State> _s = new();

    public (string, int, DateTime?) Get(string c)
    {
        var s = _s.GetOrAdd(c, _ => new State());
        lock (s) return (s.OpenUntil is { } t ? (t > DateTime.UtcNow ? "open" : "half_open") : "closed", s.Failures, s.OpenUntil);
    }

    public bool IsOpen(string c)
    {
        var s = _s.GetOrAdd(c, _ => new State());
        lock (s) return s.OpenUntil is { } t && t > DateTime.UtcNow;
    }

    public void Success(string c)
    {
        var s = _s.GetOrAdd(c, _ => new State());
        lock (s) { s.Failures = 0; s.OpenUntil = null; }
    }

    public void Failure(string c, int threshold, TimeSpan open)
    {
        var s = _s.GetOrAdd(c, _ => new State());
        lock (s)
        {
            s.Failures++;
            if (s.Failures >= threshold) s.OpenUntil = DateTime.UtcNow + open;
        }
    }

    public void Reset(string c) => Success(c);
}

/// <summary>تنفيذ استدعاءات الموصلات (23 §4.2). لا يُسجل جسم الطلب أو الاستجابة ولا رؤوس المصادقة؛ رسالة الخطأ تُنقح من أي رمز أو سر وتُقتطع.</summary>
internal sealed partial class ConnectorRuntime(Db db, IFeatureService features, ICurrentUser me, IConfiguration configuration, CircuitRegistry circuits,
    ILogger<ConnectorRuntime> log) : IConnectorRuntime
{
    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9\-\._~\+/]+=*|(client_secret|password|api[_-]?key|access_token|code|sig)=([^&\s]+)|eyJ[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]*")]
    private static partial Regex Sensitive();

    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        var m = Sensitive().Replace(message, x => x.Groups[1].Success ? x.Groups[1].Value + "***" : x.Groups[2].Success ? x.Groups[2].Value + "=***" : "***");
        return m.Length > 300 ? m[..300] + "…" : m;
    }

    public (string State, int ConsecutiveFailures, DateTime? OpenUntil) Circuit(string connector) => circuits.Get(connector);

    private int Setting(string connector, string name, int fallback) =>
        int.TryParse(configuration[$"Integrations:Policies:{connector}:{name}"], out var v) ? v : fallback;

    public async Task<T> ExecuteAsync<T>(string connector, string operation, string? featureKey, ConnectorCallPolicy policy, Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default)
    {
        if (featureKey is not null && !await features.IsEnabledAsync(featureKey, ct))
        {
            await LogAsync(connector, operation, "skipped_disabled", 0, null, null, $"{featureKey} = false", ct);
            throw new IntegrationFailedException(connector, "disabled", "التكامل معطل من الإعدادات");
        }
        if (circuits.IsOpen(connector))
        {
            await LogAsync(connector, operation, "circuit_open", 0, null, null, "قاطع الدائرة مفتوح بعد إخفاقات متتالية", ct);
            throw new IntegrationFailedException(connector, "circuit_open", "أوقفت الاستدعاءات مؤقتًا بعد إخفاقات متتالية؛ ستُستأنف تلقائيًا");
        }
        var timeout = TimeSpan.FromSeconds(Math.Clamp(Setting(connector, "TimeoutSeconds", policy.TimeoutSeconds), 1, 120));
        var max = policy.Idempotent ? Math.Clamp(Setting(connector, "MaxAttempts", policy.MaxAttempts), 1, 6) : 1;
        var baseDelay = Math.Clamp(Setting(connector, "BaseDelayMs", policy.BaseDelayMs), 0, 30000);
        var threshold = Math.Max(1, int.TryParse(configuration["Integrations:Circuit:FailureThreshold"], out var th) ? th : 5);
        var openFor = TimeSpan.FromSeconds(Math.Max(1, int.TryParse(configuration["Integrations:Circuit:OpenSeconds"], out var os) ? os : 60));

        for (var attempt = 1; ; attempt++)
        {
            var sw = Stopwatch.StartNew();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            string status, reason, message;
            int? http = null;
            bool transient;
            try
            {
                var result = await action(cts.Token);
                circuits.Success(connector);
                await LogAsync(connector, operation, "succeeded", attempt, (int)sw.ElapsedMilliseconds, null, null, ct);
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                (status, reason, message, transient) = ("timeout", "timeout", $"انتهت المهلة ({timeout.TotalSeconds:0} ث)", true);
            }
            catch (ConnectorCallException e)
            {
                (status, reason, message, transient, http) = (e.Reason == "denied" ? "denied" : e.Reason == "not_configured" ? "not_configured" : "failed",
                    e.Reason, e.Message, e.Transient, e.HttpStatus);
            }
            catch (HttpRequestException e)
            {
                (status, reason, message, transient, http) = ("failed", "unavailable", e.Message, true, e.StatusCode is { } sc ? (int)sc : null);
            }
            catch (IntegrationFailedException)
            {
                throw;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // خطأ برمجي أو استجابة غير متوقعة: لا يُعاد (قد يكون الأثر وقع)
                (status, reason, message, transient) = ("failed", "rejected", e.GetType().Name + ": " + e.Message, false);
            }
            // الإخفاق الدائم بسبب الإعداد أو الصلاحية لا يفتح الدائرة؛ إخفاقات التوفر تفتحها
            if (transient) circuits.Failure(connector, threshold, openFor);
            var retry = transient && attempt < max && !circuits.IsOpen(connector);
            await LogAsync(connector, operation, retry ? "retrying" : status, attempt, (int)sw.ElapsedMilliseconds, http, message, ct);
            if (!retry)
            {
                log.LogWarning("فشل استدعاء الموصل {Connector}/{Operation} بعد {Attempts} محاولة: {Reason}", connector, operation, attempt, reason);
                throw new IntegrationFailedException(connector, reason, Redact(message));
            }
            var delay = baseDelay * Math.Pow(2, attempt - 1) * (0.8 + Random.Shared.NextDouble() * 0.4);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delay, 30000)), ct);
        }
    }

    private async Task LogAsync(string connector, string operation, string status, int attempt, int? ms, int? http, string? error, CancellationToken ct)
    {
        try
        {
            await db.ExecuteAsync("""
                INSERT INTO integ.connector_calls (id, connector, operation, status, attempt, duration_ms, http_status, error, actor_user_id, correlation_id)
                VALUES (@id, @connector, @operation, @status, @attempt, @ms, @http, @error, @actor, @corr)
                """, new
            {
                id = Guid.CreateVersion7(), connector, operation, status, attempt = Math.Max(attempt, 1), ms, http, error = error is null ? null : Redact(error),
                actor = me.IsAuthenticated ? me.UserId : (Guid?)null, corr = me.IsAuthenticated ? me.CorrelationId : null,
            }, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // التسجيل التشغيلي لا يُفشل العملية
            log.LogWarning(e, "تعذر تسجيل استدعاء الموصل {Connector}", connector);
        }
    }
}

/// <summary>شاشة التكاملات (23 §4.4): لكل موصل وصفه ومفتاحه وحالة اتصاله الفعلية آخر فحص، وإحصاءات الاستدعاءات، وحالة قاطع الدائرة، ومتطلباته الناقصة.</summary>
internal static class IntegrationsAdmin
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/integrations", Overview);
        g.MapGet("/integrations/{code}/calls", Calls);
        g.MapPost("/integrations/{code}/test", Test);
        g.MapPost("/integrations/{code}/circuit/reset", ResetCircuit);
    }

    private static async Task<IResult> Overview(Db db, IAccessService access, IEnumerable<IConnector> connectors, IFeatureService features, IConnectorRuntime runtime,
        CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsView, Scope.System, ct);
        var status = (await db.QueryAsync("SELECT code, status, detail, latency_ms, checked_at FROM config.connector_status", null,
            r => (Code: r.GetString(0), Status: r.GetString(1), Detail: r.Str("detail"), Latency: r.IsDBNull(3) ? (int?)null : r.GetInt32(3), At: r.GetDateTime(4)), ct))
            .ToDictionary(x => x.Code);
        var stats = (await db.QueryAsync("""
            SELECT connector, count(*) FILTER (WHERE status = 'succeeded'), count(*) FILTER (WHERE status IN ('failed','timeout','denied','not_configured')),
                   count(*) FILTER (WHERE status = 'retrying'), count(*) FILTER (WHERE status IN ('skipped_disabled','circuit_open')), max(created_at),
                   (SELECT c2.error FROM integ.connector_calls c2 WHERE c2.connector = c.connector AND c2.status IN ('failed','timeout','denied','not_configured') ORDER BY c2.created_at DESC LIMIT 1)
            FROM integ.connector_calls c WHERE created_at > now() - interval '24 hours' GROUP BY connector
            """, null, r => (Code: r.GetString(0), Ok: r.GetInt64(1), Failed: r.GetInt64(2), Retries: r.GetInt64(3), Skipped: r.GetInt64(4), Last: r.GetDateTime(5),
                LastError: r.Str("error")), ct)).ToDictionary(x => x.Code);
        var items = new List<object>();
        foreach (var c in connectors.OrderBy(c => c.Code))
        {
            var d = await c.DescribeAsync(ct);
            var enabled = d.FeatureKey is null ? (bool?)null : await features.IsEnabledAsync(d.FeatureKey, ct);
            var s = status.GetValueOrDefault(c.Code);
            var st = stats.GetValueOrDefault(c.Code);
            var circuit = runtime.Circuit(c.Code);
            var missing = d.Requirements.Where(r => !r.Present).Select(r => r.Key).ToArray();
            items.Add(new
            {
                code = c.Code, nameAr = d.NameAr, nameEn = d.NameEn, featureKey = d.FeatureKey, enabled, authType = d.AuthType,
                requirements = d.Requirements.Select(r => new { key = r.Key, descriptionAr = r.DescriptionAr, descriptionEn = r.DescriptionEn, present = r.Present, secret = r.Secret }),
                missing, requiredPermissions = d.RequiredPermissions ?? [], notesAr = d.NotesAr, notesEn = d.NotesEn,
                connection = s == default ? null : new { status = s.Status, detail = s.Detail, latencyMs = s.Latency, checkedAt = s.At },
                // «يعمل فعليًا» فقط مع فحص اتصال ناجح؛ التفعيل وحده لا يكفي
                verified = s != default && s.Status == "healthy",
                calls24h = st == default ? null : new { succeeded = st.Ok, failed = st.Failed, retries = st.Retries, skipped = st.Skipped, last = st.Last, lastError = st.LastError },
                circuit = new { state = circuit.State, failures = circuit.ConsecutiveFailures, openUntil = circuit.OpenUntil },
            });
        }
        return Results.Ok(items);
    }

    private static async Task<IResult> Calls(string code, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsView, Scope.System, ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT c.created_at, c.operation, c.status, c.attempt, c.duration_ms, c.http_status, c.error, u.display_name_ar
            FROM integ.connector_calls c LEFT JOIN iam.users u ON u.id = c.actor_user_id WHERE c.connector = @code ORDER BY c.created_at DESC LIMIT 100
            """, new { code }, r => new
        {
            at = r.GetDateTime(0), operation = r.GetString(1), status = r.GetString(2), attempt = r.GetInt32(3), durationMs = r.IsDBNull(4) ? (int?)null : r.GetInt32(4),
            httpStatus = r.IsDBNull(5) ? (int?)null : r.GetInt32(5), error = r.Str("error"), actor = r.Str("display_name_ar"),
        }, ct));
    }

    /// <summary>اختبار الاتصال: يفحص الموصل فعليًا ويحفظ النتيجة ويسجلها كاستدعاء. لا يغيّر تفعيل الخاصية.</summary>
    private static async Task<IResult> Test(string code, ConnectorStatusStore status, Db db, IAccessService access, IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsManage, Scope.System, ct);
        var sw = Stopwatch.StartNew();
        var health = await status.CheckAsync(code, ct);
        await db.ExecuteAsync("""
            INSERT INTO integ.connector_calls (id, connector, operation, status, attempt, duration_ms, error, actor_user_id, correlation_id)
            VALUES (@id, @code, 'connection_test', @s, 1, @ms, @err, @me, @corr)
            """, new
        {
            id = Guid.CreateVersion7(), code, s = health.State switch { ConnectorState.Healthy => "succeeded", ConnectorState.NotConfigured => "not_configured", _ => "failed" },
            ms = (int)sw.ElapsedMilliseconds, err = health.State == ConnectorState.Healthy ? null : ConnectorRuntime.Redact(health.Detail), me = me.UserId, corr = me.CorrelationId,
        }, ct);
        await audit.WriteAsync(new AuditEntry("ADM", "config.connector_checked", "connector", code, After: new { status = health.StateCode, detail = ConnectorRuntime.Redact(health.Detail) }), ct);
        return Results.Ok(new { code, status = health.StateCode, detail = ConnectorRuntime.Redact(health.Detail), latencyMs = health.LatencyMs });
    }

    private static async Task<IResult> ResetCircuit(string code, CircuitRegistry circuits, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsManage, Scope.System, ct);
        circuits.Reset(code);
        await audit.WriteAsync(new AuditEntry("ADM", "config.connector_circuit_reset", "connector", code), ct);
        return Results.NoContent();
    }
}
