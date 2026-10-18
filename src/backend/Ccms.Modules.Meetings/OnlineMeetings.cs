using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Meetings;

/// <summary>إنشاء رابط اجتماع مرئي عبر منصة معتمدة (23 §4.3). اختياري تمامًا: الاجتماع يُدار دونه ويمكن إدخال الرابط يدويًا.
/// معرّف الاجتماع هو مفتاح عدم التكرار، فإعادة الطلب تُرجع الاجتماع نفسه لدى المنصة.</summary>
internal static class OnlineMeetings
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/meetings/{id:guid}/online-meeting", Create).WithTags("Meetings");

    private static async Task<IResult> Create(Guid id, Db db, IAccessService access, IFeatureService features, IAuditWriter audit, IServiceProvider sp, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingManage, Scope.Record("meeting", id), ct);
        await features.DemandEnabledAsync(FeatureKeys.VideoMeetingsEnabled, ct);
        var m = await db.QuerySingleAsync("""
            SELECT title_ar, starts_at, coalesce(planned_end_at, starts_at + interval '2 hours'), mode, status, online_link FROM meetings.meetings WHERE id = @id
            """, new { id }, r => (Title: r.GetString(0), Starts: r.GetDateTime(1), Ends: r.GetDateTime(2), Mode: r.GetString(3), Status: r.GetString(4), Link: r.Str("online_link")), ct);
        if (m == default) throw new NotFoundException();
        if (m.Mode == "in_person") throw new ConflictException("in-person-meeting", "الاجتماع حضوري؛ غيّر نمطه إلى عن بعد أو مدمج أولًا");
        if (m.Status is "closed" or "cancelled" or "concluded" or "quorum_failed") throw new ConflictException("invalid-state", "لا يُنشأ رابط لاجتماع منتهٍ أو ملغى");
        var gateway = sp.GetService<IVideoMeetingGateway>() ?? throw new ConflictException("integration-unavailable", "وحدة التكاملات غير مثبتة");
        OnlineMeeting online;
        try
        {
            online = await gateway.CreateAsync(m.Title, m.Starts, m.Ends, $"ccms-meeting-{id:N}", ct);
        }
        catch (IntegrationFailedException e)
        {
            await audit.WriteAsync(new AuditEntry("MTG", "meeting.online_link_failed", "meeting", id.ToString(), After: new { e.Connector, e.Reason }, Outcome: "failure"), ct);
            throw new ConflictException("integration-failed", "تعذر إنشاء رابط الاجتماع المرئي؛ الاجتماع لم يتأثر ويمكن إدخال الرابط يدويًا",
                new Dictionary<string, object?> { ["connector"] = e.Connector, ["reason"] = e.Reason });
        }
        await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("UPDATE meetings.meetings SET online_link = @link WHERE id = @id", new { link = online.JoinUrl, id }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.online_link_created", "meeting", id.ToString(), Before: new { onlineLink = m.Link },
                After: new { online.Provider, online.ExternalId }), ct);
        }, ct);
        return Results.Ok(new { onlineLink = online.JoinUrl, provider = online.Provider });
    }
}
