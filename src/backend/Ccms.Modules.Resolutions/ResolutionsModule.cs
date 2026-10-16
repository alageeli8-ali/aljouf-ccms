using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Resolutions;

public sealed class ResolutionsModule : IModule
{
    public string Code => "RES";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
        => services.AddScoped<IRecordBodyResolver, ResolutionBodyResolver>();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/resolutions").WithTags("Resolutions");
        g.MapGet("/", List);
        g.MapGet("/{id:guid}", Get);
        api.MapPost("/minutes/{minutesId:guid}/resolutions", Propose).WithTags("Resolutions").Validate<ProposeRequest>();
    }

    public sealed record ProposeRequest(
        [property: Required, OneOf("decision", "recommendation")] string ResolutionType,
        [property: Required, MaxLength(300)] string TitleAr,
        [property: Required, MaxLength(10000)] string TextAr,
        [property: MaxLength(10000)] string? TextEn,
        bool? RequiresFollowUp);

    private const string Select = """
        SELECT r.*, b.name_ar AS body_name, m.reference_no AS meeting_reference,
               (SELECT count(*) FROM tasks.tasks t WHERE t.resolution_id = r.id AND t.status <> 'cancelled') AS task_count
        FROM resolutions.resolutions r JOIN bodies.bodies b ON b.id = r.body_id LEFT JOIN meetings.meetings m ON m.id = r.meeting_id
        """;

    private static object Map(System.Data.Common.DbDataReader r) => new
    {
        id = r.Get<Guid>("id"), bodyId = r.Get<Guid>("body_id"), bodyName = r.Get<string>("body_name"), minutesId = r.Uuid("minutes_id"),
        meetingId = r.Uuid("meeting_id"), meetingReference = r.Str("meeting_reference"), resolutionType = r.Get<string>("resolution_type"),
        number = r.Str("number"), titleAr = r.Get<string>("title_ar"), textAr = r.Get<string>("text_ar"), textEn = r.Str("text_en"),
        status = r.Get<string>("status"), issuedAt = r.Time("issued_at"), requiresFollowUp = r.Get<bool>("requires_follow_up"),
        source = r.Get<string>("source"), createdAt = r.Get<DateTime>("created_at"), taskCount = r.Get<long>("task_count"),
    };

    private static async Task<IResult> List(Guid? bodyId, Guid? minutesId, string? status, string? type, string? q, int? page, int? pageSize, Db db, IAccessService access, CancellationToken ct)
    {
        var filter = await access.GetBodyFilterAsync(Permissions.ResolutionView, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { all = filter.All, ids = filter.BodyIds, bodyId, minutesId, status, type, q = SqlText.Like(q), limit = p.PageSize, offset = p.Offset };
        const string where = """
            WHERE (@all::boolean OR r.body_id = ANY(@ids::uuid[])) AND (@bodyId::uuid IS NULL OR r.body_id = @bodyId)
              AND (@minutesId::uuid IS NULL OR r.minutes_id = @minutesId)
              AND (@status::text IS NULL OR r.status = @status) AND (@type::text IS NULL OR r.resolution_type = @type)
              AND (@q::text IS NULL OR r.title_ar ILIKE @q OR r.text_ar ILIKE @q OR r.number ILIKE @q)
            """;
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM resolutions.resolutions r " + "\n" + where, args, ct);
        var items = await db.QueryAsync(Select + "\n" + where + " ORDER BY r.created_at DESC LIMIT @limit OFFSET @offset", args, Map, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    private static async Task<IResult> Get(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.ResolutionView, Scope.Record("resolution", id), ct);
        return Results.Ok(await db.QuerySingleAsync(Select + " WHERE r.id = @id", new { id }, Map, ct) ?? throw new NotFoundException());
    }

    /// <summary>قرار مقترح ضمن مسودة المحضر؛ لا يُرقّم ولا يُصدر إلا باعتماد المحضر (BRL-08).</summary>
    private static async Task<IResult> Propose(Guid minutesId, ProposeRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.ResolutionManage, Scope.Record("minutes", minutesId), ct);
        var id = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var m = await tx.QuerySingleAsync("""
                SELECT mi.status, mt.id AS meeting_id, mt.body_id, mi.classification FROM minutes.minutes mi JOIN meetings.meetings mt ON mt.id = mi.meeting_id
                WHERE mi.id = @minutesId FOR UPDATE OF mi
                """, new { minutesId }, r => (Status: r.GetString(0), MeetingId: r.GetGuid(1), BodyId: r.GetGuid(2), Cls: r.GetString(3)), ct);
            if (m == default) throw new NotFoundException();
            if (m.Status != "draft") throw new ConflictException("invalid-transition", "تُضاف القرارات المقترحة إلى مسودة المحضر فقط");
            await tx.ExecuteAsync("""
                INSERT INTO resolutions.resolutions (id, body_id, minutes_id, meeting_id, resolution_type, title_ar, text_ar, text_en, requires_follow_up, classification, created_by)
                VALUES (@id, @bodyId, @minutesId, @meetingId, @ResolutionType, @TitleAr, @TextAr, @TextEn, @follow, @cls, @by)
                """, new { id, bodyId = m.BodyId, minutesId, meetingId = m.MeetingId, req.ResolutionType, req.TitleAr, req.TextAr, req.TextEn,
                follow = req.RequiresFollowUp ?? true, cls = m.Cls, by = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("RES", "resolution.proposed", "resolution", id.ToString(), After: req), ct);
        }, ct);
        return Results.Created($"/api/v1/resolutions/{id}", await db.QuerySingleAsync(Select + " WHERE r.id = @id", new { id }, Map, ct));
    }
}

internal sealed class ResolutionBodyResolver(Db db) : IRecordBodyResolver
{
    public string RecordType => "resolution";
    public Task<Guid?> ResolveBodyAsync(Guid recordId, CancellationToken ct = default) =>
        db.ScalarAsync<Guid?>("SELECT body_id FROM resolutions.resolutions WHERE id = @recordId", new { recordId }, ct);
}
