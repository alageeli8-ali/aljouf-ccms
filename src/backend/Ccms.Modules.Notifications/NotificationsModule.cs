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

namespace Ccms.Modules.Notifications;

public sealed class NotificationsModule : IModule
{
    public string Code => "NTF";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<INotifier, Notifier>();
        services.AddScoped<OutboxDispatcher>();
        services.AddScoped<IFeatureImpactProvider, EmailImpact>();
        if (configuration.GetValue("Workers:Enabled", true))
            services.AddHostedService<OutboxWorker>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/notifications").WithTags("Notifications");
        g.MapGet("/", List);
        g.MapGet("/unread-count", Unread);
        g.MapPost("/{id:guid}/read", Read);
        g.MapPost("/read-all", ReadAll);
        api.MapGet("/admin/outbox", Outbox).WithTags("Notifications");
        api.MapPost("/admin/outbox/dispatch", Dispatch).WithTags("Notifications");
    }

    private static async Task<IResult> List(bool? unreadOnly, int? page, int? pageSize, Db db, ICurrentUser me, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        var p = PageQuery.From(page, pageSize);
        var args = new { uid = me.UserId, unread = unreadOnly ?? false, limit = p.PageSize, offset = p.Offset };
        const string where = "WHERE user_id = @uid AND (NOT @unread::boolean OR NOT is_read)";
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM notify.in_app_notifications " + "\n" + where, args, ct);
        var items = await db.QueryAsync("SELECT id, event_type, title_ar, title_en, link, severity, is_read, created_at FROM notify.in_app_notifications "
            + "\n" + where + " ORDER BY created_at DESC LIMIT @limit OFFSET @offset", args, r => new
        {
            id = r.GetGuid(0), eventType = r.GetString(1), titleAr = r.GetString(2), titleEn = r.GetString(3), link = r.Str("link"),
            severity = r.GetString(5), isRead = r.GetBoolean(6), createdAt = r.GetDateTime(7),
        }, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    private static async Task<IResult> Unread(Db db, ICurrentUser me, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        return Results.Ok(new { count = await db.ScalarAsync<long>("SELECT count(*) FROM notify.in_app_notifications WHERE user_id = @uid AND NOT is_read", new { uid = me.UserId }, ct) });
    }

    private static async Task<IResult> Read(Guid id, Db db, ICurrentUser me, CancellationToken ct)
    {
        var n = await db.ExecuteAsync("UPDATE notify.in_app_notifications SET is_read = true, read_at = now() WHERE id = @id AND user_id = @uid",
            new { id, uid = me.UserId }, ct);
        return n == 0 ? throw new NotFoundException() : Results.NoContent();
    }

    private static async Task<IResult> ReadAll(Db db, ICurrentUser me, CancellationToken ct)
    {
        var n = await db.ExecuteAsync("UPDATE notify.in_app_notifications SET is_read = true, read_at = now() WHERE user_id = @uid AND NOT is_read",
            new { uid = me.UserId }, ct);
        return Results.Ok(new { updated = n });
    }

    private static async Task<IResult> Outbox(string? status, int? page, int? pageSize, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsView, Scope.System, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { status, limit = p.PageSize, offset = p.Offset };
        const string where = "WHERE (@status::text IS NULL OR status = @status)";
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM notify.outbox_messages " + "\n" + where, args, ct);
        var items = await db.QueryAsync("SELECT id, event_type, channel, recipient_address, subject, status, attempts, last_error, created_at, sent_at FROM notify.outbox_messages "
            + "\n" + where + " ORDER BY created_at DESC LIMIT @limit OFFSET @offset", args, r => new
        {
            id = r.GetGuid(0), eventType = r.GetString(1), channel = r.GetString(2), recipient = r.GetString(3), subject = r.GetString(4),
            status = r.GetString(5), attempts = r.GetInt32(6), lastError = r.Str("last_error"), createdAt = r.GetDateTime(8), sentAt = r.Time("sent_at"),
        }, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    private static async Task<IResult> Dispatch(OutboxDispatcher dispatcher, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsManage, Scope.System, ct);
        return Results.Ok(await dispatcher.RunOnceAsync(ct));
    }
}

/// <summary>إشعار داخلي فوري + رسالة بريد في صندوق الصادر داخل معاملة العمل نفسها (03 §5.3).</summary>
internal sealed class Notifier : INotifier
{
    public async Task NotifyAsync(ISql sql, NotificationRequest n, CancellationToken ct = default)
    {
        await sql.ExecuteAsync("""
            INSERT INTO notify.in_app_notifications (id, user_id, event_type, title_ar, title_en, link, severity, dedupe_key)
            VALUES (@id, @UserId, @EventType, @TitleAr, @TitleEn, @Link, @Severity, @DedupeKey)
            ON CONFLICT (dedupe_key) DO NOTHING
            """, new { id = Guid.CreateVersion7(), n.UserId, n.EventType, n.TitleAr, n.TitleEn, n.Link, n.Severity, n.DedupeKey }, ct);
        await sql.ExecuteAsync("""
            INSERT INTO notify.outbox_messages (id, event_type, channel, recipient_user_id, recipient_address, subject, payload, dedupe_key)
            SELECT @id, @EventType, 'email', u.id, u.email, @TitleAr, @payload::jsonb, @dedupe
            FROM iam.users u WHERE u.id = @UserId AND u.status = 'active'
            ON CONFLICT (dedupe_key) DO NOTHING
            """, new { id = Guid.CreateVersion7(), n.EventType, n.UserId, n.TitleAr, payload = Json.Serialize(new { n.TitleAr, n.TitleEn, n.Link }), dedupe = "email:" + n.DedupeKey }, ct);
    }
}

public sealed record DispatchResult(int Sent, int Logged, int Failed, int Deferred);

/// <summary>يرسل الصادر حين يكون تكامل البريد مفعلًا والمزوّد متاحًا؛ وإلا يعلّم الرسائل «مسجّلة» (معطل) أو يؤجلها (مزوّد غير متاح).</summary>
internal sealed class OutboxDispatcher(Db db, IFeatureService features, IConnectorStatusStore connectors, IServiceProvider sp, ILogger<OutboxDispatcher> log)
{
    private const int MaxAttempts = 8;

    public async Task<DispatchResult> RunOnceAsync(CancellationToken ct)
    {
        if (!await features.IsEnabledAsync(FeatureKeys.EmailIntegrationEnabled, ct))
        {
            var logged = await db.ExecuteAsync("UPDATE notify.outbox_messages SET status = 'logged' WHERE status = 'pending' AND channel = 'email'", null, ct);
            return new DispatchResult(0, logged, 0, 0);
        }
        var sender = sp.GetService<IEmailSender>();
        var available = false;
        if (sender is not null)
        {
            try { await connectors.DemandAvailableAsync(sender.ConnectorCode, ct); available = true; }
            catch (ProviderUnavailableException) { }
        }
        var batch = await db.QueryAsync("""
            SELECT id, recipient_address, subject, payload::text, attempts FROM notify.outbox_messages
            WHERE status = 'pending' AND channel = 'email' AND next_attempt_at <= now() ORDER BY created_at LIMIT 50
            """, null, r => (Id: r.GetGuid(0), To: r.GetString(1), Subject: r.GetString(2), Payload: r.GetString(3), Attempts: r.GetInt32(4)), ct);
        int sent = 0, failed = 0, deferred = 0;
        foreach (var m in batch)
        {
            try
            {
                if (!available) throw new InvalidOperationException("مزوّد البريد غير متاح");
                await sender!.SendAsync(m.To, m.Subject, $"<div dir=\"rtl\">{System.Net.WebUtility.HtmlEncode(m.Subject)}</div>", ct);
                await db.ExecuteAsync("UPDATE notify.outbox_messages SET status = 'sent', sent_at = now(), attempts = attempts + 1 WHERE id = @id", new { id = m.Id }, ct);
                sent++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var attempts = m.Attempts + 1;
                var final = attempts >= MaxAttempts;
                await db.ExecuteAsync("""
                    UPDATE notify.outbox_messages SET attempts = @attempts, last_error = @err, status = CASE WHEN @final::boolean THEN 'failed' ELSE 'pending' END,
                      next_attempt_at = now() + make_interval(secs => @delay)
                    WHERE id = @id
                    """, new { id = m.Id, attempts, err = e.Message, final, delay = Math.Min(30 * Math.Pow(2, attempts), 3600) }, ct);
                if (final) failed++; else deferred++;
                log.LogWarning("تعذر إرسال الرسالة {Id}: {Error}", m.Id, e.Message);
            }
        }
        return new DispatchResult(sent, 0, failed, deferred);
    }
}

internal sealed class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "فشل توزيع صندوق الصادر");
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ContinueWith(_ => { });
        }
    }
}

internal sealed class EmailImpact(Db db) : IFeatureImpactProvider
{
    public IReadOnlyCollection<string> FeatureKeys => [Contracts.FeatureKeys.EmailIntegrationEnabled];

    public async Task<IReadOnlyList<FeatureImpactItem>> AssessAsync(string key, string oldValue, string newValue, CancellationToken ct = default)
    {
        if (newValue != "false") return [];
        var pending = await db.ScalarAsync<long>("SELECT count(*) FROM notify.outbox_messages WHERE status = 'pending' AND channel = 'email'", null, ct);
        return [new FeatureImpactItem("pending_emails", "رسائل بريد معلقة ستُسجَّل في الصادر دون إرسال", "Pending emails will be logged without sending", (int)pending)];
    }
}
