using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ccms.Modules.Meetings;

/// <summary>الدعوات والتذكير وتأكيد الحضور وتسجيل الحضور والاعتذار والغياب والإنابة، وفحوص النصاب.</summary>
internal static class Attendance
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}/invitations", List);
        g.MapPost("/{id:guid}/invitations/send", Send);
        g.MapPost("/{id:guid}/invitations/remind", Remind);
        g.MapPost("/{id:guid}/guests", AddGuest).Validate<GuestRequest>();
        g.MapPost("/{id:guid}/rsvp", Rsvp).Validate<RsvpRequest>();
        g.MapPut("/{id:guid}/attendance", Record).Validate<AttendanceRequest>();
        g.MapPost("/{id:guid}/attendance/{userId:guid}/depart", Depart);
        g.MapGet("/{id:guid}/quorum", Quorum);
        g.MapPost("/{id:guid}/quorum/check", QuorumCheck);
    }

    public sealed record GuestRequest([property: Required] Guid? UserId, Guid? AgendaItemId);
    public sealed record RsvpRequest([property: Required, OneOf("accepted", "declined", "tentative")] string Response, [property: MaxLength(500)] string? Note);
    public sealed record AttendanceLine(
        [property: Required] Guid? UserId,
        [property: Required, OneOf("present", "absent", "excused", "proxy")] string Status,
        [property: OneOf("in_person", "remote")] string? Mode,
        Guid? ProxyHolderUserId,
        [property: MaxLength(500)] string? Note);
    public sealed record AttendanceRequest([property: Required, MinLength(1)] AttendanceLine[] Records);

    private static readonly string[] OpenForInvites = ["scheduled", "agenda_published"];

    private static async Task<IResult> List(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingView, Scope.Record("meeting", id), ct);
        var rows = await db.QueryAsync("""
            WITH m AS (SELECT id, body_id, (starts_at AT TIME ZONE 'Asia/Riyadh')::date AS d FROM meetings.meetings WHERE id = @id),
            mem AS (
              SELECT DISTINCT ON (bm.user_id) bm.user_id, bm.id AS member_id, bm.role, bm.is_voting, bm.alternate_for_member_id
              FROM bodies.body_members bm, m
              WHERE bm.body_id = m.body_id AND bm.status <> 'suspended' AND bm.start_date <= m.d AND (bm.end_date IS NULL OR bm.end_date >= m.d)
              ORDER BY bm.user_id, bm.start_date DESC, bm.created_at DESC),
            people AS (
              SELECT user_id FROM mem UNION SELECT user_id FROM meetings.invitations WHERE meeting_id = @id
              UNION SELECT user_id FROM meetings.attendance_records WHERE meeting_id = @id)
            SELECT p.user_id, u.display_name_ar, u.display_name_en, u.job_title, ou.name_ar AS org_unit,
                   mem.member_id, mem.role, COALESCE(mem.is_voting, false) AS is_voting, pu.display_name_ar AS alternate_for_name,
                   i.id AS invitation_id, i.invitee_kind, i.sent_at, i.response, i.response_note, i.responded_at, i.last_reminder_at, i.agenda_item_id,
                   ai.title_ar AS agenda_item_title,
                   a.status AS attendance_status, a.attendance_mode, a.proxy_holder_user_id, ph.display_name_ar AS proxy_holder_name,
                   a.arrived_at, a.left_at, a.note AS attendance_note
            FROM people p JOIN iam.users u ON u.id = p.user_id
            LEFT JOIN org.org_units ou ON ou.id = u.org_unit_id
            LEFT JOIN mem ON mem.user_id = p.user_id
            LEFT JOIN bodies.body_members pbm ON pbm.id = mem.alternate_for_member_id LEFT JOIN iam.users pu ON pu.id = pbm.user_id
            LEFT JOIN meetings.invitations i ON i.meeting_id = @id AND i.user_id = p.user_id
            LEFT JOIN meetings.agenda_items ai ON ai.id = i.agenda_item_id
            LEFT JOIN meetings.attendance_records a ON a.meeting_id = @id AND a.user_id = p.user_id
            LEFT JOIN iam.users ph ON ph.id = a.proxy_holder_user_id
            ORDER BY CASE COALESCE(mem.role, 'guest') WHEN 'chair' THEN 0 WHEN 'vice_chair' THEN 1 WHEN 'member' THEN 2 WHEN 'alternate' THEN 3
                     WHEN 'secretary' THEN 4 WHEN 'observer' THEN 5 ELSE 6 END, u.display_name_ar
            """, new { id }, r => new
        {
            userId = r.Get<Guid>("user_id"), name = r.Get<string>("display_name_ar"), nameEn = r.Str("display_name_en"), jobTitle = r.Str("job_title"),
            orgUnit = r.Str("org_unit"), memberId = r.Uuid("member_id"), role = r.Str("role") ?? "guest", isVoting = r.Get<bool>("is_voting"),
            alternateForName = r.Str("alternate_for_name"), invitationId = r.Uuid("invitation_id"), inviteeKind = r.Str("invitee_kind"),
            sentAt = r.Time("sent_at"), response = r.Str("response"), responseNote = r.Str("response_note"), respondedAt = r.Time("responded_at"),
            lastReminderAt = r.Time("last_reminder_at"), agendaItemId = r.Uuid("agenda_item_id"), agendaItemTitle = r.Str("agenda_item_title"),
            attendanceStatus = r.Str("attendance_status"), attendanceMode = r.Str("attendance_mode"), proxyHolderUserId = r.Uuid("proxy_holder_user_id"),
            proxyHolderName = r.Str("proxy_holder_name"), arrivedAt = r.Time("arrived_at"), leftAt = r.Time("left_at"), attendanceNote = r.Str("attendance_note"),
        }, ct);
        return Results.Ok(rows);
    }

    /// <summary>إرسال الدعوات لكل الأعضاء السارية عضويتهم في تاريخ الاجتماع (بمن فيهم البدلاء والمراقبون وأمين السر) وللضيوف المضافين.
    /// إعادة الإرسال ترسل فقط لمن لم يُدعَ بعد (عضو جديد مثلًا).</summary>
    private static async Task<IResult> Send(Guid id, Db db, IAccessService access, IAuditWriter audit, INotifier notifier, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingInvite, Scope.Record("meeting", id), ct);
        var sent = await db.InTransactionAsync(async tx =>
        {
            var m = await tx.QuerySingleAsync("""
                SELECT status, title_ar, reference_no, meeting_type, starts_at, (SELECT count(*) FROM meetings.agenda_items WHERE meeting_id = @id AND status <> 'withdrawn') AS items
                FROM meetings.meetings WHERE id = @id FOR UPDATE
                """, new { id }, r => (Status: r.GetString(0), Title: r.GetString(1), Ref: r.Str("reference_no"), Type: r.GetString(3), Starts: r.GetDateTime(4), Items: r.Get<long>("items")), ct);
            if (m == default) throw new NotFoundException();
            if (!OpenForInvites.Contains(m.Status)) throw new ConflictException("invalid-transition", m.Status == "draft" ? "ثبّت موعد الاجتماع (جدولة) قبل إرسال الدعوات" : "لا تُرسل دعوات لاجتماع في حالته الحالية");
            if (m.Items == 0) throw new ConflictException("agenda-empty", "أضف بندًا واحدًا على الأقل في جدول الأعمال قبل إرسال الدعوات");
            await tx.ExecuteAsync("""
                INSERT INTO meetings.invitations (id, meeting_id, user_id, invitee_kind, body_member_id)
                SELECT gen_random_uuid(), @id, x.user_id, 'member', x.id FROM (
                  SELECT DISTINCT ON (bm.user_id) bm.user_id, bm.id FROM bodies.body_members bm JOIN meetings.meetings mt ON mt.id = @id AND mt.body_id = bm.body_id
                  WHERE bm.status = 'active' AND bm.start_date <= (mt.starts_at AT TIME ZONE 'Asia/Riyadh')::date
                    AND (bm.end_date IS NULL OR bm.end_date >= (mt.starts_at AT TIME ZONE 'Asia/Riyadh')::date)
                  ORDER BY bm.user_id, bm.start_date DESC, bm.created_at DESC) x
                ON CONFLICT (meeting_id, user_id) DO NOTHING
                """, new { id }, ct);
            var targets = await tx.QueryAsync("UPDATE meetings.invitations SET sent_at = now() WHERE meeting_id = @id AND sent_at IS NULL RETURNING user_id", new { id }, r => r.GetGuid(0), ct);
            await tx.ExecuteAsync("UPDATE meetings.meetings SET status = 'agenda_published', invitations_sent_at = COALESCE(invitations_sent_at, now()) WHERE id = @id", new { id }, ct);
            var when = TimeZoneInfo.ConvertTimeFromUtc(m.Starts, MeetingsModule.Riyadh).ToString("yyyy-MM-dd HH:mm");
            var kind = m.Type == "circulation" ? "دعوة للمشاركة بالتمرير" : "دعوة لاجتماع";
            foreach (var u in targets)
                await notifier.NotifyAsync(tx, new NotificationRequest(u, "meeting_invitation", $"{kind}: {m.Title} — {when}", $"Meeting invitation: {m.Ref} — {when}",
                    $"/meetings/{id}", $"meeting_invitation:{id}:{u}", m.Type == "urgent" ? "warning" : "info"), ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.invitations_sent", "meeting", id.ToString(), After: new { count = targets.Count }), ct);
            return targets.Count;
        }, ct);
        return Results.Ok(new { id, status = "agenda_published", sent });
    }

    private static async Task<IResult> Remind(Guid id, Db db, IAccessService access, IAuditWriter audit, INotifier notifier, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingInvite, Scope.Record("meeting", id), ct);
        var count = await db.InTransactionAsync(async tx =>
        {
            var m = await tx.QuerySingleAsync("SELECT status, title_ar, reference_no FROM meetings.meetings WHERE id = @id", new { id },
                r => (Status: r.GetString(0), Title: r.GetString(1), Ref: r.Str("reference_no")), ct);
            if (m == default) throw new NotFoundException();
            if (m.Status != "agenda_published") throw new ConflictException("invalid-transition", "التذكير متاح بعد إرسال الدعوات وقبل الانعقاد");
            var n = await RemindPendingAsync(tx, notifier, id, m.Title, m.Ref, "manual", ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.reminders_sent", "meeting", id.ToString(), After: new { count = n }), ct);
            return n;
        }, ct);
        return Results.Ok(new { reminded = count });
    }

    internal static async Task<int> RemindPendingAsync(ISql tx, INotifier notifier, Guid id, string title, string? refNo, string tag, CancellationToken ct)
    {
        var users = await tx.QueryAsync("""
            UPDATE meetings.invitations SET last_reminder_at = now()
            WHERE meeting_id = @id AND sent_at IS NOT NULL AND response IN ('pending','tentative') RETURNING user_id
            """, new { id }, r => r.GetGuid(0), ct);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmm");
        foreach (var u in users)
            await notifier.NotifyAsync(tx, new NotificationRequest(u, "meeting_reminder", $"تذكير: يرجى تأكيد حضورك — {title}", $"Reminder: please confirm attendance — {refNo}",
                $"/meetings/{id}", $"meeting_reminder:{id}:{u}:{tag}:{stamp}"), ct);
        return users.Count;
    }

    /// <summary>ضيف من خارج عضوية الجهة (مقدم بند أو مختص) — يطّلع على الاجتماع بعد إرسال دعوته فقط ولا يُحتسب في النصاب.</summary>
    private static async Task<IResult> AddGuest(Guid id, GuestRequest req, Db db, IAccessService access, IAuditWriter audit, INotifier notifier, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingInvite, Scope.Record("meeting", id), ct);
        var userId = req.UserId!.Value;
        await db.InTransactionAsync(async tx =>
        {
            var m = await tx.QuerySingleAsync("SELECT status, title_ar, reference_no, invitations_sent_at FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id },
                r => (Status: r.GetString(0), Title: r.GetString(1), Ref: r.Str("reference_no"), Sent: r.Time("invitations_sent_at")), ct);
            if (m == default) throw new NotFoundException();
            if (m.Status is not ("draft" or "scheduled" or "agenda_published")) throw new ConflictException("invalid-transition", "لا يُضاف ضيوف لاجتماع في حالته الحالية");
            var ustatus = await tx.ScalarAsync<string>("SELECT status FROM iam.users WHERE id = @userId", new { userId }, ct);
            if (ustatus != "active") throw AppValidationException.For("userId", "المستخدم غير موجود أو غير نشط");
            if (req.AgendaItemId is { } item && await tx.ScalarAsync<Guid?>("SELECT meeting_id FROM meetings.agenda_items WHERE id = @item", new { item }, ct) != id)
                throw AppValidationException.For("agendaItemId", "البند ليس في هذا الاجتماع");
            var inserted = await tx.ExecuteAsync("""
                INSERT INTO meetings.invitations (id, meeting_id, user_id, invitee_kind, agenda_item_id, sent_at)
                VALUES (@iid, @id, @userId, 'guest', @AgendaItemId, CASE WHEN @sent THEN now() END) ON CONFLICT (meeting_id, user_id) DO NOTHING
                """, new { iid = Guid.CreateVersion7(), id, userId, req.AgendaItemId, sent = m.Sent is not null }, ct);
            if (inserted == 0) throw new ConflictException("duplicate", "المستخدم مدعو مسبقًا");
            if (m.Sent is not null)
                await notifier.NotifyAsync(tx, new NotificationRequest(userId, "meeting_invitation", $"دعوة لحضور اجتماع: {m.Title}", $"Meeting invitation: {m.Ref}",
                    $"/meetings/{id}", $"meeting_invitation:{id}:{userId}"), ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.guest_invited", "meeting", id.ToString(), After: new { userId, req.AgendaItemId }), ct);
        }, ct);
        return Results.Ok(new { id, userId });
    }

    private static async Task<IResult> Rsvp(Guid id, RsvpRequest req, Db db, ICurrentUser me, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingView, Scope.Record("meeting", id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM meetings.meetings WHERE id = @id", new { id }, ct) ?? throw new NotFoundException();
            if (status != "agenda_published") throw new ConflictException("invalid-transition", "تأكيد الحضور متاح بعد الدعوة وقبل الانعقاد");
            var n = await tx.ExecuteAsync("""
                UPDATE meetings.invitations SET response = @Response, response_note = @Note, responded_at = now()
                WHERE meeting_id = @id AND user_id = @me AND sent_at IS NOT NULL
                """, new { id, me = me.UserId, req.Response, req.Note }, ct);
            if (n == 0) throw new ConflictException("not-invited", "لست مدعوًا لهذا الاجتماع");
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.rsvp", "meeting", id.ToString(), After: new { userId = me.UserId, req.Response, req.Note }), ct);
        }, ct);
        return Results.Ok(new { id, response = req.Response });
    }

    /// <summary>تسجيل الحضور (حضوري/عن بعد) أو الاعتذار أو الغياب أو الإنابة. الإنابة لا تُقبل إلا إن أجازتها سياسة الجهة ولعضو مصوت لعضو مصوت.</summary>
    private static async Task<IResult> Record(Guid id, AttendanceRequest req, Db db, IAccessService access, IAuditWriter audit, QuorumService quorum, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingAttendance, Scope.Record("meeting", id), ct);
        var result = await db.InTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync("SELECT 1 FROM meetings.meetings WHERE id = @id FOR UPDATE", new { id }, ct);
            var c = await quorum.LoadContextAsync(tx, id, ct);
            if (c.Status is not ("agenda_published" or "in_progress")) throw new ConflictException("invalid-transition", "يُسجل الحضور بعد إرسال الدعوات وحتى إغلاق الاجتماع");
            if (c.MeetingType == "circulation") throw new ConflictException("invalid-transition", "الاجتماع بالتمرير لا يُسجل له حضور؛ يُعتد بالردود");
            var members = (await quorum.MembersAsync(tx, c.BodyId, c.MeetingDate, ct)).ToDictionary(m => m.UserId);
            var invited = (await tx.QueryAsync("SELECT user_id FROM meetings.invitations WHERE meeting_id = @id AND sent_at IS NOT NULL", new { id }, r => r.GetGuid(0), ct)).ToHashSet();
            var errors = new Dictionary<string, string[]>();
            var proxyCount = new Dictionary<Guid, int>();
            for (var i = 0; i < req.Records.Length; i++)
            {
                var l = req.Records[i];
                var u = l.UserId!.Value;
                string? err = null;
                members.TryGetValue(u, out var mem);
                if (!invited.Contains(u) && mem is null) err = "المستخدم ليس عضوًا ولا مدعوًا";
                else if (l.Status == "present" && l.Mode is null) err = "حدد طريقة الحضور";
                else if (l.Status == "present" && l.Mode == "remote" && !c.Policy.AllowRemoteAttendance) err = "سياسة الجهة لا تجيز الحضور عن بعد";
                else if (l.Status == "proxy")
                {
                    var holder = l.ProxyHolderUserId is { } h && members.TryGetValue(h, out var hm) ? hm : null;
                    if (!c.Policy.AllowProxy) err = "سياسة الجهة لا تجيز الإنابة";
                    else if (mem is null || !mem.IsVoting || mem.Role is "alternate") err = "الإنابة للأعضاء المصوتين الأصلاء فقط";
                    else if (holder is null || !holder.IsVoting || holder.Role is "alternate" || holder.UserId == u) err = "المُناب يجب أن يكون عضوًا مصوتًا آخر في الجهة";
                    else if ((proxyCount[holder.UserId] = proxyCount.GetValueOrDefault(holder.UserId) + 1) > c.Policy.MaxProxiesPerMember)
                        err = $"تجاوز الحد الأقصى للإنابات لكل عضو ({c.Policy.MaxProxiesPerMember})";
                }
                if (l.Status != "proxy" && l.ProxyHolderUserId is not null) err = "المُناب يُحدد لحالة الإنابة فقط";
                if (err is not null) errors[$"records[{i}]"] = [err];
            }
            if (errors.Count > 0) throw new AppValidationException(errors);
            foreach (var l in req.Records)
            {
                var u = l.UserId!.Value;
                await tx.ExecuteAsync("""
                    INSERT INTO meetings.attendance_records (id, meeting_id, user_id, body_member_id, status, attendance_mode, proxy_holder_user_id, note, arrived_at, recorded_by)
                    VALUES (@rid, @id, @u, @mid, @Status, @mode, @ProxyHolderUserId, @Note, CASE WHEN @Status = 'present' THEN now() END, @by)
                    ON CONFLICT (meeting_id, user_id) DO UPDATE SET status = EXCLUDED.status, attendance_mode = EXCLUDED.attendance_mode,
                      proxy_holder_user_id = EXCLUDED.proxy_holder_user_id, note = EXCLUDED.note,
                      arrived_at = CASE WHEN EXCLUDED.status = 'present' THEN COALESCE(meetings.attendance_records.arrived_at, now()) END,
                      left_at = CASE WHEN EXCLUDED.status = 'present' THEN NULL ELSE meetings.attendance_records.left_at END,
                      recorded_by = EXCLUDED.recorded_by, recorded_at = now()
                    """, new
                {
                    rid = Guid.CreateVersion7(), id, u, mid = members.TryGetValue(u, out var mm) ? mm.MemberId : (Guid?)null, l.Status,
                    mode = l.Status == "present" ? l.Mode : null, l.ProxyHolderUserId, l.Note, by = me.UserId,
                }, ct);
            }
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.attendance_recorded", "meeting", id.ToString(),
                After: req.Records.Select(l => new { l.UserId, l.Status, l.Mode, l.ProxyHolderUserId })), ct);
            return await quorum.ComputeAsync(tx, c, "actual", ct);
        }, ct);
        return Results.Ok(new { quorum = result });
    }

    /// <summary>مغادرة عضو أثناء الاجتماع: يُعاد فحص النصاب ويُسجل؛ فقدانه يُعاد للواجهة للتنبيه (08 §5).</summary>
    private static async Task<IResult> Depart(Guid id, Guid userId, Db db, IAccessService access, IAuditWriter audit, QuorumService quorum, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingAttendance, Scope.Record("meeting", id), ct);
        var q = await db.InTransactionAsync(async tx =>
        {
            var c = await quorum.LoadContextAsync(tx, id, ct);
            if (c.Status != "in_progress") throw new ConflictException("invalid-transition", "تُسجل المغادرة أثناء انعقاد الاجتماع");
            var n = await tx.ExecuteAsync("UPDATE meetings.attendance_records SET left_at = now() WHERE meeting_id = @id AND user_id = @userId AND status = 'present' AND left_at IS NULL",
                new { id, userId }, ct);
            if (n == 0) throw new ConflictException("not-present", "العضو غير مسجل حاضرًا");
            var r = await quorum.ComputeAsync(tx, c, "actual", ct);
            await quorum.RecordAsync(tx, c, "departure", r, null, me.UserId, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "meeting.member_departed", "meeting", id.ToString(), After: new { userId, quorumMet = r.IsMet, r.Counted, r.Required }), ct);
            return r;
        }, ct);
        return Results.Ok(new { quorum = q, quorumLost = !q.IsMet });
    }

    private static async Task<IResult> Quorum(Guid id, string? mode, Db db, IAccessService access, QuorumService quorum, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingView, Scope.Record("meeting", id), ct);
        var c = await quorum.LoadContextAsync(db, id, ct);
        var recorded = await db.ScalarAsync<long>("SELECT count(*) FROM meetings.attendance_records WHERE meeting_id = @id", new { id }, ct) > 0;
        // قبل الانعقاد: التوقع من ردود الدعوات، وما إن يبدأ تسجيل الحضور يُعتد بالحضور الفعلي
        var effective = mode ?? (c.MeetingType == "circulation" ? "circulation"
            : recorded || c.Status is "in_progress" or "concluded" or "closed" or "quorum_failed" ? "actual" : "forecast");
        if (effective is not ("actual" or "forecast" or "circulation")) throw AppValidationException.For("mode", "قيمة غير صالحة");
        var result = await quorum.ComputeAsync(db, c, effective, ct);
        var history = await db.QueryAsync("""
            SELECT q.id, q.context, q.eligible_count, q.counted_present, q.required_count, q.chair_present, q.is_met, q.override_reason, q.checked_at, u.display_name_ar
            FROM meetings.quorum_checks q LEFT JOIN iam.users u ON u.id = q.checked_by WHERE q.meeting_id = @id ORDER BY q.checked_at DESC
            """, new { id }, r => new
        {
            id = r.Get<Guid>("id"), context = r.Get<string>("context"), eligible = r.Get<int>("eligible_count"), counted = r.Get<int>("counted_present"),
            required = r.Get<int>("required_count"), chairPresent = r.Get<bool>("chair_present"), isMet = r.Get<bool>("is_met"),
            overrideReason = r.Str("override_reason"), checkedAt = r.Get<DateTime>("checked_at"), checkedBy = r.Str("display_name_ar"),
        }, ct);
        return Results.Ok(new
        {
            mode = effective, result,
            policy = new { c.Policy.Version, c.Policy.Quorum, c.Policy.AllowProxy, c.Policy.MaxProxiesPerMember, c.Policy.AllowRemoteAttendance, c.Policy.AllowCirculation },
            history,
        });
    }

    private static async Task<IResult> QuorumCheck(Guid id, Db db, IAccessService access, QuorumService quorum, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingAttendance, Scope.Record("meeting", id), ct);
        var r = await db.InTransactionAsync(async tx =>
        {
            var c = await quorum.LoadContextAsync(tx, id, ct);
            var mode = c.MeetingType == "circulation" ? "circulation" : c.Status is "draft" or "scheduled" or "agenda_published" ? "forecast" : "actual";
            var q = await quorum.ComputeAsync(tx, c, mode, ct);
            await quorum.RecordAsync(tx, c, mode == "forecast" ? "forecast" : mode == "circulation" ? "circulation" : "manual", q, null, me.UserId, ct);
            return q;
        }, ct);
        return Results.Ok(r);
    }
}

/// <summary>تذكير آلي لمن لم يؤكد حضوره قبل 24 ساعة من الاجتماع (مرة واحدة لكل مدعو).</summary>
internal sealed class MeetingReminderWorker(IServiceScopeFactory scopes, ILogger<MeetingReminderWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Db>();
                var notifier = scope.ServiceProvider.GetRequiredService<INotifier>();
                var due = await db.QueryAsync("""
                    SELECT DISTINCT m.id, m.title_ar, m.reference_no FROM meetings.meetings m JOIN meetings.invitations i ON i.meeting_id = m.id
                    WHERE m.status = 'agenda_published' AND m.starts_at BETWEEN now() AND now() + interval '24 hours'
                      AND i.sent_at IS NOT NULL AND i.response = 'pending' AND i.last_reminder_at IS NULL
                    """, null, r => (Id: r.GetGuid(0), Title: r.GetString(1), Ref: r.Str("reference_no")), stoppingToken);
                foreach (var m in due)
                    await db.InTransactionAsync(tx => Attendance.RemindPendingAsync(tx, notifier, m.Id, m.Title, m.Ref, "auto", stoppingToken), stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "فشل التذكير الآلي بالاجتماعات");
            }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken).ContinueWith(_ => { });
        }
    }
}
