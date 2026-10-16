using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Audit;

public sealed class AuditModule : IModule
{
    public string Code => "AUD";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IActivityQuery, ActivityQuery>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/audit").WithTags("Audit");
        g.MapGet("/events", List);
        g.MapGet("/integrity", Integrity);
    }

    private static async Task<IResult> List(string? module, string? action, string? entityType, string? entityId, Guid? actorUserId,
        DateTime? from, DateTime? to, int? page, int? pageSize, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AuditRead, Scope.System, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { module, action = SqlText.Like(action), entityType, entityId, actorUserId, from, to, limit = p.PageSize, offset = p.Offset };
        const string where = """
            WHERE (@module::text IS NULL OR a.module = @module)
              AND (@action::text IS NULL OR a.action ILIKE @action)
              AND (@entityType::text IS NULL OR a.entity_type = @entityType)
              AND (@entityId::text IS NULL OR a.entity_id = @entityId)
              AND (@actorUserId::uuid IS NULL OR a.actor_user_id = @actorUserId)
              AND (@from::timestamptz IS NULL OR a.occurred_at >= @from)
              AND (@to::timestamptz IS NULL OR a.occurred_at < @to)
            """;
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM audit.audit_events a " + "\n" + where, args, ct);
        var items = await db.QueryAsync("""
            SELECT a.chain_seq, a.occurred_at, a.actor_user_id, u.display_name_ar AS actor_name, a.on_behalf_of_user_id, a.actor_ip,
                   a.module, a.action, a.entity_type, a.entity_id, a.outcome, a.before::text AS before, a.after::text AS after, a.hash
            FROM audit.audit_events a LEFT JOIN iam.users u ON u.id = a.actor_user_id
            """ + "\n" + where + " ORDER BY a.chain_seq DESC LIMIT @limit OFFSET @offset", args, r => new
        {
            seq = r.Get<long>("chain_seq"), occurredAt = r.Get<DateTime>("occurred_at"), actorUserId = r.Uuid("actor_user_id"),
            actorName = r.Str("actor_name"), onBehalfOfUserId = r.Uuid("on_behalf_of_user_id"), ip = r.Str("actor_ip"),
            module = r.Get<string>("module"), action = r.Get<string>("action"), entityType = r.Get<string>("entity_type"),
            entityId = r.Str("entity_id"), outcome = r.Get<string>("outcome"), before = r.Str("before"), after = r.Str("after"),
            hash = r.Get<string>("hash"),
        }, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    private static async Task<IResult> Integrity(Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AuditRead, Scope.System, ct);
        var r = await db.QuerySingleAsync("SELECT checked, first_broken_seq FROM audit.verify_chain()", null,
            x => new { checkedEvents = x.GetInt64(0), firstBrokenSeq = x.IsDBNull(1) ? (long?)null : x.GetInt64(1) }, ct);
        return Results.Ok(new { r!.checkedEvents, r.firstBrokenSeq, intact = r.firstBrokenSeq is null });
    }
}

/// <summary>يكتب حدث التدقيق؛ البصمة والتسلسل يحسبهما مشغّل القاعدة (V006).</summary>
internal sealed class AuditWriter(Db db, ICurrentUser user) : IAuditWriter
{
    public Task WriteAsync(AuditEntry entry, CancellationToken ct = default) => WriteAsync(db, entry, ct);

    public Task WriteAsync(ISql sql, AuditEntry e, CancellationToken ct = default) =>
        sql.ExecuteAsync("""
            INSERT INTO audit.audit_events (actor_user_id, actor_ip, correlation_id, module, action, entity_type, entity_id, outcome, classification, before, after)
            VALUES (@actor, @ip, @corr, @Module, @Action, @EntityType, @EntityId, @Outcome, @Classification, @before::jsonb, @after::jsonb)
            """, new
        {
            actor = e.ActorUserId ?? (user.IsAuthenticated ? user.UserId : (Guid?)null),
            ip = user.IpAddress,
            corr = user.CorrelationId,
            e.Module, e.Action, e.EntityType, e.EntityId, e.Outcome, e.Classification,
            before = e.Before is null ? null : Json.Serialize(e.Before),
            after = e.After is null ? null : Json.Serialize(e.After),
        }, ct);
}

internal sealed class ActivityQuery(Db db) : IActivityQuery
{
    public async Task<IReadOnlyList<ActivityEntry>> ForEntityAsync(string entityType, Guid entityId, int limit = 200, CancellationToken ct = default) =>
        await db.QueryAsync("""
            SELECT a.chain_seq, a.occurred_at, a.actor_user_id, u.display_name_ar, a.module, a.action, a.entity_type, a.entity_id, a.outcome, a.after::text
            FROM audit.audit_events a LEFT JOIN iam.users u ON u.id = a.actor_user_id
            WHERE a.entity_type = @entityType AND a.entity_id = @id AND a.outcome = 'success'
            ORDER BY a.chain_seq DESC LIMIT @limit
            """, new { entityType, id = entityId.ToString(), limit }, r => new ActivityEntry(
            r.GetInt64(0), r.GetDateTime(1), r.Uuid("actor_user_id"), r.Str("display_name_ar"), r.GetString(4), r.GetString(5),
            r.GetString(6), r.Str("entity_id"), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9)), ct);
}
