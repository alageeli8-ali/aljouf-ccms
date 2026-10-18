using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ccms.Modules.Ai;

/// <summary>وحدة الذكاء الاصطناعي المستقلة والاختيارية (10، ADR-021). تعطيلها أو فشل مزوّدها لا يمس المحاضر ولا القرارات ولا المهام ولا البحث التقليدي:
/// لا وحدة أساسية تعتمد عليها، وهي لا تكتب في جداول أي وحدة أخرى.</summary>
public sealed class AiModule : IModule
{
    public string Code => "AI";
    public bool IsOptional => true;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.Section));
        services.AddHttpClient("ai", c => c.Timeout = TimeSpan.FromMinutes(6));
        services.AddSingleton<MockAiFaults>();
        services.AddSingleton<AiInflight>();
        services.AddScoped<IAiProvider, MockAiProvider>();
        services.AddScoped<IAiProvider, OpenAiCompatibleProvider>();
        services.AddScoped<AiProviderRegistry>();
        services.AddScoped<AiService>();
        services.AddScoped<AiData>();
        services.AddScoped<IConnector, AiConnector>();
        services.AddScoped<IFeatureValueValidator, AiSettingsValidator>();
        services.AddScoped<IFeatureImpactProvider, AiImpact>();
        if (configuration.GetValue("Workers:Enabled", true)) services.AddHostedService<AiWorker>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/ai").WithTags("AI (optional)");
        g.MapGet("/status", Status);
        Functions.Map(g);
        Outputs.Map(g);
        Transcription.Map(g);
        g.MapPost("/maintenance/run", Maintenance);
        g.MapGet("/mock/faults", GetFaults);
        g.MapPut("/mock/faults", SetFaults);
        var admin = api.MapGroup("/admin/ai").WithTags("AI (optional)");
        admin.MapGet("/overview", Overview);
        admin.MapGet("/requests", RequestLog);
    }

    // ---------------- حالة الوحدة للمستخدم ----------------

    /// <summary>للواجهة: الوظائف المفعلة وجاهزية المزوّد. 404 إن كان AI_ENABLED معطلًا — الواجهة تخفي كل إجراءات الذكاء الاصطناعي.</summary>
    private static async Task<IResult> Status(IFeatureService features, AiService svc, AiProviderRegistry providers, ICurrentUser me, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        await features.DemandEnabledAsync(FeatureKeys.AiEnabled, ct);
        var s = await svc.SettingsAsync(ct);
        var (p, ready) = await providers.ResolveAsync(s, false, ct);
        var (_, readyT) = await providers.ResolveAsync(s, true, ct);
        var fns = new Dictionary<string, bool>();
        foreach (var fn in AiFunctions.All) fns[fn] = await features.IsEnabledAsync(AiFunctions.Flag[fn], ct);
        var u = await svc.UsageAsync(ct);
        return Results.Ok(new
        {
            enabled = true, functions = fns,
            provider = new { code = s.Provider, name = p?.NameAr, nameEn = p?.NameEn, testOnly = p?.TestOnly ?? false, ready = ready.Ready, reason = ready.Reason,
                reasonText = OpenAiCompatibleProvider.Describe(ready.Reason), transcriptionReady = readyT.Ready, transcriptionReason = readyT.Reason },
            usage = new { myToday = u.MyToday, dailyLimit = s.DailyPerUser },
            policy = new { maxClassification = DataPolicy.AllowedLabel(s), hosting = s.Hosting },
        });
    }

    // ---------------- الإدارة ----------------

    private static async Task DemandAdminView(IAccessService access, CancellationToken ct)
    {
        if (!await access.HasAsync(Permissions.AdminSettingsView, Scope.System, ct) && !await access.HasAsync(Permissions.AiLogsView, Scope.System, ct))
            throw new ForbiddenException(permission: Permissions.AiLogsView);
    }

    private static async Task<IResult> Overview(IAccessService access, AiService svc, AiProviderRegistry providers, ISecretStore secrets, IConnectorStatusStore connectors,
        IFeatureService features, Db db, IOptions<AiOptions> options, AiInflight inflight, MockAiFaults faults, IHostEnvironment env, CancellationToken ct)
    {
        await DemandAdminView(access, ct);
        var s = await svc.SettingsAsync(ct);
        var (p, ready) = await providers.ResolveAsync(s, false, ct);
        var (_, readyT) = await providers.ResolveAsync(s, true, ct);
        var u = await svc.UsageAsync(ct);
        var fns = new List<object>();
        foreach (var fn in AiFunctions.All) fns.Add(new { code = fn, flag = AiFunctions.Flag[fn], enabled = await features.IsEnabledAsync(AiFunctions.Flag[fn], ct) });
        var byFunction = await db.QueryAsync("""
            SELECT function, status, count(*), coalesce(sum(coalesce(input_tokens,0) + coalesce(output_tokens,0)), 0)::bigint, coalesce(avg(duration_ms), 0)::int
            FROM ai.requests WHERE created_at >= now() - interval '30 days' GROUP BY function, status ORDER BY function, status
            """, null, r => new { function = r.GetString(0), status = r.GetString(1), count = r.GetInt64(2), tokens = r.GetInt64(3), avgMs = r.GetInt32(4) }, ct);
        var alerts = await db.QueryAsync("SELECT metric, threshold, value, limit_value, created_at FROM ai.usage_alerts WHERE period = @p ORDER BY created_at",
            new { p = u.Period }, r => new { metric = r.GetString(0), threshold = r.GetInt32(1), value = r.GetInt64(2), limit = r.GetInt64(3), at = r.GetDateTime(4) }, ct);
        var counts = await db.QuerySingleAsync("""
            SELECT (SELECT count(*) FROM ai.outputs WHERE status = 'held'), (SELECT count(*) FROM ai.outputs WHERE status IN ('saved','applied')),
                   (SELECT count(*) FROM ai.outputs WHERE status = 'draft'), (SELECT count(*) FROM ai.jobs WHERE status IN ('queued','running')),
                   (SELECT count(*) FROM ai.jobs WHERE status = 'held'), (SELECT count(*) FROM ai.jobs WHERE status = 'failed'),
                   (SELECT count(*) FROM ai.requests WHERE created_at >= (now() AT TIME ZONE 'Asia/Riyadh')::date AT TIME ZONE 'Asia/Riyadh' AND counted)
            """, null, r => Enumerable.Range(0, 7).Select(r.GetInt64).ToArray(), ct) ?? new long[7];
        var connection = await connectors.GetAsync("ai", ct);
        long Pct(long v, long l) => l <= 0 ? 0 : v * 100 / l;
        return Results.Ok(new
        {
            settings = new
            {
                enabled = s.Enabled, provider = s.Provider, providerName = p?.NameAr, providerNameEn = p?.NameEn, testOnly = p?.TestOnly ?? false, s.Hosting,
                configuredMaxClassification = s.MaxClassification, effectiveMaxClassification = DataPolicy.AllowedLabel(s), s.BaseUrl, s.Model, s.TranscriptionModel,
                s.TimeoutSeconds, s.DailyPerUser, s.MonthlyRequests, s.MonthlyTokens, s.AlertPercent, s.RetentionDays,
            },
            functions = fns,
            readiness = new { ready.Ready, ready.Reason, text = OpenAiCompatibleProvider.Describe(ready.Reason), transcriptionReady = readyT.Ready, transcriptionReason = readyT.Reason,
                transcriptionText = OpenAiCompatibleProvider.Describe(readyT.Reason) },
            connection = connection is null ? null : new { status = connection.StateCode, connection.Detail, connection.LatencyMs },
            secret = await secrets.InfoAsync(SecretNames.AiApiKey, ct),
            allowedHosts = options.Value.AllowedHosts,
            usage = new
            {
                period = u.Period, todayRequests = counts[6], monthRequests = u.MonthRequests, monthTokens = u.MonthTokens, requestsPct = Pct(u.MonthRequests, s.MonthlyRequests),
                tokensPct = Pct(u.MonthTokens, s.MonthlyTokens), alertPercent = s.AlertPercent,
            },
            alerts, byFunction,
            outputs = new { held = counts[0], kept = counts[1], drafts = counts[2] },
            jobs = new { active = counts[3], held = counts[4], failed = counts[5] },
            inflight = inflight.Count,
            mock = env.IsProduction() ? null : faults.Snapshot(),
        });
    }

    private static async Task<IResult> RequestLog(string? status, string? function, int? page, int? pageSize, IAccessService access, Db db, CancellationToken ct)
    {
        await DemandAdminView(access, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { status, function, limit = p.PageSize, offset = p.Offset };
        const string where = " WHERE (@status::text IS NULL OR r.status = @status) AND (@function::text IS NULL OR r.function = @function)";
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM ai.requests r" + where, args, ct);
        // بيانات تشغيلية فقط: لا نص مدخل ولا مخرج
        var items = await db.QueryAsync("""
            SELECT r.id, r.created_at, r.function, u.display_name_ar, r.status, r.error_code, r.duration_ms, r.input_tokens, r.output_tokens, r.usage_estimated, r.provider,
                   r.model, r.source_count, r.excluded_sources, r.redactions, r.max_classification, r.counted, r.output_id, r.subject_type, b.name_ar
            FROM ai.requests r LEFT JOIN iam.users u ON u.id = r.user_id LEFT JOIN bodies.bodies b ON b.id = r.body_id
            """ + where + " ORDER BY r.created_at DESC LIMIT @limit OFFSET @offset", args, r => new
        {
            id = r.GetGuid(0), at = r.GetDateTime(1), function = r.GetString(2), user = r.Str("display_name_ar"), status = r.GetString(4), errorCode = r.Str("error_code"),
            durationMs = r.IsDBNull(6) ? (int?)null : r.GetInt32(6), inputTokens = r.IsDBNull(7) ? (int?)null : r.GetInt32(7), outputTokens = r.IsDBNull(8) ? (int?)null : r.GetInt32(8),
            usageEstimated = r.GetBoolean(9), provider = r.Str("provider"), model = r.Str("model"), sources = r.IsDBNull(12) ? (int?)null : r.GetInt32(12),
            excluded = r.GetInt32(13), redactions = r.GetInt32(14), classification = r.Str("max_classification"), counted = r.GetBoolean(16), outputId = r.Uuid("output_id"),
            subjectType = r.Str("subject_type"), body = r.Str("name_ar"),
        }, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    public sealed record FaultsRequest([property: Required, OneOf("none", "unavailable", "timeout", "error", "invalid_credentials", "invalid_output", "hallucinate")] string Mode,
        int DelayMs = 0, int Count = 0);

    private static async Task<IResult> GetFaults(IAccessService access, MockAiFaults faults, IHostEnvironment env, CancellationToken ct)
    {
        if (env.IsProduction()) throw new NotFoundException();
        await access.DemandAsync(Permissions.AdminSettingsView, Scope.System, ct);
        return Results.Ok(faults.Snapshot());
    }

    private static async Task<IResult> SetFaults(FaultsRequest req, IAccessService access, MockAiFaults faults, IHostEnvironment env, CancellationToken ct)
    {
        if (env.IsProduction()) throw new NotFoundException();
        await access.DemandAsync(Permissions.AdminSettingsManage, Scope.System, ct);
        ValidationFilter<FaultsRequest>.Validate(req);
        faults.Set(req.Mode, req.DelayMs, req.Count);
        return Results.Ok(faults.Snapshot());
    }

    private static async Task<IResult> Maintenance(IAccessService access, IServiceProvider sp, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsManage, Scope.System, ct);
        return Results.Ok(await AiWorker.RunOnceAsync(sp, ct));
    }
}

/// <summary>حالة الاتصال بمزوّد الذكاء الاصطناعي — مستقلة عن التفعيل: التفعيل لا يعني أن الخدمة متاحة أو أن بيانات الاعتماد صحيحة.</summary>
internal sealed class AiConnector(IFeatureService features, AiProviderRegistry providers) : IConnector
{
    public string Code => "ai";
    public string NameAr => "مزوّد الذكاء الاصطناعي";
    public string NameEn => "AI provider";

    public async Task<ConnectorHealth> CheckAsync(CancellationToken ct = default)
    {
        var s = await AiSettings.LoadAsync(features, ct);
        var (p, ready) = await providers.ResolveAsync(s, false, ct);
        if (p is null) return new ConnectorHealth(ConnectorState.NotConfigured, OpenAiCompatibleProvider.Describe(ready.Reason));
        return await p.CheckAsync(s, ct);
    }
}

/// <summary>عنوان المزوّد لا يُقبل إلا بمضيف من القائمة المسموحة في بيئة الخادم وبـ https (سيادة البيانات)؛ والمزوّد التجريبي لا يُختار في الإنتاج.</summary>
internal sealed class AiSettingsValidator(IOptions<AiOptions> options, IHostEnvironment env) : IFeatureValueValidator
{
    public IReadOnlyCollection<string> FeatureKeys => [Contracts.FeatureKeys.AiBaseUrl, Contracts.FeatureKeys.AiProvider];

    public Task<string?> ValidateAsync(string key, string value, CancellationToken ct = default)
    {
        if (key == Contracts.FeatureKeys.AiProvider)
            return Task.FromResult(value == "mock" && env.IsProduction() ? "المزوّد التجريبي ممنوع في بيئة الإنتاج" : null);
        if (value.Length == 0) return Task.FromResult<string?>(null);
        return Task.FromResult(OpenAiCompatibleProvider.EndpointProblem(value, options.Value, env.IsProduction()) is { } problem
            ? OpenAiCompatibleProvider.Describe(problem) + (problem == "host_not_allowed" ? $"؛ المسموح: {string.Join("، ", options.Value.AllowedHosts.DefaultIfEmpty("لا شيء"))}" : "")
            : null);
    }
}

/// <summary>أثر تعطيل الذكاء الاصطناعي أو تغيير مزوّده على الطلبات الجارية: يتطلب تأكيدًا صريحًا، ولا يُحذف شيء.</summary>
internal sealed class AiImpact(Db db, AiInflight inflight) : IFeatureImpactProvider
{
    public IReadOnlyCollection<string> FeatureKeys =>
    [
        Contracts.FeatureKeys.AiEnabled, Contracts.FeatureKeys.AiAudioTranscription, Contracts.FeatureKeys.AiProvider, Contracts.FeatureKeys.AiMinutesSummary,
        Contracts.FeatureKeys.AiDecisionExtraction, Contracts.FeatureKeys.AiTaskExtraction, Contracts.FeatureKeys.AiSmartSearch,
    ];

    public async Task<IReadOnlyList<FeatureImpactItem>> AssessAsync(string key, string oldValue, string newValue, CancellationToken ct = default)
    {
        if (newValue == "true") return [];
        var jobs = await db.ScalarAsync<long>("SELECT count(*) FROM ai.jobs WHERE status IN ('queued','running')", null, ct);
        var list = new List<FeatureImpactItem>
        {
            new("ai_inflight_requests", "طلبات جارية لدى المزوّد الآن: تكتمل لكن نتائجها تُحجز ولا تُعرض حتى إعادة التفعيل والإفراج عنها",
                "Requests in flight complete, but their results are held and hidden until re-enabled and released", inflight.Count),
        };
        if (key is Contracts.FeatureKeys.AiEnabled or Contracts.FeatureKeys.AiAudioTranscription or Contracts.FeatureKeys.AiProvider)
            list.Add(new FeatureImpactItem("ai_queued_jobs", "مهام تفريغ صوتي منتظرة: تُحجز بمدخلاتها ولا تُرسل، ولا تُستأنف إلا بإجراء صريح",
                "Queued transcription jobs are held with their inputs, not sent, and resumed only explicitly", (int)jobs));
        return list;
    }
}

/// <summary>العامل الخلفي: معالجة مهام التفريغ، وحجزها عند التعطيل، وتطبيق مدة الاحتفاظ على المسودات غير المحفوظة.</summary>
internal sealed class AiWorker(IServiceScopeFactory scopes, IOptions<AiOptions> options, ILogger<AiWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, options.Value.WorkerIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                using var scope = scopes.CreateScope();
                await RunOnceAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { log.LogError(e, "فشل دورة صيانة الذكاء الاصطناعي"); }
        }
    }

    public static async Task<object> RunOnceAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<Db>();
        var features = sp.GetRequiredService<IFeatureService>();
        var svc = sp.GetRequiredService<AiService>();
        var jobs = await Transcription.ProcessAsync(db, features, sp.GetRequiredService<AiProviderRegistry>(), sp.GetRequiredService<IDocumentStore>(), svc, ct);
        var s = await svc.SettingsAsync(ct);
        // الاحتفاظ: يُفرغ محتوى غير المحفوظ بعد المدة ويبقى سجله؛ المحفوظ والمطبق لا يُمس
        var expired = await db.ExecuteAsync("""
            UPDATE ai.outputs SET status = 'expired', content = NULL, citations = '[]', held_reason = NULL, purged_at = now(), updated_at = now()
            WHERE status IN ('draft','held') AND created_at < now() - make_interval(days => @days)
            """, new { days = s.RetentionDays }, ct);
        var purged = await db.ExecuteAsync("""
            UPDATE ai.outputs SET content = NULL, citations = '[]', purged_at = now(), updated_at = now()
            WHERE status = 'discarded' AND purged_at IS NULL AND coalesce(reviewed_at, created_at) < now() - make_interval(days => @days)
            """, new { days = s.RetentionDays }, ct);
        return new { jobs, retention = new { expired, purged, days = s.RetentionDays } };
    }
}
