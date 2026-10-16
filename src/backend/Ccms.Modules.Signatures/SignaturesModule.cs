using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ccms.Modules.Signatures;

/// <summary>وحدة التوقيع الإلكتروني المستقلة والاختيارية (09، ADR-020). تعطيلها لا يعطّل المحاضر ولا اعتمادها: يعمل المسار البديل.</summary>
public sealed class SignaturesModule : IModule
{
    public string Code => "SIG";
    public bool IsOptional => true;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SignatureOptions>(configuration.GetSection(SignatureOptions.Section));
        services.AddHttpContextAccessor();
        services.AddSingleton<MockFaults>();
        services.AddScoped<MockSignatureProvider>();
        services.AddScoped<ISignatureProvider>(sp => sp.GetRequiredService<MockSignatureProvider>());
        services.AddScoped<ISignatureProvider, NationalProviderAdapter>();
        services.AddScoped<ProviderRegistry>();
        services.AddScoped<VerificationService>();
        services.AddScoped<SigningService>();
        services.RemoveAll<ISignatureGateway>();
        services.AddScoped<ISignatureGateway, ESignatureGateway>();
        services.AddScoped<IFeatureImpactProvider, ESignatureImpact>();
        if (configuration.GetValue("Workers:Enabled", true)) services.AddHostedService<SignatureWorker>();
    }

    public void MapPublicEndpoints(IEndpointRouteBuilder app)
    {
        // استدعاءات المزوّد الراجعة: لا جلسة ولا رمز CSRF؛ المصادقة بتوقيع الرسالة (HMAC) ويُرفض غير الموقّع ويُسجَّل
        app.MapPost("/api/v1/esign/callbacks/{provider}", (string provider, HttpRequest request, SigningService svc, CancellationToken ct) =>
            svc.CallbackAsync(provider, request, ct)).AllowAnonymous().WithTags("E-signature").DisableAntiforgery();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/esign").WithTags("E-signature");
        g.MapGet("/status", Status);
        g.MapGet("/minutes/{minutesId:guid}", ForMinutes);
        g.MapPost("/minutes/{minutesId:guid}/requests", async (Guid minutesId, SigningService svc, CancellationToken ct) => Results.Ok(await svc.RestartAsync(minutesId, ct))).Idempotent();
        g.MapGet("/requests/{id:guid}", Card);
        g.MapPost("/reauth", (ReauthRequest req, SigningService svc, CancellationToken ct) => svc.ReauthAsync(req.Password, ct)).Validate<ReauthRequest>();
        g.MapPost("/requests/{id:guid}/sign", async (Guid id, SigningService.SignRequest req, SigningService svc, CancellationToken ct) => Results.Ok(await svc.SignAsync(id, req, ct))).Idempotent();
        g.MapPost("/requests/{id:guid}/decline", (Guid id, DeclineBody req, SigningService svc, CancellationToken ct) =>
            svc.DeclineAsync(id, new SigningService.DeclineRequest(req.Reason, req.OnBehalfOf), ct)).Validate<DeclineBody>();
        g.MapPost("/requests/{id:guid}/provider-session", (Guid id, SigningService.SessionRequest req, SigningService svc, CancellationToken ct) => svc.ProviderSessionAsync(id, req, ct));
        g.MapPost("/requests/{id:guid}/fallback", (Guid id, ReasonBody req, SigningService svc, CancellationToken ct) =>
            svc.FallbackAsync(id, new SigningService.ReasonRequest(req.Reason), ct)).Validate<ReasonBody>();
        g.MapPost("/requests/{id:guid}/hold", (Guid id, ReasonBody req, SigningService svc, CancellationToken ct) =>
            svc.HoldAsync(id, new SigningService.ReasonRequest(req.Reason), ct)).Validate<ReasonBody>();
        g.MapPost("/requests/{id:guid}/resume", (Guid id, SigningService svc, CancellationToken ct) => svc.ResumeAsync(id, ct));
        g.MapGet("/requests/{id:guid}/verify", Verify);
        g.MapPost("/verify", VerifyUpload).DisableAntiforgery();
        g.MapPost("/maintenance/run", Maintenance);
        Routes.Map(g);
        MockEndpoints.Map(g);
    }

    public sealed record ReauthRequest([property: Required(ErrorMessage = "كلمة المرور مطلوبة"), MaxLength(200)] string Password);
    public sealed record DeclineBody([property: Required(ErrorMessage = "سبب الرفض مطلوب"), MinLength(5), MaxLength(2000)] string Reason, Guid? OnBehalfOf);
    public sealed record ReasonBody([property: Required(ErrorMessage = "السبب مطلوب"), MinLength(5), MaxLength(2000)] string Reason);
    public sealed record MaintenanceRequest(DateTime? AsOf);

    // ---------------- القراءة ----------------

    private static async Task<object> ModuleStateAsync(IFeatureService features, ProviderRegistry providers, CancellationToken ct)
    {
        var s = await SigSettings.LoadAsync(features, ct);
        var (ready, reason) = ESignatureGateway.Readiness(s, s.Method, providers);
        var p = providers.Find(s.Provider);
        return new
        {
            enabled = s.Enabled, ready, reason,
            settings = new
            {
                method = s.Method, methodLabel = Labels.Type(s.Method), provider = s.Provider, providerName = p?.NameAr,
                productionGrade = p?.Readiness().ProductionGrade ?? false, s.ReauthRequired, s.Sequential, s.PdfOutput, s.MemberRequired, s.ChairRequired, s.SecretaryRequired,
            },
            notice = Labels.TypeNotice(s.Method, s.Method == SignatureTypes.InAppApproval || (p?.Readiness().ProductionGrade ?? false)),
        };
    }

    private static async Task<IResult> Status(IFeatureService features, ProviderRegistry providers, CancellationToken ct) =>
        Results.Ok(await ModuleStateAsync(features, providers, ct));

    private static async Task<IResult> ForMinutes(Guid minutesId, Db db, IAccessService access, IFeatureService features, ProviderRegistry providers,
        ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesView, Scope.Record("minutes", minutesId), ct);
        var perms = await access.GetPermissionsAsync(Scope.Record("minutes", minutesId), ct);
        var state = await ModuleStateAsync(features, providers, ct);
        var s = await SigSettings.LoadAsync(features, ct);
        var (ready, _) = ESignatureGateway.Readiness(s, s.Method, providers);
        var m = await db.QuerySingleAsync("""
            SELECT mi.status, v.id, v.version_no,
                   (SELECT count(*) FROM minutes.minutes_signoffs so WHERE so.minutes_version_id = v.id AND so.status = 'requested'),
                   (SELECT count(*) FROM sig.requests r WHERE r.minutes_version_id = v.id AND r.status IN ('queued','in_progress','on_hold'))
            FROM minutes.minutes mi LEFT JOIN minutes.minutes_versions v ON v.minutes_id = mi.id AND v.version_no = mi.current_version_no WHERE mi.id = @minutesId
            """, new { minutesId }, r => (Status: r.GetString(0), Version: r.Uuid("id"), No: r.IsDBNull(2) ? 0 : r.GetInt32(2), Pending: r.GetInt64(3), Live: r.GetInt64(4)), ct);
        var requests = new List<object>();
        foreach (var r in await SigData.RequestsWhereAsync(db, "r.minutes_id = @minutesId", new { minutesId }, ct))
            requests.Add(await CardDataAsync(db, r, perms, me, features, providers, false, ct));
        return Results.Ok(new
        {
            module = state, minutesStatus = m.Status, currentVersionNo = m.No,
            canStart = perms.Contains(Permissions.ESignRequest) && ready && m.Status == "pending_signatures" && m.Pending > 0 && m.Live == 0,
            startBlockedReason = m.Status != "pending_signatures" ? null : !s.Enabled ? "esign_disabled" : !ready ? "esign_not_ready" : m.Live > 0 ? "request_live" : m.Pending == 0 ? "no_pending" : null,
            requests,
        });
    }

    private static async Task<IResult> Card(Guid id, Db db, IAccessService access, IFeatureService features, ProviderRegistry providers, ICurrentUser me, CancellationToken ct)
    {
        var r = await SigData.RequestAsync(db, id, false, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Permissions.MinutesView, Scope.Record("minutes", r.MinutesId), ct);
        var perms = await access.GetPermissionsAsync(Scope.Record("minutes", r.MinutesId), ct);
        return Results.Ok(await CardDataAsync(db, r, perms, me, features, providers, true, ct));
    }

    private static async Task<object> CardDataAsync(Db db, SigRequest r, IReadOnlySet<string> perms, ICurrentUser me, IFeatureService features, ProviderRegistry providers,
        bool withEvents, CancellationToken ct)
    {
        var enabled = await features.IsEnabledAsync(FeatureKeys.ESignatureEnabled, ct);
        var signers = await SigData.SignersAsync(db, r.Id, ct);
        var provider = providers.Find(r.Provider);
        var productionGrade = r.Type == SignatureTypes.InAppApproval || (provider?.Readiness().ProductionGrade ?? false);
        var mine = signers.FirstOrDefault(s => s.UserId == me.UserId);
        var delegations = await db.QueryAsync("""
            SELECT delegator_user_id FROM iam.delegations WHERE body_id = @body AND delegate_user_id = @me AND scope = 'chair_authority' AND status = 'active'
              AND valid_from <= now() AND valid_to > now()
            """, new { body = r.BodyId, me = me.UserId }, x => x.GetGuid(0), ct);
        var onBehalf = signers.Where(s => s.Role == "chair" && s.AllowDelegation && s.Status == "pending" && delegations.Contains(s.UserId) && s.UserId != me.UserId)
            .Select(s => new { signerId = s.Id, userId = s.UserId, s.Name }).ToList();
        var live = r.Status is "in_progress" or "queued";
        var canAct = enabled && live && perms.Contains(Permissions.MinutesSign);
        var manage = perms.Contains(Permissions.ESignManage);
        var seeEvents = withEvents && (manage || perms.Contains(Permissions.ESignVerify));
        var events = seeEvents
            ? await db.QueryAsync("""
                SELECT e.kind, e.source, u.display_name_ar, e.created_at, e.data::text FROM sig.events e LEFT JOIN iam.users u ON u.id = e.actor_user_id
                WHERE e.request_id = @id ORDER BY e.created_at DESC, e.id DESC LIMIT 60
                """, new { id = r.Id }, x => (object)new { kind = x.GetString(0), source = x.GetString(1), actor = x.IsDBNull(2) ? null : x.GetString(2), at = x.GetDateTime(3),
                    data = JsonNode.Parse(x.GetString(4)) }, ct)
            : [];
        var callbacks = withEvents && manage
            ? await db.QueryAsync("""
                SELECT provider_event_id, signature_valid, outcome, detail, received_at, payload->>'event' FROM sig.provider_callbacks WHERE request_id = @id ORDER BY received_at DESC LIMIT 30
                """, new { id = r.Id }, x => (object)new { eventId = x.GetString(0), signatureValid = x.GetBoolean(1), outcome = x.GetString(2), detail = x.Str("detail"),
                    at = x.GetDateTime(4), @event = x.IsDBNull(5) ? null : x.GetString(5) }, ct)
            : [];
        return new
        {
            r.Id, r.Reference, r.MinutesId, r.VersionNo, r.ContentHash, type = r.Type, typeLabel = Labels.Type(r.Type), typeNotice = Labels.TypeNotice(r.Type, productionGrade),
            provider = r.Provider, providerName = provider?.NameAr ?? (r.Provider == "internal" ? "داخل النظام" : r.Provider), productionGrade,
            r.Status, r.StatusReason, r.Sequential, route = r.Route, settings = r.Settings, r.DocumentId, r.DocumentSha256, r.DocumentSize, r.CurrentDocumentId,
            r.CurrentDocumentSha256, r.SignedDocumentId, r.SignedDocumentSha256, r.ExpiresAt, r.CreatedAt, r.CompletedAt, dispatchAttempts = r.Attempts, r.LastError,
            r.NextAttemptAt, r.ProviderRef, verification = r.VerificationJson is null ? null : JsonNode.Parse(r.VerificationJson),
            signers = signers.Select(s =>
            {
                var ev = s.EvidenceJson is null ? null : JsonNode.Parse(s.EvidenceJson);
                return new
                {
                    s.Id, s.UserId, s.Name, s.Role, s.Step, s.Position, s.Status, s.SignedAt, actedBy = s.ActedByName, onBehalf = s.ActedBy is { } a && a != s.UserId,
                    s.DeclineReason, s.AllowDelegation, certificate = ev?["certificate"], reauth = ev?["reauth"], s.SignedDocumentSha256,
                };
            }),
            my = new
            {
                signerId = mine?.Id, myStatus = mine?.Status,
                canSign = canAct && mine is { Status: "pending" } && r.Type != SignatureTypes.ProviderESign,
                canOpenProviderSession = canAct && mine is { Status: "pending" } && r.Type == SignatureTypes.ProviderESign,
                canDecline = canAct && mine is { Status: "pending" or "waiting" },
                notYourTurn = mine is { Status: "waiting" },
                reauthRequired = r.ReauthRequired,
                onBehalf = canAct ? onBehalf : [],
            },
            manage = new
            {
                canFallback = manage && r.Live,
                canHold = manage && r.Status is "in_progress" or "queued",
                canResume = manage && enabled && r.Status == "on_hold",
                moduleDisabled = !enabled,
                actionRequired = !enabled && r.Live,
            },
            events, callbacks,
        };
    }

    private static async Task<IResult> Verify(Guid id, Db db, IAccessService access, VerificationService verification, IAuditWriter audit, CancellationToken ct)
    {
        var r = await SigData.RequestAsync(db, id, false, ct) ?? throw new NotFoundException();
        var perms = await access.GetPermissionsAsync(Scope.Record("minutes", r.MinutesId), ct);
        if (!perms.Contains(Permissions.MinutesView)) throw new NotFoundException();
        if (!perms.Contains(Permissions.ESignVerify) && !perms.Contains(Permissions.ESignManage)) throw new ForbiddenException(permission: Permissions.ESignVerify);
        var report = await verification.ReportAsync(r, await SigData.SignersAsync(db, id, ct), ct);
        await audit.WriteAsync(new AuditEntry("SIG", "esign.verified", "minutes", r.MinutesId.ToString(), After: new { requestId = id, overall = report["overall"]?.GetValue<string>() }), ct);
        return Results.Ok(report);
    }

    private static async Task<IResult> VerifyUpload(HttpRequest request, IAccessService access, VerificationService verification, ICurrentUser me, CancellationToken ct)
    {
        if (!request.HasFormContentType) throw AppValidationException.For("file", "أرفق ملف PDF");
        var form = await request.ReadFormAsync(ct);
        var file = form.Files["file"] ?? throw AppValidationException.For("file", "أرفق ملف PDF");
        if (file.Length is 0 or > 30 * 1024 * 1024) throw AppValidationException.For("file", "حجم الملف غير مقبول");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        if (bytes.Length < 5 || Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-") throw AppValidationException.For("file", "الملف ليس PDF");
        return Results.Ok(await verification.VerifyUploadAsync(bytes, ct));
    }

    private static async Task<IResult> Maintenance(MaintenanceRequest req, IAccessService access, SigningService svc, IHostEnvironment env, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsManage, Scope.System, ct);
        if (req.AsOf is not null && env.IsProduction()) throw AppValidationException.For("asOf", "تحديد التاريخ متاح في بيئات الاختبار فقط");
        return Results.Ok(await svc.MaintenanceAsync(req.AsOf ?? DateTime.UtcNow, ct));
    }
}

/// <summary>صيانة دورية: إعادة إرسال المتعثر، وانتهاء الصلاحية، وإلغاء الطلبات لدى المزوّد، وتطبيق الأحداث المؤجلة.</summary>
internal sealed class SignatureWorker(IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<SignatureOptions> options, ILogger<SignatureWorker> log) : BackgroundService
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
                await scope.ServiceProvider.GetRequiredService<SigningService>().MaintenanceAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { log.LogError(e, "فشل دورة صيانة التوقيع"); }
        }
    }
}

/// <summary>أثر تغيير إعدادات التوقيع على الطلبات الجارية: يتطلب التأكيد الصريح، ولا تتغير الطلبات آليًا.</summary>
internal sealed class ESignatureImpact(Db db) : IFeatureImpactProvider
{
    public IReadOnlyCollection<string> FeatureKeys => [Contracts.FeatureKeys.ESignatureEnabled, Contracts.FeatureKeys.ESignatureProvider, Contracts.FeatureKeys.ESignatureMethod];

    public async Task<IReadOnlyList<FeatureImpactItem>> AssessAsync(string key, string oldValue, string newValue, CancellationToken ct = default)
    {
        if (key == Contracts.FeatureKeys.ESignatureEnabled && newValue == "true") return [];
        var live = await db.ScalarAsync<long>("SELECT count(*) FROM sig.requests WHERE status IN ('queued','in_progress','on_hold')", null, ct);
        var item = key == Contracts.FeatureKeys.ESignatureEnabled
            ? new FeatureImpactItem("esign_live_requests", "طلبات توقيع جارية ستبقى كما هي وتتوقف إجراءاتها حتى إجراء صريح (تحويل للبديل أو تعليق)",
                "Live signature requests are kept unchanged and frozen until an explicit action", (int)live)
            : new FeatureImpactItem("esign_live_requests_snapshot", "طلبات جارية تحتفظ بإعداداتها وقت إنشائها؛ التغيير يسري على الطلبات الجديدة فقط",
                "Live requests keep their creation-time settings; the change applies to new requests only", (int)live);
        return [item];
    }
}
