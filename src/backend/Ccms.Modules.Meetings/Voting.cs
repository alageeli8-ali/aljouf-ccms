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

public sealed record Tally(int For, int Against, int Abstain, int Present, int Members, string? ChairChoice);

public sealed record VoteDecision(string Result, bool ChairCasting, string Basis);

/// <summary>حساب نتيجة التصويت وفق قاعدة الجهة (08 §6). دالة صرفة؛ حالات الاختبار المرجعية V1–V8.
/// P = المؤهلون للتصويت الحاضرون عند الإغلاق (أو من صوتوا في التمرير)، N = الأعضاء المصوتون الأصلاء.</summary>
public static class VotingCalculator
{
    public static VoteDecision Decide(VotingRule rule, Tally t)
    {
        int forV = t.For, against = t.Against;
        switch (rule.Threshold)
        {
            case "majority_of_present":
                return new(forV > t.Present / 2 ? "adopted" : "rejected", false, $"for>{t.Present / 2}");
            case "absolute_majority_of_members":
                return new(forV > t.Members / 2 ? "adopted" : "rejected", false, $"for>{t.Members / 2}");
            case "two_thirds_of_present":
                var need = (int)Math.Ceiling(2.0 * t.Present / 3 - 1e-9);
                return new(forV >= need ? "adopted" : "rejected", false, $"for>={need}");
            case "unanimity":
                return new(forV > 0 && against == 0 && t.Abstain == 0 ? "adopted" : "rejected", false, "unanimity");
            default:
                if (rule.Abstentions == "count_as_against") against += t.Abstain;
                if (forV > against) return new("adopted", false, "for>against");
                if (forV < against) return new("rejected", false, "for<against");
                return rule.TieBreaker switch
                {
                    "chair_casting_vote" when t.ChairChoice == "for" => new("adopted", true, "tie:chair_casting"),
                    "chair_casting_vote" when t.ChairChoice == "against" => new("rejected", true, "tie:chair_casting"),
                    "motion_fails" => new("rejected", false, "tie:motion_fails"),
                    _ => new("tied_unresolved", false, "tie:" + rule.TieBreaker),
                };
        }
    }
}

/// <summary>المؤهل للتصويت عن أصيل: الأصيل نفسه، أو حامل إنابته، أو بديله الحاضر.</summary>
public sealed record EligibleVoter(Guid PrincipalUserId, string Name, string Role, Guid CastByUserId, string Capacity);

/// <summary>التصويت: فتح المقترح على قرار مقترح، والأهلية وقت التصويت، ومنع التكرار، والسري ورفع الأيدي والتوافق والتمرير، والتحفظات.</summary>
internal static class Voting
{
    public static void Map(IEndpointRouteBuilder api, RouteGroupBuilder meetings)
    {
        meetings.MapGet("/{id:guid}/motions", List);
        meetings.MapPost("/{id:guid}/motions", Open).Validate<OpenMotionRequest>().Idempotent();
        var m = api.MapGroup("/motions").WithTags("Voting");
        m.MapPost("/{mid:guid}/votes", Cast).Validate<CastRequest>().Idempotent();
        m.MapPost("/{mid:guid}/record", RecordHands).Validate<RecordRequest>();
        m.MapPost("/{mid:guid}/close", Close);
        m.MapPost("/{mid:guid}/withdraw", Withdraw).Validate<MeetingsModule.ReasonRequest>();
    }

    public sealed record OpenMotionRequest(
        [property: Required] Guid? ResolutionId,
        [property: OneOf("consensus", "show_of_hands", "electronic", "secret", "circulation")] string? Method,
        DateTime? DeadlineAt);

    public sealed record CastRequest(
        [property: Required, OneOf("for", "against", "abstain")] string Choice,
        Guid? PrincipalUserId,
        [property: MaxLength(2000)] string? Reservation);

    public sealed record HandVote([property: Required] Guid? PrincipalUserId, [property: Required, OneOf("for", "against", "abstain")] string Choice);

    public sealed record RecordRequest([property: Required, MinLength(1), MaxLength(200)] List<HandVote> Votes) : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Votes.Select(v => v.PrincipalUserId).Distinct().Count() != Votes.Count) yield return new("صوت مكرر للعضو نفسه", [nameof(Votes)]);
        }
    }

    // ---------------- الأهلية ----------------

    public static List<EligibleVoter> Eligible(BodyPolicy policy, IReadOnlyList<QuorumMember> members, IReadOnlyDictionary<Guid, QuorumAttendance> attendance, bool circulation)
    {
        var principals = members.Where(m => m.IsVoting && m.Role is "chair" or "vice_chair" or "member").ToList();
        if (circulation) return principals.Select(p => new EligibleVoter(p.UserId, p.Name, p.Role, p.UserId, "self")).ToList();
        bool Present(Guid u) => attendance.TryGetValue(u, out var a) && a.Status == "present" && a.LeftAt is null
            && (a.Mode != "remote" || policy.AllowRemoteAttendance);
        var result = new List<EligibleVoter>();
        var load = new Dictionary<Guid, int>();
        foreach (var p in principals)
        {
            if (Present(p.UserId)) { result.Add(new(p.UserId, p.Name, p.Role, p.UserId, "self")); continue; }
            if (policy.AllowProxy && attendance.TryGetValue(p.UserId, out var a) && a.Status == "proxy" && a.ProxyHolderUserId is { } h
                && principals.Any(x => x.UserId == h) && Present(h) && load.GetValueOrDefault(h) < policy.MaxProxiesPerMember)
            {
                load[h] = load.GetValueOrDefault(h) + 1;
                result.Add(new(p.UserId, p.Name, p.Role, h, "proxy"));
                continue;
            }
            var alt = members.FirstOrDefault(m => m.Role == "alternate" && m.AlternateForMemberId == p.MemberId && Present(m.UserId));
            if (alt is not null) result.Add(new(p.UserId, p.Name, p.Role, alt.UserId, "alternate"));
        }
        return result;
    }

    private static async Task<(QuorumService.Context Ctx, List<EligibleVoter> Voters, QuorumResult? Quorum, List<QuorumMember> Members)> EligibilityAsync(
        ISql sql, QuorumService quorum, Guid meetingId, CancellationToken ct)
    {
        var c = await quorum.LoadContextAsync(sql, meetingId, ct);
        var members = await quorum.MembersAsync(sql, c.BodyId, c.MeetingDate, ct);
        var circulation = c.MeetingType == "circulation";
        var attendance = circulation ? new Dictionary<Guid, QuorumAttendance>() :
            (await sql.QueryAsync("SELECT user_id, status, attendance_mode, proxy_holder_user_id, left_at FROM meetings.attendance_records WHERE meeting_id = @meetingId",
                new { meetingId }, r => new QuorumAttendance(r.GetGuid(0), r.GetString(1), r.Str("attendance_mode"), r.Uuid("proxy_holder_user_id"), r.Time("left_at")), ct))
            .ToDictionary(a => a.UserId);
        var voters = Eligible(c.Policy, members, attendance, circulation);
        var q = circulation ? null : await quorum.ComputeAsync(sql, c, "actual", ct);
        return (c, voters, q, members);
    }

    // ---------------- القراءة ----------------

    private sealed record MotionRow(Guid Id, Guid MeetingId, Guid ResolutionId, Guid? AgendaItemId, int Round, string Method, string Status,
        DateTime OpenedAt, DateTime? DeadlineAt, DateTime? ClosedAt, string? Result, string? Tally, string Eligible, string? CloseReason,
        string ResolutionTitle, string? ResolutionNumber);

    private const string MotionSelect = """
        SELECT mo.id, mo.meeting_id, mo.resolution_id, mo.agenda_item_id, mo.round, mo.method, mo.status, mo.opened_at, mo.deadline_at, mo.closed_at,
               mo.result, mo.tally::text AS tally, mo.eligible::text AS eligible, mo.close_reason, r.title_ar, r.number
        FROM deliberation.motions mo JOIN resolutions.resolutions r ON r.id = mo.resolution_id
        """;

    private static MotionRow MapMotion(System.Data.Common.DbDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.Uuid("agenda_item_id"), r.GetInt32(4),
        r.GetString(5), r.GetString(6), r.GetDateTime(7), r.Time("deadline_at"), r.Time("closed_at"), r.Str("result"), r.Str("tally"), r.GetString(12),
        r.Str("close_reason"), r.GetString(14), r.Str("number"));

    private static async Task<IResult> List(Guid id, Guid? resolutionId, Db db, IAccessService access, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MeetingView, Scope.Record("meeting", id), ct);
        var perms = await access.GetPermissionsAsync(Scope.Record("meeting", id), ct);
        var seeIndividual = perms.Contains(Permissions.VoteViewIndividual);
        var motions = await db.QueryAsync(MotionSelect + "\nWHERE mo.meeting_id = @id AND (@resolutionId::uuid IS NULL OR mo.resolution_id = @resolutionId) ORDER BY mo.opened_at",
            new { id, resolutionId }, MapMotion, ct);
        var ids = motions.Select(m => m.Id).ToArray();
        var participation = await db.QueryAsync("SELECT motion_id, principal_user_id, cast_by_user_id FROM deliberation.vote_participation WHERE motion_id = ANY(@ids::uuid[])",
            new { ids }, r => (Motion: r.GetGuid(0), Principal: r.GetGuid(1), CastBy: r.GetGuid(2)), ct);
        var votes = await db.QueryAsync("""
            SELECT v.motion_id, v.principal_user_id, u.display_name_ar, v.choice, v.capacity, v.cast_at, cb.display_name_ar AS cast_by
            FROM deliberation.votes v LEFT JOIN iam.users u ON u.id = v.principal_user_id LEFT JOIN iam.users cb ON cb.id = v.cast_by_user_id
            WHERE v.motion_id = ANY(@ids::uuid[])
            """, new { ids }, r => (Motion: r.GetGuid(0), Principal: r.Uuid("principal_user_id"), Name: r.Str("display_name_ar"), Choice: r.GetString(3),
            Capacity: r.Str("capacity"), CastAt: r.GetDateTime(5), CastBy: r.Str("cast_by")), ct);
        var reservations = await db.QueryAsync("""
            SELECT rv.motion_id, u.display_name_ar, rv.kind, rv.text, rv.created_at FROM deliberation.reservations rv JOIN iam.users u ON u.id = rv.user_id
            WHERE rv.motion_id = ANY(@ids::uuid[]) ORDER BY rv.created_at
            """, new { ids }, r => (Motion: r.GetGuid(0), Name: r.GetString(1), Kind: r.GetString(2), Text: r.GetString(3), At: r.GetDateTime(4)), ct);

        return Results.Ok(new
        {
            canManage = perms.Contains(Permissions.VoteManage),
            canCast = perms.Contains(Permissions.VoteCast),
            seeIndividual,
            items = motions.Select(m =>
            {
                var eligible = JsonSerializer.Deserialize<List<EligibleVoter>>(m.Eligible, PolicyJson.Options) ?? [];
                var voted = participation.Where(p => p.Motion == m.Id).ToList();
                var secret = m.Method == "secret";
                return new
                {
                    id = m.Id, meetingId = m.MeetingId, resolutionId = m.ResolutionId, resolutionTitle = m.ResolutionTitle, resolutionNumber = m.ResolutionNumber,
                    agendaItemId = m.AgendaItemId, round = m.Round, method = m.Method, status = m.Status, openedAt = m.OpenedAt, deadlineAt = m.DeadlineAt,
                    closedAt = m.ClosedAt, result = m.Result, closeReason = m.CloseReason,
                    tally = m.Tally is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(m.Tally),
                    eligibleCount = eligible.Count, votedCount = voted.Count,
                    eligible = eligible.Select(e => new { e.PrincipalUserId, e.Name, e.Role, e.Capacity, e.CastByUserId, voted = voted.Any(v => v.Principal == e.PrincipalUserId) }),
                    myBallots = eligible.Where(e => e.CastByUserId == me.UserId).Select(e => new
                    {
                        e.PrincipalUserId, e.Name, e.Capacity,
                        voted = voted.Any(v => v.Principal == e.PrincipalUserId),
                        choice = secret ? null : votes.FirstOrDefault(v => v.Motion == m.Id && v.Principal == e.PrincipalUserId).Choice,
                    }),
                    // الأصوات الفردية لمن يملك الصلاحية وفي غير السري فقط؛ وإلا المجاميع وحدها
                    votes = !secret && seeIndividual
                        ? votes.Where(v => v.Motion == m.Id).Select(v => new { principalUserId = v.Principal, name = v.Name, choice = v.Choice, capacity = v.Capacity, castBy = v.CastBy, castAt = v.CastAt })
                        : null,
                    reservations = reservations.Where(r => r.Motion == m.Id).Select(r => new { name = r.Name, kind = r.Kind, text = r.Text, createdAt = r.At }),
                };
            }),
        });
    }

    // ---------------- الفتح ----------------

    private static async Task<IResult> Open(Guid id, OpenMotionRequest req, Db db, IAccessService access, QuorumService quorum, INotifier notifier,
        IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.VoteManage, Scope.Record("meeting", id), ct);
        var motionId = Guid.CreateVersion7();
        var outcome = await db.InTransactionAsync(async tx =>
        {
            var meeting = await tx.QuerySingleAsync("SELECT status, meeting_type, circulation_deadline, reference_no FROM meetings.meetings WHERE id = @id FOR UPDATE",
                new { id }, r => (Status: r.GetString(0), Type: r.GetString(1), Deadline: r.Time("circulation_deadline"), Ref: r.Str("reference_no")), ct);
            if (meeting == default) throw new NotFoundException();
            var (c, voters, q, _) = await EligibilityAsync(tx, quorum, id, ct);
            var circulation = meeting.Type == "circulation";
            var method = circulation ? "circulation" : req.Method ?? "electronic";
            if (circulation)
            {
                if (meeting.Status != "agenda_published") throw new ConflictException("invalid-transition", "يُفتح التصويت بالتمرير بعد إرسال الدعوات وقبل الإغلاق");
                if (!c.Policy.AllowCirculation) throw new ConflictException("circulation-not-allowed", "سياسة الجهة لا تسمح بالتمرير");
            }
            else
            {
                if (method == "circulation") throw AppValidationException.For("method", "التصويت بالتمرير لاجتماع من نوع التمرير فقط");
                if (meeting.Status != "in_progress") throw new ConflictException("invalid-transition", "يُفتح التصويت أثناء انعقاد الاجتماع فقط");
                if (method == "secret" && !c.Policy.Voting.SecretAllowed) throw new ConflictException("secret-not-allowed", "سياسة الجهة لا تسمح بالتصويت السري");
            }
            var res = await tx.QuerySingleAsync("SELECT status, meeting_id, agenda_item_id, title_ar FROM resolutions.resolutions WHERE id = @rid FOR UPDATE",
                new { rid = req.ResolutionId }, r => (Status: r.GetString(0), Meeting: r.Uuid("meeting_id"), Item: r.Uuid("agenda_item_id"), Title: r.GetString(3)), ct);
            if (res == default || res.Meeting != id) throw AppValidationException.For("resolutionId", "القرار المقترح لا يتبع هذا الاجتماع");
            if (res.Status != "proposed") throw new ConflictException("invalid-transition", "يُصوت على القرار المقترح فقط");
            if (await tx.ScalarAsync<long>("SELECT count(*) FROM deliberation.motions WHERE resolution_id = @rid AND status = 'open'", new { rid = req.ResolutionId }, ct) > 0)
                throw new ConflictException("motion-open", "يوجد تصويت مفتوح على هذا القرار");
            // التحقق من النصاب وقت التصويت (لا يُفترض أن كل لجنة تستخدم قاعدة واحدة: القاعدة من سياسة الجهة المثبتة على الاجتماع)
            if (q is not null)
            {
                await quorum.RecordAsync(tx, c, "vote", q, null, me.UserId, ct);
                if (!q.IsMet) throw new ConflictException("quorum-not-met", "النصاب غير مكتمل؛ لا يُفتح التصويت",
                    new Dictionary<string, object?> { ["counted"] = q.Counted, ["required"] = q.Required });
            }
            if (voters.Count == 0) throw new ConflictException("no-eligible-voters", "لا يوجد أعضاء مؤهلون للتصويت");
            var round = await tx.ScalarAsync<int>("SELECT COALESCE(max(round), 0) + 1 FROM deliberation.motions WHERE resolution_id = @rid", new { rid = req.ResolutionId }, ct);
            var deadline = circulation ? req.DeadlineAt ?? meeting.Deadline : req.DeadlineAt;
            if (circulation && (deadline is null || deadline <= DateTime.UtcNow)) throw AppValidationException.For("deadlineAt", "حدد موعدًا مستقبليًا لانتهاء التمرير");
            var rule = JsonSerializer.Serialize(new { voting = c.Policy.Voting, quorum = c.Policy.Quorum, policyVersion = c.Policy.Version }, PolicyJson.Options);
            await tx.ExecuteAsync("""
                INSERT INTO deliberation.motions (id, meeting_id, agenda_item_id, resolution_id, round, method, opened_by, deadline_at, rule_snapshot, eligible, quorum_snapshot)
                VALUES (@motionId, @id, @item, @rid, @round, @method, @by, @deadline, @rule::jsonb, @eligible::jsonb, @quorum::jsonb)
                """, new
            {
                motionId, id, item = res.Item, rid = req.ResolutionId, round, method, by = me.UserId, deadline, rule,
                eligible = JsonSerializer.Serialize(voters, PolicyJson.Options),
                quorum = q is null ? null : JsonSerializer.Serialize(new { q.Eligible, q.Counted, q.Required, q.IsMet }, PolicyJson.Options),
            }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "motion.opened", "meeting", id.ToString(), After: new { motionId, resolutionId = req.ResolutionId, method, round, eligible = voters.Count }), ct);
            await audit.WriteAsync(tx, new AuditEntry("RES", "resolution.vote_opened", "resolution", req.ResolutionId.ToString(), After: new { motionId, method, round }), ct);
            if (method == "consensus")
                return await CloseAsync(tx, motionId, me.UserId, "consensus", audit, ct);
            if (method is "electronic" or "secret" or "circulation")
                foreach (var castBy in voters.Select(v => v.CastByUserId).Distinct())
                    await notifier.NotifyAsync(tx, new NotificationRequest(castBy, "vote_opened", $"تصويت مفتوح: {res.Title}", $"Vote open: {res.Title}",
                        $"/meetings/{id}?tab=voting", $"vote_opened:{motionId}:{castBy}", "warning"), ct);
            return null;
        }, ct);
        return Results.Created($"/api/v1/motions/{motionId}", new { id = motionId, status = outcome is null ? "open" : "closed", result = outcome?.Result });
    }

    // ---------------- الإدلاء بالصوت ----------------

    private static async Task<IResult> Cast(Guid mid, CastRequest req, Db db, IAccessService access, QuorumService quorum, IAuditWriter audit,
        ICurrentUser me, CancellationToken ct)
    {
        var meetingId = await db.ScalarAsync<Guid?>("SELECT meeting_id FROM deliberation.motions WHERE id = @mid", new { mid }, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Permissions.VoteCast, Scope.Record("meeting", meetingId), ct);
        var closed = await db.InTransactionAsync(async tx =>
        {
            var m = await tx.QuerySingleAsync("SELECT status, method, deadline_at, resolution_id, eligible::text FROM deliberation.motions WHERE id = @mid FOR UPDATE",
                new { mid }, r => (Status: r.GetString(0), Method: r.GetString(1), Deadline: r.Time("deadline_at"), Resolution: r.GetGuid(3), Eligible: r.GetString(4)), ct);
            if (m.Status != "open") throw new ConflictException("motion-closed", "التصويت مغلق");
            if (m.Method is "show_of_hands" or "consensus") throw new ConflictException("invalid-method", "هذا التصويت يُسجله أمين السر");
            if (m.Deadline is { } d && d <= DateTime.UtcNow) throw new ConflictException("deadline-passed", "انتهت مهلة التصويت");
            // الأهلية تُفحص الآن لا عند الفتح: من غادر الاجتماع أو انتهت عضويته لا يصوت
            var (_, voters, _, _) = await EligibilityAsync(tx, quorum, meetingId, ct);
            var principal = req.PrincipalUserId ?? me.UserId;
            var entry = voters.FirstOrDefault(v => v.PrincipalUserId == principal && v.CastByUserId == me.UserId)
                ?? throw new ForbiddenException(principal == me.UserId ? "لست مؤهلًا للتصويت الآن (العضوية أو الحضور أو حق التصويت)" : "لا تملك الإدلاء بصوت هذا العضو");
            var inserted = await tx.ExecuteAsync("""
                INSERT INTO deliberation.vote_participation (motion_id, principal_user_id, cast_by_user_id) VALUES (@mid, @principal, @me)
                ON CONFLICT DO NOTHING
                """, new { mid, principal, me = me.UserId }, ct);
            if (inserted == 0) throw new ConflictException("already-voted", "سُجل صوت هذا العضو مسبقًا؛ لا يُقبل صوت مكرر");
            var secret = m.Method == "secret";
            await tx.ExecuteAsync("""
                INSERT INTO deliberation.votes (id, motion_id, principal_user_id, cast_by_user_id, capacity, choice, cast_at)
                VALUES (@vid, @mid, @principal, @castBy, @capacity, @Choice, CASE WHEN @secret::boolean THEN date_trunc('minute', now()) ELSE now() END)
                """, new
            {
                vid = Guid.CreateVersion7(), mid, principal = secret ? (Guid?)null : principal, castBy = secret ? (Guid?)null : me.UserId,
                capacity = secret ? null : entry.Capacity, req.Choice, secret,
            }, ct);
            if (!string.IsNullOrWhiteSpace(req.Reservation))
                await tx.ExecuteAsync("""
                    INSERT INTO deliberation.reservations (id, motion_id, resolution_id, user_id, kind, text) VALUES (@id, @mid, @rid, @uid, 'reservation', @text)
                    """, new { id = Guid.CreateVersion7(), mid, rid = m.Resolution, uid = principal, text = req.Reservation.Trim() }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "motion.vote_cast", "motion", mid.ToString(),
                After: secret ? new { secret = true } : new { principal, capacity = entry.Capacity, choice = req.Choice, reservation = req.Reservation is not null }), ct);
            var snapshot = JsonSerializer.Deserialize<List<EligibleVoter>>(m.Eligible, PolicyJson.Options) ?? [];
            var expected = snapshot.Select(v => v.PrincipalUserId).Union(voters.Select(v => v.PrincipalUserId)).ToHashSet();
            var votedCount = await tx.ScalarAsync<long>("SELECT count(*) FROM deliberation.vote_participation WHERE motion_id = @mid AND principal_user_id = ANY(@ids::uuid[])",
                new { mid, ids = expected.ToArray() }, ct);
            return votedCount >= expected.Count ? await CloseAsync(tx, mid, me.UserId, "all_voted", audit, ct) : null;
        }, ct);
        return Results.Ok(new { motionId = mid, recorded = true, closed = closed is not null, result = closed?.Result });
    }

    private static async Task<IResult> RecordHands(Guid mid, RecordRequest req, Db db, IAccessService access, QuorumService quorum, IAuditWriter audit,
        ICurrentUser me, CancellationToken ct)
    {
        var meetingId = await db.ScalarAsync<Guid?>("SELECT meeting_id FROM deliberation.motions WHERE id = @mid", new { mid }, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Permissions.VoteManage, Scope.Record("meeting", meetingId), ct);
        await db.InTransactionAsync(async tx =>
        {
            var m = await tx.QuerySingleAsync("SELECT status, method FROM deliberation.motions WHERE id = @mid FOR UPDATE", new { mid }, r => (Status: r.GetString(0), Method: r.GetString(1)), ct);
            if (m.Status != "open") throw new ConflictException("motion-closed", "التصويت مغلق");
            if (m.Method != "show_of_hands") throw new ConflictException("invalid-method", "التسجيل اليدوي للتصويت برفع الأيدي فقط");
            var (_, voters, _, _) = await EligibilityAsync(tx, quorum, meetingId, ct);
            foreach (var v in req.Votes)
            {
                var entry = voters.FirstOrDefault(e => e.PrincipalUserId == v.PrincipalUserId)
                    ?? throw AppValidationException.For("votes", "عضو غير مؤهل للتصويت الآن");
                if (await tx.ExecuteAsync("INSERT INTO deliberation.vote_participation (motion_id, principal_user_id, cast_by_user_id) VALUES (@mid, @p, @by) ON CONFLICT DO NOTHING",
                        new { mid, p = v.PrincipalUserId, by = entry.CastByUserId }, ct) == 0)
                    throw new ConflictException("already-voted", $"سُجل صوت {entry.Name} مسبقًا");
                await tx.ExecuteAsync("""
                    INSERT INTO deliberation.votes (id, motion_id, principal_user_id, cast_by_user_id, capacity, choice, cast_at, recorded_by)
                    VALUES (@id, @mid, @p, @castBy, @capacity, @choice, now(), @me)
                    """, new { id = Guid.CreateVersion7(), mid, p = v.PrincipalUserId, castBy = entry.CastByUserId, capacity = entry.Capacity, choice = v.Choice, me = me.UserId }, ct);
            }
            await audit.WriteAsync(tx, new AuditEntry("MTG", "motion.hands_recorded", "motion", mid.ToString(), After: new { count = req.Votes.Count }), ct);
        }, ct);
        return Results.NoContent();
    }

    // ---------------- الإغلاق والنتيجة ----------------

    private static async Task<IResult> Close(Guid mid, Db db, IAccessService access, IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        var meetingId = await db.ScalarAsync<Guid?>("SELECT meeting_id FROM deliberation.motions WHERE id = @mid", new { mid }, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Permissions.VoteManage, Scope.Record("meeting", meetingId), ct);
        var r = await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM deliberation.motions WHERE id = @mid FOR UPDATE", new { mid }, ct);
            if (status != "open") throw new ConflictException("motion-closed", "التصويت مغلق مسبقًا");
            return await CloseAsync(tx, mid, me.UserId, "manual", audit, ct);
        }, ct);
        return Results.Ok(new { motionId = mid, status = "closed", result = r!.Result, tally = JsonSerializer.Deserialize<JsonElement>(r.TallyJson) });
    }

    private static async Task<IResult> Withdraw(Guid mid, MeetingsModule.ReasonRequest req, Db db, IAccessService access, IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        var meetingId = await db.ScalarAsync<Guid?>("SELECT meeting_id FROM deliberation.motions WHERE id = @mid", new { mid }, ct) ?? throw new NotFoundException();
        await access.DemandAsync(Permissions.VoteManage, Scope.Record("meeting", meetingId), ct);
        await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM deliberation.motions WHERE id = @mid FOR UPDATE", new { mid }, ct);
            if (status != "open") throw new ConflictException("motion-closed", "التصويت مغلق");
            if (await tx.ScalarAsync<long>("SELECT count(*) FROM deliberation.vote_participation WHERE motion_id = @mid", new { mid }, ct) > 0)
                throw new ConflictException("votes-cast", "لا يُسحب تصويت بعد الإدلاء بأصوات؛ أغلقه لتُحسب نتيجته");
            await tx.ExecuteAsync("UPDATE deliberation.motions SET status = 'withdrawn', result = 'withdrawn', closed_at = now(), closed_by = @me, close_reason = @Reason WHERE id = @mid",
                new { mid, me = me.UserId, req.Reason }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MTG", "motion.withdrawn", "motion", mid.ToString(), After: new { req.Reason }), ct);
        }, ct);
        return Results.NoContent();
    }

    public sealed record CloseOutcome(string Result, string TallyJson);

    /// <summary>يحسب النتيجة ويغلق المقترح (المقترح مقفل مسبقًا داخل المعاملة).</summary>
    internal static async Task<CloseOutcome> CloseAsync(ISql tx, Guid mid, Guid? by, string reason, IAuditWriter audit, CancellationToken ct)
    {
        var m = await tx.QuerySingleAsync("SELECT meeting_id, method, rule_snapshot::text, eligible::text, resolution_id FROM deliberation.motions WHERE id = @mid",
            new { mid }, r => (Meeting: r.GetGuid(0), Method: r.GetString(1), Rule: r.GetString(2), Eligible: r.GetString(3), Resolution: r.GetGuid(4)), ct);
        using var ruleDoc = JsonDocument.Parse(m.Rule);
        var voting = ruleDoc.RootElement.GetProperty("voting").Deserialize<VotingRule>(PolicyJson.Options) ?? new VotingRule();
        var quorumRule = ruleDoc.RootElement.GetProperty("quorum").Deserialize<QuorumRule>(PolicyJson.Options) ?? new QuorumRule();
        var eligible = JsonSerializer.Deserialize<List<EligibleVoter>>(m.Eligible, PolicyJson.Options) ?? [];
        var bodyId = await tx.ScalarAsync<Guid>("SELECT body_id FROM meetings.meetings WHERE id = @id", new { id = m.Meeting }, ct);
        var members = await tx.ScalarAsync<long>("""
            SELECT count(DISTINCT user_id) FROM bodies.body_members bm JOIN meetings.meetings mt ON mt.id = @meeting
            WHERE bm.body_id = @bodyId AND bm.status <> 'suspended' AND bm.is_voting AND bm.role IN ('chair','vice_chair','member')
              AND bm.start_date <= (mt.starts_at AT TIME ZONE 'Asia/Riyadh')::date AND (bm.end_date IS NULL OR bm.end_date >= (mt.starts_at AT TIME ZONE 'Asia/Riyadh')::date)
            """, new { meeting = m.Meeting, bodyId }, ct);
        var counts = await tx.QueryAsync("SELECT choice, count(*) FROM deliberation.votes WHERE motion_id = @mid GROUP BY choice", new { mid },
            r => (Choice: r.GetString(0), N: (int)r.GetInt64(1)), ct);
        var chairId = eligible.FirstOrDefault(e => e.Role == "chair")?.PrincipalUserId;
        var chairChoice = chairId is null || m.Method == "secret" ? null
            : await tx.ScalarAsync<string>("SELECT choice FROM deliberation.votes WHERE motion_id = @mid AND principal_user_id = @chairId", new { mid, chairId }, ct);
        int F(string c) => counts.FirstOrDefault(x => x.Choice == c).N;
        var cast = counts.Sum(x => x.N);

        string result;
        VoteDecision? decision = null;
        int present;
        bool quorumMet;
        if (m.Method == "consensus")
        {
            present = eligible.Count;
            quorumMet = true;
            result = "adopted";
        }
        else
        {
            // التمرير: الحاضر = من أدلى بصوته، ويُطبق عليه النصاب نفسه
            present = m.Method == "circulation" ? cast : eligible.Count;
            var required = QuorumCalculator.Required(quorumRule, (int)members);
            quorumMet = m.Method != "circulation" || cast >= required;
            if (!quorumMet) result = "no_quorum";
            else
            {
                decision = VotingCalculator.Decide(voting, new Tally(F("for"), F("against"), F("abstain"), present, (int)members, chairChoice));
                result = decision.Result;
            }
        }
        var tally = Json.Serialize(new
        {
            method = m.Method, @for = F("for"), against = F("against"), abstain = F("abstain"), cast, eligible = eligible.Count, present, members,
            threshold = voting.Threshold, abstentions = voting.Abstentions, tieBreaker = voting.TieBreaker,
            chairCasting = decision?.ChairCasting ?? false, basis = decision?.Basis, quorumMet, result,
        });
        await tx.ExecuteAsync("""
            UPDATE deliberation.motions SET status = 'closed', result = @result, tally = @tally::jsonb, closed_at = now(), closed_by = @by, close_reason = @reason WHERE id = @mid
            """, new { mid, result, tally, by, reason }, ct);
        await audit.WriteAsync(tx, new AuditEntry("MTG", "motion.closed", "motion", mid.ToString(), After: new { result, reason, cast }, ActorUserId: by), ct);
        await audit.WriteAsync(tx, new AuditEntry("RES", "resolution.vote_closed", "resolution", m.Resolution.ToString(), After: new { motionId = mid, result }, ActorUserId: by), ct);
        return new CloseOutcome(result, tally);
    }
}

internal sealed class VotingQuery : IVotingQuery
{
    public async Task<IReadOnlyList<MotionOutcome>> LatestOutcomesAsync(ISql sql, Guid meetingId, CancellationToken ct = default) =>
        await sql.QueryAsync("""
            SELECT DISTINCT ON (resolution_id) id, resolution_id, method, result, tally::text, closed_at
            FROM deliberation.motions WHERE meeting_id = @meetingId AND status = 'closed'
            ORDER BY resolution_id, round DESC
            """, new { meetingId }, r => new MotionOutcome(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDateTime(5)), ct);

    public async Task<int> OpenMotionsAsync(ISql sql, Guid meetingId, CancellationToken ct = default) =>
        (int)await sql.ScalarAsync<long>("SELECT count(*) FROM deliberation.motions WHERE meeting_id = @meetingId AND status = 'open'", new { meetingId }, ct);
}

internal sealed class MotionBodyResolver(Db db) : IRecordBodyResolver
{
    public string RecordType => "motion";
    public Task<Guid?> ResolveBodyAsync(Guid recordId, CancellationToken ct = default) =>
        db.ScalarAsync<Guid?>("SELECT m.body_id FROM deliberation.motions mo JOIN meetings.meetings m ON m.id = mo.meeting_id WHERE mo.id = @recordId", new { recordId }, ct);
}

/// <summary>يغلق التصويت بالتمرير عند انتهاء مهلته.</summary>
internal sealed class VotingDeadlineWorker(IServiceScopeFactory scopes, ILogger<VotingDeadlineWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Db>();
                var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
                var due = await db.QueryAsync("SELECT id FROM deliberation.motions WHERE status = 'open' AND deadline_at < now()", null, r => r.GetGuid(0), stoppingToken);
                foreach (var id in due)
                    await db.InTransactionAsync(async tx =>
                    {
                        if (await tx.ScalarAsync<string>("SELECT status FROM deliberation.motions WHERE id = @id FOR UPDATE", new { id }, stoppingToken) == "open")
                            await Voting.CloseAsync(tx, id, null, "deadline", audit, stoppingToken);
                    }, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "فشل إغلاق التصويت المنتهية مهلته");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ContinueWith(_ => { });
        }
    }
}
