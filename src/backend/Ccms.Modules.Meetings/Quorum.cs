using System.Text.Json;
using Ccms.BuildingBlocks.Data;
using Ccms.Contracts;

namespace Ccms.Modules.Meetings;

public sealed record QuorumMember(Guid MemberId, Guid UserId, string Name, string Role, bool IsVoting, Guid? AlternateForMemberId);

public sealed record QuorumAttendance(Guid UserId, string Status, string? Mode, Guid? ProxyHolderUserId, DateTime? LeftAt);

public sealed record QuorumLine(Guid UserId, string Name, string Role, bool Principal, bool Counted, string Reason);

public sealed record QuorumResult(int Eligible, int Counted, int Required, bool ChairRequired, bool ChairPresent, bool IsMet, string Basis, IReadOnlyList<QuorumLine> Lines);

/// <summary>حساب النصاب وفق لقطة سياسة الجهة (08 §5). دالة صرفة بلا قاعدة بيانات؛ حالات الاختبار المرجعية Q1–Q8.
/// N = الأعضاء المصوتون الأصلاء (يُستثنى البدلاء والمراقبون وأمين السر).
/// P = الأصلاء الحاضرون (والحاضرون عن بعد إن احتُسبوا) + المنيب عنهم بإنابة مقبولة + البدلاء الحاضرون عن أصيل غائب.</summary>
public static class QuorumCalculator
{
    public static int Required(QuorumRule rule, int eligible) => eligible == 0 ? 1 : rule.Type switch
    {
        "fraction" => (int)Math.Ceiling(eligible * (double)rule.Numerator / Math.Max(rule.Denominator, 1) - 1e-9),
        "fixed" => Math.Min(Math.Max(rule.FixedCount ?? 1, 1), eligible),
        _ => eligible / 2 + 1,
    };

    public static QuorumResult Compute(QuorumRule rule, bool allowProxy, int maxProxiesPerHolder, IReadOnlyList<QuorumMember> members,
        IReadOnlyDictionary<Guid, QuorumAttendance> attendance, ISet<Guid> chairDelegates)
    {
        var principals = members.Where(m => m.IsVoting && m.Role is "chair" or "vice_chair" or "member").ToList();
        var lines = new Dictionary<Guid, QuorumLine>();
        var counted = new HashSet<Guid>();

        bool PresentCounted(Guid userId, out string reason)
        {
            if (!attendance.TryGetValue(userId, out var a)) { reason = "not_recorded"; return false; }
            if (a.Status == "present" && a.LeftAt is not null) { reason = "departed"; return false; }
            switch (a.Status)
            {
                case "present" when a.Mode == "remote" && !rule.CountRemote: reason = "remote_not_counted"; return false;
                case "present": reason = a.Mode == "remote" ? "present_remote" : "present_in_person"; return true;
                case "excused": reason = "excused"; return false;
                case "absent": reason = "absent"; return false;
                default: reason = a.Status; return false;
            }
        }

        // 1) الحضور المباشر للأصلاء
        foreach (var p in principals)
        {
            var ok = PresentCounted(p.UserId, out var reason);
            if (ok) counted.Add(p.MemberId);
            lines[p.MemberId] = new QuorumLine(p.UserId, p.Name, p.Role, true, ok, reason);
        }

        // 2) الإنابة: الأصيل الغائب ممثل بعضو مصوت حاضر، ضمن الحد الأقصى لكل منيب
        var proxyLoad = new Dictionary<Guid, int>();
        foreach (var p in principals.Where(p => !counted.Contains(p.MemberId)))
        {
            if (!attendance.TryGetValue(p.UserId, out var a) || a.Status != "proxy" || a.ProxyHolderUserId is not { } holder) continue;
            var holderMember = principals.FirstOrDefault(x => x.UserId == holder);
            string reason;
            if (!allowProxy || !rule.CountProxies) reason = "proxy_not_allowed";
            else if (holderMember is null || !counted.Contains(holderMember.MemberId)) reason = "proxy_holder_absent";
            else if (proxyLoad.GetValueOrDefault(holder) >= maxProxiesPerHolder) reason = "proxy_limit_exceeded";
            else
            {
                proxyLoad[holder] = proxyLoad.GetValueOrDefault(holder) + 1;
                counted.Add(p.MemberId);
                lines[p.MemberId] = lines[p.MemberId] with { Counted = true, Reason = "proxy" };
                continue;
            }
            lines[p.MemberId] = lines[p.MemberId] with { Reason = reason };
        }

        // 3) البدلاء: يُحتسب البديل الحاضر عن أصيله الغائب (بديل واحد لكل أصيل)
        foreach (var alt in members.Where(m => m.Role == "alternate" && m.AlternateForMemberId is not null))
        {
            var principal = principals.FirstOrDefault(p => p.MemberId == alt.AlternateForMemberId);
            var present = PresentCounted(alt.UserId, out var reason);
            if (principal is not null && present && !counted.Contains(principal.MemberId))
            {
                counted.Add(principal.MemberId);
                lines[principal.MemberId] = lines[principal.MemberId] with { Counted = true, Reason = "substituted_by_alternate" };
                lines[alt.MemberId] = new QuorumLine(alt.UserId, alt.Name, alt.Role, false, true, "alternate_substitute");
            }
            else lines[alt.MemberId] = new QuorumLine(alt.UserId, alt.Name, alt.Role, false, false, present ? "principal_present" : reason);
        }

        // 4) غير المصوتين للعرض فقط
        foreach (var m in members.Where(m => !lines.ContainsKey(m.MemberId)))
        {
            PresentCounted(m.UserId, out var reason);
            lines[m.MemberId] = new QuorumLine(m.UserId, m.Name, m.Role, false, false, "non_voting:" + reason);
        }

        var chair = principals.FirstOrDefault(p => p.Role == "chair");
        var chairPresent = (chair is not null && PresentCounted(chair.UserId, out _))
            || chairDelegates.Any(d => PresentCounted(d, out _));
        var n = principals.Count;
        var required = Required(rule, n);
        var p2 = counted.Count;
        var met = n > 0 && p2 >= required && (!rule.ChairRequired || chairPresent);
        var basis = rule.Type switch
        {
            "fraction" => $"{rule.Numerator}/{rule.Denominator}",
            "fixed" => $"fixed:{rule.FixedCount}",
            _ => "majority",
        };
        return new QuorumResult(n, p2, required, rule.ChairRequired, chairPresent, met, basis,
            [.. lines.Values.OrderByDescending(l => l.Principal).ThenBy(l => Array.IndexOf(RoleOrder, l.Role)).ThenBy(l => l.Name)]);
    }

    private static readonly string[] RoleOrder = ["chair", "vice_chair", "member", "alternate", "secretary", "observer"];
}

/// <summary>يجمع مدخلات الحساب من القاعدة لاجتماع معيّن.</summary>
internal sealed class QuorumService(IBodyPolicyQuery policies)
{
    public sealed record Context(Guid MeetingId, Guid BodyId, DateOnly MeetingDate, string Status, string MeetingType, BodyPolicy Policy);

    public async Task<Context> LoadContextAsync(ISql sql, Guid meetingId, CancellationToken ct)
    {
        var m = await sql.QuerySingleAsync("""
            SELECT body_id, (starts_at AT TIME ZONE 'Asia/Riyadh')::date AS d, status, meeting_type, policy_id FROM meetings.meetings WHERE id = @meetingId
            """, new { meetingId }, r => (Body: r.GetGuid(0), Date: r.GetFieldValue<DateOnly>(1), Status: r.GetString(2), Type: r.GetString(3), Policy: r.Uuid("policy_id")), ct);
        if (m == default) throw new BuildingBlocks.Web.NotFoundException();
        var policy = (m.Policy is { } pid ? await policies.GetByIdAsync(pid, ct) : null)
            ?? await policies.GetEffectiveAsync(m.Body, m.Date, ct)
            ?? throw new InvalidOperationException("لا توجد سياسة سارية للجهة");
        return new Context(meetingId, m.Body, m.Date, m.Status, m.Type, policy);
    }

    /// <summary>العضويات السارية في تاريخ الاجتماع؛ إن تغيّرت صفة العضو في اليوم نفسه يُعتد بالسجل الأحدث.</summary>
    public async Task<List<QuorumMember>> MembersAsync(ISql sql, Guid bodyId, DateOnly on, CancellationToken ct)
    {
        var rows = await sql.QueryAsync("""
            SELECT DISTINCT ON (m.user_id) m.id, m.user_id, u.display_name_ar, m.role, m.is_voting, m.alternate_for_member_id
            FROM bodies.body_members m JOIN iam.users u ON u.id = m.user_id
            WHERE m.body_id = @bodyId AND m.status <> 'suspended' AND m.start_date <= @on AND (m.end_date IS NULL OR m.end_date >= @on)
            ORDER BY m.user_id, m.start_date DESC, m.created_at DESC
            """, new { bodyId, on }, r => new QuorumMember(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.Uuid("alternate_for_member_id")), ct);
        return rows;
    }

    /// <summary>mode: actual (سجل الحضور)، forecast (ردود الدعوات المقبولة)، circulation (الموافقون على المشاركة في التمرير).</summary>
    public async Task<QuorumResult> ComputeAsync(ISql sql, Context c, string mode, CancellationToken ct)
    {
        var members = await MembersAsync(sql, c.BodyId, c.MeetingDate, ct);
        Dictionary<Guid, QuorumAttendance> attendance;
        if (mode == "actual")
            attendance = (await sql.QueryAsync("SELECT user_id, status, attendance_mode, proxy_holder_user_id, left_at FROM meetings.attendance_records WHERE meeting_id = @id",
                new { id = c.MeetingId }, r => new QuorumAttendance(r.GetGuid(0), r.GetString(1), r.Str("attendance_mode"), r.Uuid("proxy_holder_user_id"), r.Time("left_at")), ct))
                .ToDictionary(a => a.UserId);
        else
            attendance = (await sql.QueryAsync("SELECT user_id, response FROM meetings.invitations WHERE meeting_id = @id", new { id = c.MeetingId },
                r => (User: r.GetGuid(0), Response: r.GetString(1)), ct))
                .Where(x => x.Response == "accepted")
                .ToDictionary(x => x.User, x => new QuorumAttendance(x.User, "present", "in_person", null, null));
        var delegates = (await sql.QueryAsync("""
            SELECT delegate_user_id FROM iam.delegations WHERE body_id = @body AND scope = 'chair_authority' AND status = 'active' AND valid_from <= now() AND valid_to > now()
            """, new { body = c.BodyId }, r => r.GetGuid(0), ct)).ToHashSet();
        return QuorumCalculator.Compute(c.Policy.Quorum, c.Policy.AllowProxy, c.Policy.MaxProxiesPerMember, members, attendance, delegates);
    }

    public Task RecordAsync(ISql sql, Context c, string context, QuorumResult r, string? overrideReason, Guid by, CancellationToken ct) =>
        sql.ExecuteAsync("""
            INSERT INTO meetings.quorum_checks (id, meeting_id, context, eligible_count, counted_present, required_count, chair_present, is_met, override_reason, rule_snapshot, breakdown, checked_by)
            VALUES (@id, @meeting, @context, @n, @p, @req, @chair, @met, @overrideReason, @rule::jsonb, @lines::jsonb, @by)
            """, new
        {
            id = Guid.CreateVersion7(), meeting = c.MeetingId, context, n = r.Eligible, p = r.Counted, req = r.Required, chair = r.ChairPresent, met = r.IsMet,
            overrideReason, rule = JsonSerializer.Serialize(new { quorum = c.Policy.Quorum, c.Policy.AllowProxy, c.Policy.MaxProxiesPerMember, policyVersion = c.Policy.Version }, PolicyJson.Options),
            lines = JsonSerializer.Serialize(r.Lines, PolicyJson.Options), by,
        }, ct);
}
