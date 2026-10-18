using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Reporting;

/// <summary>التقارير واللوحات: قراءة فقط، وكل رقم مقيد بالجهات التي يملك المستخدم الاطلاع عليها (FR-RPT-06).</summary>
public sealed class ReportingModule : IModule
{
    public string Code => "RPT";

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddScoped<ISearchService, SearchService>();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        api.MapGet("/dashboard", Dashboard).WithTags("Reports");
        api.MapGet("/search", SearchService.Endpoint).WithTags("Search");
        var g = api.MapGroup("/reports").WithTags("Reports");
        g.MapGet("/executive", Kpis.ExecutiveEndpoint);
        g.MapGet("/kpi-definitions", () => Results.Ok(KpiCatalog.All));
        g.MapGet("/catalog", Reports.CatalogEndpoint);
        g.MapGet("/run/{code}", Reports.RunEndpoint);
        g.MapGet("/tasks-by-body", TasksByBody);
        g.MapGet("/meetings-by-month", MeetingsByMonth);
        g.MapGet("/resolutions-by-status", ResolutionsByStatus);
    }

    private static async Task<IResult> Dashboard(Db db, IAccessService access, ICurrentUser me, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        var bodies = await access.GetBodyFilterAsync(Permissions.BodyView, ct);
        var meetings = await access.GetBodyFilterAsync(Permissions.MeetingView, ct);
        var minutes = await access.GetBodyFilterAsync(Permissions.MinutesView, ct);
        var resolutions = await access.GetBodyFilterAsync(Permissions.ResolutionView, ct);
        var tasks = await access.GetBodyFilterAsync(Permissions.TaskView, ct);
        var r = await db.QuerySingleAsync("""
            SELECT
              (SELECT count(*) FROM bodies.bodies b WHERE b.status = 'active' AND (@bAll::boolean OR b.id = ANY(@bIds::uuid[]))) AS active_bodies,
              (SELECT count(*) FROM meetings.meetings m WHERE m.status IN ('scheduled','agenda_published') AND m.starts_at >= now()
                 AND (@mAll::boolean OR m.body_id = ANY(@mIds::uuid[]))) AS upcoming_meetings,
              (SELECT count(*) FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id
                 WHERE mi.status IN ('secretary_review','chair_review','member_review','pending_signatures','pending_final_approval','returned') AND (@miAll::boolean OR m.body_id = ANY(@miIds::uuid[]))) AS minutes_in_progress,
              (SELECT count(*) FROM resolutions.resolutions r WHERE r.status IN ('issued','in_execution') AND (@rAll::boolean OR r.body_id = ANY(@rIds::uuid[]))) AS open_resolutions,
              (SELECT count(*) FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status WHERE ws.category IN ('open','active','waiting','review')
                 AND (@tAll::boolean OR t.body_id = ANY(@tIds::uuid[]))) AS open_tasks,
              (SELECT count(*) FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status WHERE ws.category IN ('open','active')
                 AND t.due_date < (now() AT TIME ZONE 'Asia/Riyadh')::date AND (@tAll::boolean OR t.body_id = ANY(@tIds::uuid[]))) AS overdue_tasks,
              (SELECT count(*) FROM tasks.tasks t WHERE t.status = 'completed' AND (@tAll::boolean OR t.body_id = ANY(@tIds::uuid[]))) AS completed_tasks,
              (SELECT count(*) FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status
                 WHERE (t.assignee_user_id = @me OR t.executor_user_id = @me OR t.accountable_user_id = @me OR t.verifier_user_id = @me)
                 AND ws.category IN ('open','active','waiting','review')) AS my_open_tasks,
              (SELECT count(*) FROM minutes.minutes_signoffs s WHERE s.user_id = @me AND s.status = 'requested') AS my_pending_signoffs
            """, new
        {
            bAll = bodies.All, bIds = bodies.BodyIds, mAll = meetings.All, mIds = meetings.BodyIds, miAll = minutes.All, miIds = minutes.BodyIds,
            rAll = resolutions.All, rIds = resolutions.BodyIds, tAll = tasks.All, tIds = tasks.BodyIds, me = me.UserId,
        }, x => new
        {
            activeBodies = x.GetInt64(0), upcomingMeetings = x.GetInt64(1), minutesInProgress = x.GetInt64(2), openResolutions = x.GetInt64(3),
            openTasks = x.GetInt64(4), overdueTasks = x.GetInt64(5), completedTasks = x.GetInt64(6), myOpenTasks = x.GetInt64(7), myPendingSignoffs = x.GetInt64(8),
        }, ct);
        var upcoming = await db.QueryAsync("""
            SELECT m.id, m.reference_no, m.title_ar, m.starts_at, b.name_ar FROM meetings.meetings m JOIN bodies.bodies b ON b.id = m.body_id
            WHERE m.status IN ('scheduled','agenda_published') AND m.starts_at >= now() AND (@all::boolean OR m.body_id = ANY(@ids::uuid[]))
            ORDER BY m.starts_at LIMIT 5
            """, new { all = meetings.All, ids = meetings.BodyIds }, x => new
        {
            id = x.GetGuid(0), referenceNo = x.Str("reference_no"), titleAr = x.GetString(2), startsAt = x.GetDateTime(3), bodyName = x.GetString(4),
        }, ct);
        return Results.Ok(new { counters = r, upcomingMeetings = upcoming });
    }

    private static async Task<IResult> TasksByBody(Db db, IAccessService access, CancellationToken ct)
    {
        var f = await access.GetBodyFilterAsync(Permissions.ReportsBody, ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT b.id, b.code, b.name_ar,
              count(t.id) FILTER (WHERE ws.category NOT IN ('new','closed')) AS total,
              count(t.id) FILTER (WHERE ws.category = 'done') AS completed,
              count(t.id) FILTER (WHERE ws.category IN ('open','active','waiting','review')) AS open,
              count(t.id) FILTER (WHERE ws.category IN ('open','active') AND t.due_date < (now() AT TIME ZONE 'Asia/Riyadh')::date) AS overdue
            FROM bodies.bodies b LEFT JOIN tasks.tasks t ON t.body_id = b.id LEFT JOIN tasks.workflow_statuses ws ON ws.code = t.status
            WHERE (@all::boolean OR b.id = ANY(@ids::uuid[])) AND b.status <> 'archived'
            GROUP BY b.id, b.code, b.name_ar ORDER BY b.name_ar
            """, new { all = f.All, ids = f.BodyIds }, x => new
        {
            bodyId = x.GetGuid(0), code = x.GetString(1), nameAr = x.GetString(2), total = x.GetInt64(3), completed = x.GetInt64(4),
            open = x.GetInt64(5), overdue = x.GetInt64(6),
        }, ct));
    }

    private static async Task<IResult> MeetingsByMonth(int? year, Db db, IAccessService access, IClock clock, CancellationToken ct)
    {
        var f = await access.GetBodyFilterAsync(Permissions.ReportsBody, ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT extract(month FROM m.starts_at AT TIME ZONE 'Asia/Riyadh')::int AS month,
              count(*) FILTER (WHERE m.status NOT IN ('cancelled','postponed')) AS held_or_planned,
              count(*) FILTER (WHERE m.status = 'cancelled') AS cancelled
            FROM meetings.meetings m
            WHERE extract(year FROM m.starts_at AT TIME ZONE 'Asia/Riyadh')::int = @year AND (@all::boolean OR m.body_id = ANY(@ids::uuid[]))
            GROUP BY 1 ORDER BY 1
            """, new { year = year ?? clock.TodayLocal.Year, all = f.All, ids = f.BodyIds }, x => new
        {
            month = x.GetInt32(0), heldOrPlanned = x.GetInt64(1), cancelled = x.GetInt64(2),
        }, ct));
    }

    private static async Task<IResult> ResolutionsByStatus(Db db, IAccessService access, CancellationToken ct)
    {
        var f = await access.GetBodyFilterAsync(Permissions.ResolutionView, ct);
        return Results.Ok(await db.QueryAsync("""
            SELECT status, resolution_type, count(*) FROM resolutions.resolutions
            WHERE (@all::boolean OR body_id = ANY(@ids::uuid[])) GROUP BY 1, 2 ORDER BY 1, 2
            """, new { all = f.All, ids = f.BodyIds }, x => new { status = x.GetString(0), type = x.GetString(1), count = x.GetInt64(2) }, ct));
    }
}
