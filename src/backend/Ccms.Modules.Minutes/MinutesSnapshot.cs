using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Pdf;

namespace Ccms.Modules.Minutes;

// ---------------- نموذج المحتوى المحرر ----------------

public sealed record ContentSection(string Key, string Title, string? Body);
public sealed record ContentItem(Guid AgendaItemId, string? Discussion, string? Recommendation);

/// <summary>محتوى المحضر المحرر: الأقسام النصية وملخص المناقشة والتوصية لكل بند. البيانات الآلية (الحضور، النصاب، القرارات…) في اللقطة.</summary>
public sealed record MinutesContent(List<ContentSection> Sections, List<ContentItem> Items)
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static MinutesContent Parse(string json)
    {
        using var d = JsonDocument.Parse(json);
        var sections = d.RootElement.TryGetProperty("sections", out var s) ? s.Deserialize<List<ContentSection>>(Options) ?? [] : [];
        var items = d.RootElement.TryGetProperty("items", out var i) ? i.Deserialize<List<ContentItem>>(Options) ?? [] : [];
        return new MinutesContent(sections, items);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

public sealed record TemplateSection(string Key, string TitleAr, string? TitleEn, string Kind, string? Source, string? DefaultBody);

public static class TemplateSources
{
    public static readonly string[] All = ["meeting_info", "attendance", "quorum", "agenda", "discussions", "decisions", "votes", "reservations", "tasks", "attachments", "approval"];
}

// ---------------- اللقطة الآلية ----------------

public sealed record SnapMeeting(Guid Id, string? ReferenceNo, string TitleAr, string BodyName, string BodyCode, string BodyReference, string MeetingType, string Mode,
    DateTime StartsAt, DateTime? ActualStartedAt, DateTime? ActualEndedAt, string? Location, string? OnlineLink, string? ChairName, string? SecretaryName,
    string? PreviousReference, string Classification);

public sealed record SnapAttendee(Guid UserId, string Name, string Role, string? Status, string? Mode, string? ProxyHolder, string? Note, DateTime? LeftAt);
public sealed record SnapGuest(string Name, string Response, string? ItemTitle);
public sealed record SnapQuorum(string Context, int Eligible, int Counted, int Required, bool IsMet, bool ChairPresent, string? OverrideReason, DateTime CheckedAt);
public sealed record SnapItem(Guid Id, int Position, string TitleAr, string? Presenter, string? Owner, string ItemType, string Status, string? StatusReason,
    string? Discussion, string? Recommendation);
public sealed record SnapVote(string Method, string Result, int For, int Against, int Abstain, int Eligible, bool ChairCasting, int Round);
public sealed record SnapResolution(Guid Id, int Position, string Type, string Category, string TitleAr, string TextAr, string? TextEn, Guid? AgendaItemId,
    int? AgendaPosition, string Status, string? Number, string? AuthorityText, string? TargetBody, string? Responsible, string? Supersedes, string? SupersedeKind,
    string AdoptionMethod, SnapVote? Vote);
public sealed record SnapReservation(string Name, string ResolutionTitle, string Kind, string Text);
public sealed record SnapTask(Guid Id, string TitleAr, string? ReferenceNo, string? Assignee, DateOnly? DueDate, string? ResolutionTitle, string Status);
public sealed record SnapAttachment(string FileName, string OwnerType, string Purpose, string Sha256);

public sealed record MinutesSnapshot(SnapMeeting Meeting, List<SnapAttendee> Attendance, List<SnapGuest> Guests, SnapQuorum? Quorum, List<SnapItem> Agenda,
    List<SnapResolution> Resolutions, List<SnapReservation> Reservations, List<SnapTask> Tasks, List<SnapAttachment> Attachments)
{
    public string ToJson() => JsonSerializer.Serialize(this, MinutesContent.Options);
    public static MinutesSnapshot? Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) || json == "{}" ? null : JsonSerializer.Deserialize<MinutesSnapshot>(json, MinutesContent.Options);
}

/// <summary>يجمع البيانات الآلية للمحضر من سجلات الاجتماع (قراءة فقط). تُجمَّد في كل إصدار وتدخل في بصمته.</summary>
internal static class SnapshotBuilder
{
    public static async Task<MinutesSnapshot> BuildAsync(ISql sql, Guid minutesId, MinutesContent content, CancellationToken ct)
    {
        var meeting = await sql.QuerySingleAsync("""
            SELECT m.id, m.reference_no, m.title_ar, b.name_ar, b.code, b.reference_no AS body_ref, m.meeting_type, m.mode, m.starts_at, m.actual_started_at, m.actual_ended_at,
                   m.location, m.online_link,
                   COALESCE(uc.display_name_ar, (SELECT u.display_name_ar FROM bodies.body_members bm JOIN iam.users u ON u.id = bm.user_id
                       WHERE bm.body_id = b.id AND bm.role = 'chair' AND bm.start_date <= (m.starts_at AT TIME ZONE 'Asia/Riyadh')::date
                         AND (bm.end_date IS NULL OR bm.end_date >= (m.starts_at AT TIME ZONE 'Asia/Riyadh')::date) ORDER BY bm.start_date DESC LIMIT 1)) AS chair_name,
                   COALESCE(us.display_name_ar, (SELECT u.display_name_ar FROM bodies.body_members bm JOIN iam.users u ON u.id = bm.user_id
                       WHERE bm.body_id = b.id AND bm.role = 'secretary' AND bm.start_date <= (m.starts_at AT TIME ZONE 'Asia/Riyadh')::date
                         AND (bm.end_date IS NULL OR bm.end_date >= (m.starts_at AT TIME ZONE 'Asia/Riyadh')::date) ORDER BY bm.start_date DESC LIMIT 1)) AS secretary_name,
                   pm.reference_no AS previous_ref, mi.classification
            FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id JOIN bodies.bodies b ON b.id = m.body_id
            LEFT JOIN iam.users uc ON uc.id = m.chair_user_id LEFT JOIN iam.users us ON us.id = m.secretary_user_id
            LEFT JOIN meetings.meetings pm ON pm.id = m.previous_meeting_id
            WHERE mi.id = @minutesId
            """, new { minutesId }, r => new SnapMeeting(r.GetGuid(0), r.Str("reference_no"), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.GetString(6), r.GetString(7), r.GetDateTime(8), r.Time("actual_started_at"), r.Time("actual_ended_at"), r.Str("location"), r.Str("online_link"),
            r.Str("chair_name"), r.Str("secretary_name"), r.Str("previous_ref"), r.GetString(16)), ct)
            ?? throw new InvalidOperationException("المحضر غير موجود");
        var meetingId = meeting.Id;

        var attendance = await sql.QueryAsync("""
            SELECT DISTINCT ON (bm.user_id) bm.user_id, u.display_name_ar, bm.role, a.status, a.attendance_mode, ph.display_name_ar AS proxy_name, a.note, a.left_at
            FROM meetings.meetings m JOIN bodies.body_members bm ON bm.body_id = m.body_id
            JOIN iam.users u ON u.id = bm.user_id
            LEFT JOIN meetings.attendance_records a ON a.meeting_id = m.id AND a.user_id = bm.user_id
            LEFT JOIN iam.users ph ON ph.id = a.proxy_holder_user_id
            WHERE m.id = @meetingId AND bm.status <> 'suspended' AND bm.start_date <= (m.starts_at AT TIME ZONE 'Asia/Riyadh')::date
              AND (bm.end_date IS NULL OR bm.end_date >= (m.starts_at AT TIME ZONE 'Asia/Riyadh')::date)
            ORDER BY bm.user_id, bm.start_date DESC
            """, new { meetingId }, r => new SnapAttendee(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Str("status"), r.Str("attendance_mode"), r.Str("proxy_name"),
            r.Str("note"), r.Time("left_at")), ct);
        attendance = [.. attendance.OrderBy(a => Array.IndexOf(RoleOrder, a.Role)).ThenBy(a => a.Name, StringComparer.Ordinal)];

        var guests = await sql.QueryAsync("""
            SELECT u.display_name_ar, i.response, ai.title_ar FROM meetings.invitations i JOIN iam.users u ON u.id = i.user_id
            LEFT JOIN meetings.agenda_items ai ON ai.id = i.agenda_item_id
            WHERE i.meeting_id = @meetingId AND i.invitee_kind = 'guest' ORDER BY u.display_name_ar
            """, new { meetingId }, r => new SnapGuest(r.GetString(0), r.GetString(1), r.Str("title_ar")), ct);

        var quorum = await sql.QuerySingleAsync("""
            SELECT context, eligible_count, counted_present, required_count, is_met, chair_present, override_reason, checked_at
            FROM meetings.quorum_checks WHERE meeting_id = @meetingId AND context IN ('opening','circulation')
            ORDER BY checked_at DESC LIMIT 1
            """, new { meetingId }, r => new SnapQuorum(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetBoolean(4), r.GetBoolean(5),
            r.Str("override_reason"), r.GetDateTime(7)), ct);

        var itemTexts = content.Items.ToDictionary(i => i.AgendaItemId);
        var agenda = (await sql.QueryAsync("""
            SELECT ai.id, ai.position, ai.title_ar, ai.presenter_text, COALESCE(ou.name_ar, ai.owner_text) AS owner, ai.item_type, ai.status, ai.status_reason,
                   ai.discussion_summary, ai.recommendation_text
            FROM meetings.agenda_items ai LEFT JOIN org.org_units ou ON ou.id = ai.owner_org_unit_id
            WHERE ai.meeting_id = @meetingId ORDER BY ai.position
            """, new { meetingId }, r => new SnapItem(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.Str("presenter_text"), r.Str("owner"), r.GetString(5),
            r.GetString(6), r.Str("status_reason"), r.Str("discussion_summary"), r.Str("recommendation_text")), ct))
            .Select(i => itemTexts.TryGetValue(i.Id, out var t) ? i with { Discussion = t.Discussion, Recommendation = t.Recommendation } : i)
            .ToList();

        var resolutions = await sql.QueryAsync("""
            SELECT r.id, r.position, r.resolution_type, r.category, r.title_ar, r.text_ar, r.text_en, r.agenda_item_id, ai.position AS item_pos, r.status, r.number,
                   r.authority_text, COALESCE(tb.name_ar, r.target_authority_text) AS target, COALESCE(ou.name_ar, ee.name_ar, r.responsible_text) AS responsible,
                   COALESCE(sr.number, sr.title_ar) AS supersedes, r.supersede_kind, r.adoption_method,
                   mo.method, mo.result, mo.tally::text AS tally, mo.round
            FROM resolutions.resolutions r
            LEFT JOIN meetings.agenda_items ai ON ai.id = r.agenda_item_id
            LEFT JOIN bodies.bodies tb ON tb.id = r.target_body_id
            LEFT JOIN org.org_units ou ON ou.id = r.responsible_org_unit_id
            LEFT JOIN org.external_entities ee ON ee.id = r.responsible_external_entity_id
            LEFT JOIN resolutions.resolutions sr ON sr.id = r.supersedes_resolution_id
            LEFT JOIN LATERAL (SELECT method, result, tally, round FROM deliberation.motions m WHERE m.resolution_id = r.id AND m.status = 'closed' ORDER BY round DESC LIMIT 1) mo ON true
            WHERE r.minutes_id = @minutesId AND r.status <> 'withdrawn'
            ORDER BY r.position, r.created_at
            """, new { minutesId }, r =>
        {
            SnapVote? vote = null;
            if (r.Str("tally") is { } t)
            {
                using var d = JsonDocument.Parse(t);
                var e = d.RootElement;
                int I(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                vote = new SnapVote(r.GetString(17), r.GetString(18), I("for"), I("against"), I("abstain"), I("eligible"),
                    e.TryGetProperty("chairCasting", out var cc) && cc.ValueKind == JsonValueKind.True, r.GetInt32(20));
            }
            return new SnapResolution(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.Str("text_en"),
                r.Uuid("agenda_item_id"), r.IsDBNull(8) ? null : r.GetInt32(8), r.GetString(9), r.Str("number"), r.Str("authority_text"), r.Str("target"),
                r.Str("responsible"), r.Str("supersedes"), r.Str("supersede_kind"), r.GetString(16), vote);
        }, ct);

        var reservations = await sql.QueryAsync("""
            SELECT u.display_name_ar, r.title_ar, rv.kind, rv.text FROM deliberation.reservations rv JOIN iam.users u ON u.id = rv.user_id
            JOIN resolutions.resolutions r ON r.id = rv.resolution_id
            WHERE r.minutes_id = @minutesId AND r.status <> 'withdrawn' ORDER BY rv.created_at
            """, new { minutesId }, r => new SnapReservation(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct);

        var tasks = await sql.QueryAsync("""
            SELECT t.id, t.title_ar, t.reference_no, COALESCE(u.display_name_ar, o.name_ar, e.name_ar) AS assignee, t.due_date, r.title_ar AS res_title, t.status
            FROM tasks.tasks t JOIN resolutions.resolutions r ON r.id = t.resolution_id
            LEFT JOIN iam.users u ON u.id = t.assignee_user_id LEFT JOIN org.org_units o ON o.id = t.assignee_org_unit_id
            LEFT JOIN org.external_entities e ON e.id = t.assignee_external_entity_id
            WHERE r.minutes_id = @minutesId AND r.status <> 'withdrawn' AND t.status <> 'cancelled'
            ORDER BY r.position, t.created_at
            """, new { minutesId }, r => new SnapTask(r.GetGuid(0), r.GetString(1), r.Str("reference_no"), r.Str("assignee"), r.Date("due_date"), r.Str("res_title"), r.GetString(6)), ct);

        var attachments = await sql.QueryAsync("""
            SELECT d.file_name, l.owner_type, l.purpose, d.sha256 FROM docs.document_links l JOIN docs.documents d ON d.id = l.document_id
            WHERE d.deleted_at IS NULL AND l.purpose NOT IN ('minutes_pdf','wet_signature','approval_evidence','signed_pdf') AND (
              (l.owner_type = 'meeting' AND l.owner_id = @meetingId)
              OR (l.owner_type = 'agenda_item' AND l.owner_id IN (SELECT id FROM meetings.agenda_items WHERE meeting_id = @meetingId))
              OR (l.owner_type = 'minutes' AND l.owner_id = @minutesId)
              OR (l.owner_type = 'resolution' AND l.owner_id IN (SELECT id FROM resolutions.resolutions WHERE minutes_id = @minutesId)))
            ORDER BY d.uploaded_at, d.id
            """, new { meetingId, minutesId }, r => new SnapAttachment(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct);

        return new MinutesSnapshot(meeting, attendance, guests, quorum, agenda, resolutions, reservations, tasks, attachments);
    }

    private static readonly string[] RoleOrder = ["chair", "vice_chair", "member", "alternate", "secretary", "observer"];
}

// ---------------- إخراج PDF ----------------

public sealed record ApprovalLine(string Step, string Decision, string Actor, string? Capacity, string? OnBehalfOf, string? Method, string? Note,
    string? ExternalApprover, string? ExternalReference, DateOnly? ExternalDate, DateTime At, int? VersionNo);
public sealed record SignoffLine(string Name, string Role, string Method, string Status, string? Reservation, DateTime? SignedAt);

public sealed record MinutesPdfModel(MinutesSnapshot Snapshot, MinutesContent Content, IReadOnlyList<TemplateSection> Template, int VersionNo, string? ContentHash,
    string Status, string? VerificationCode, int AmendmentNo, string? AmendmentReason, IReadOnlyList<ApprovalLine> Approvals, IReadOnlyList<SignoffLine> Signoffs,
    bool Draft);

internal static class MinutesPdf
{
    private static readonly CultureInfo Ar = new("ar-SA") { DateTimeFormat = { Calendar = new GregorianCalendar() } };
    private static readonly TimeZoneInfo Riyadh = TryZone();

    private static TimeZoneInfo TryZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("AST", TimeSpan.FromHours(3), "AST", "AST"); }
    }

    public static string Date(DateTime utc, bool time = true)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Riyadh);
        var hijri = new UmAlQuraCalendar();
        var h = $"{hijri.GetDayOfMonth(local)}/{hijri.GetMonth(local)}/{hijri.GetYear(local)}هـ";
        return local.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) + $"م ({h})" + (time ? " " + local.ToString("HH:mm", CultureInfo.InvariantCulture) : "");
    }

    public static readonly Dictionary<string, string> Labels = new()
    {
        ["chair"] = "الرئيس", ["vice_chair"] = "نائب الرئيس", ["secretary"] = "أمين السر", ["member"] = "عضو", ["alternate"] = "عضو بديل", ["observer"] = "مراقب",
        ["present"] = "حاضر", ["absent"] = "غائب", ["excused"] = "معتذر", ["proxy"] = "أناب", ["in_person"] = "حضوري", ["remote"] = "عن بعد",
        ["regular"] = "دوري", ["extraordinary"] = "استثنائي", ["urgent"] = "عاجل", ["circulation"] = "بالتمرير", ["hybrid"] = "مدمج",
        ["decision"] = "قرار", ["recommendation"] = "توصية", ["issued"] = "صادر", ["proposed"] = "مقترح", ["rejected"] = "مرفوض", ["deferred"] = "مؤجل",
        ["referred"] = "مرفوع لجهة أعلى", ["in_execution"] = "قيد التنفيذ", ["executed"] = "منفذ", ["closed"] = "مغلق", ["cancelled"] = "ملغى", ["superseded"] = "ملغى بقرار لاحق",
        ["adopted"] = "معتمد", ["tied_unresolved"] = "تعادل دون ترجيح", ["no_quorum"] = "لم يكتمل النصاب",
        ["consensus"] = "بالتوافق", ["show_of_hands"] = "برفع الأيدي", ["electronic"] = "إلكتروني", ["secret"] = "سري", ["vote"] = "بالتصويت", ["chair_decision"] = "بقرار الرئيس",
        ["pending"] = "قيد الانتظار", ["discussed"] = "نوقش", ["withdrawn"] = "مسحوب", ["for_decision"] = "للقرار", ["for_information"] = "للعلم", ["for_discussion"] = "للنقاش",
        ["accepted"] = "قبل", ["declined"] = "اعتذر", ["tentative"] = "مبدئي",
        ["administrative"] = "إداري", ["financial"] = "مالي", ["technical"] = "تقني", ["policy"] = "سياسات", ["organizational"] = "تنظيمي", ["procedural"] = "إجرائي", ["other"] = "أخرى",
        ["draft"] = "مسودة", ["assigned"] = "مسندة", ["in_progress"] = "قيد التنفيذ", ["completed"] = "مكتملة",
        ["secretary_review"] = "مراجعة أمين السر", ["chair_review"] = "مراجعة الرئيس", ["member_review"] = "ملاحظات الأعضاء",
        ["pending_signatures"] = "انتظار التوقيع", ["pending_final_approval"] = "انتظار الاعتماد النهائي", ["approved"] = "معتمد", ["returned"] = "معاد للتعديل",
        ["submitted"] = "رُفع للمراجعة", ["step_approved"] = "أُقرت المرحلة", ["amendment_opened"] = "فُتح التعديل", ["signatures_completed"] = "اكتملت التوقيعات",
        ["in_system"] = "اعتماد داخل النظام", ["recorded_external"] = "اعتماد خارج النظام مسجل بمرجع", ["attestation"] = "إقرار إلكتروني داخل النظام",
        ["wet_signature"] = "توقيع ورقي مرفوع", ["electronic_signature"] = "توقيع إلكتروني", ["valid"] = "مكتمل", ["requested"] = "مطلوب", ["invalidated"] = "أُبطل",
        ["amends"] = "يعدّل", ["revokes"] = "يلغي", ["chair_delegate"] = "بالتفويض عن الرئيس", ["editor"] = "معد المحضر", ["governance_officer"] = "مسؤول المجالس",
    };

    private static string L(string? key) => key is null ? "—" : Labels.GetValueOrDefault(key, key);

    public static byte[] Render(MinutesPdfModel m)
    {
        var s = m.Snapshot;
        var footer = m.Draft
            ? $"مسودة غير معتمدة — الإصدار {m.VersionNo}" + (m.ContentHash is { } dh ? $" — البصمة {dh[..16]}" : "")
            : $"رمز التحقق: {m.VerificationCode} — الإصدار المعتمد {m.VersionNo} — بصمة المحتوى SHA-256: {m.ContentHash}";
        var pdf = new PdfReport(new PdfReportOptions
        {
            Title = $"محضر {s.Meeting.ReferenceNo} — {s.Meeting.TitleAr}",
            HeaderLine1 = "المكتب الاستراتيجي لتطوير منطقة الجوف",
            HeaderLine2 = $"{s.Meeting.BodyName} — محضر الاجتماع {s.Meeting.ReferenceNo}",
            FooterText = footer,
            Watermark = m.Draft ? "مسودة — غير معتمدة" : null,
            Author = s.Meeting.BodyName,
        });
        pdf.Title($"محضر {s.Meeting.TitleAr}");
        if (m.AmendmentNo > 0)
            pdf.Paragraph($"محضر معدّل (التعديل رقم {m.AmendmentNo}): {m.AmendmentReason}", 9.5, true, PdfColor.Hex("#8A5A00"));

        var texts = m.Content.Sections.ToDictionary(x => x.Key, x => x);
        var used = new HashSet<string>();
        foreach (var sec in m.Template)
        {
            used.Add(sec.Key);
            if (sec.Kind == "text")
            {
                var body = texts.TryGetValue(sec.Key, out var t) ? t.Body : sec.DefaultBody;
                if (string.IsNullOrWhiteSpace(body)) continue;
                pdf.Heading(texts.TryGetValue(sec.Key, out var tt) && !string.IsNullOrWhiteSpace(tt.Title) ? tt.Title : sec.TitleAr);
                pdf.Paragraph(body);
                continue;
            }
            RenderAuto(pdf, sec, m);
        }
        // أقسام نصية خارج القالب (مثل محاضر سابقة لاعتماد القوالب)
        foreach (var extra in m.Content.Sections.Where(x => !used.Contains(x.Key) && !string.IsNullOrWhiteSpace(x.Body)))
        {
            pdf.Heading(extra.Title);
            pdf.Paragraph(extra.Body);
        }
        return pdf.Build();
    }

    private static void RenderAuto(PdfReport pdf, TemplateSection sec, MinutesPdfModel m)
    {
        var s = m.Snapshot;
        switch (sec.Source)
        {
            case "meeting_info":
                pdf.Heading(sec.TitleAr);
                pdf.KeyValues([
                    ("رقم الاجتماع", s.Meeting.ReferenceNo), ("الجهة", $"{s.Meeting.BodyName} ({s.Meeting.BodyReference})"),
                    ("نوع الاجتماع", $"{L(s.Meeting.MeetingType)} — {L(s.Meeting.Mode)}"),
                    ("الموعد", Date(s.Meeting.StartsAt)),
                    ("الانعقاد الفعلي", s.Meeting.ActualStartedAt is { } a ? Date(a) + (s.Meeting.ActualEndedAt is { } e ? " — حتى " + Date(e) : "") : null),
                    ("المكان", s.Meeting.Location ?? (s.Meeting.OnlineLink is null ? null : "اجتماع مرئي")),
                    ("رئيس الاجتماع", s.Meeting.ChairName), ("أمين السر", s.Meeting.SecretaryName),
                    ("الاجتماع السابق", s.Meeting.PreviousReference),
                ]);
                break;
            case "attendance":
                pdf.Heading(sec.TitleAr);
                pdf.Table(["الاسم", "الصفة", "الحضور", "ملاحظة"], [0.34, 0.18, 0.18, 0.30],
                    s.Attendance.Select(a => (IReadOnlyList<string?>)[a.Name, L(a.Role),
                        a.Status is null ? "غير مسجل" : a.Status == "present" ? $"{L("present")} ({L(a.Mode)})" : L(a.Status),
                        a.Status == "proxy" ? $"أناب {a.ProxyHolder}" : a.LeftAt is not null ? "غادر أثناء الاجتماع" : a.Note]).ToList());
                if (s.Guests.Count > 0)
                    pdf.Table(["الضيف", "الرد على الدعوة", "البند"], [0.4, 0.2, 0.4], s.Guests.Select(g => (IReadOnlyList<string?>)[g.Name, L(g.Response), g.ItemTitle]).ToList());
                break;
            case "quorum":
                pdf.Heading(sec.TitleAr);
                if (s.Quorum is null) { pdf.Paragraph("لم يُسجل فحص نصاب.", 9.5, false, PdfColor.Gray); break; }
                pdf.Paragraph($"{(s.Quorum.IsMet ? "اكتمل النصاب" : "لم يكتمل النصاب")}: الحاضرون المحتسبون {s.Quorum.Counted} من {s.Quorum.Eligible} عضوًا مصوتًا، والمطلوب {s.Quorum.Required}"
                    + (s.Quorum.ChairPresent ? "، بحضور الرئيس." : ".") + (s.Quorum.OverrideReason is { } o ? $" (افتُتح بتجاوز موثق: {o})" : ""));
                break;
            case "agenda":
                pdf.Heading(sec.TitleAr);
                pdf.Table(["#", "البند", "مقدمه / الجهة المالكة", "الحالة"], [0.06, 0.5, 0.26, 0.18],
                    s.Agenda.Select(i => (IReadOnlyList<string?>)[i.Position.ToString(CultureInfo.InvariantCulture), i.TitleAr,
                        string.Join(" — ", new[] { i.Presenter, i.Owner }.Where(x => !string.IsNullOrWhiteSpace(x))), L(i.Status) + (i.StatusReason is { } r ? $" ({r})" : "")]).ToList());
                break;
            case "discussions":
                pdf.Heading(sec.TitleAr);
                foreach (var i in s.Agenda.Where(i => i.Status is not "withdrawn"))
                {
                    pdf.Paragraph($"البند {i.Position}: {i.TitleAr}", 10, true, PdfColor.Hex("#006837"));
                    pdf.Paragraph(string.IsNullOrWhiteSpace(i.Discussion) ? (i.Status == "deferred" ? "أُجّل البند." : "لا يوجد ملخص مناقشة.") : i.Discussion, 10);
                    if (!string.IsNullOrWhiteSpace(i.Recommendation)) pdf.Paragraph("التوصية: " + i.Recommendation, 10);
                }
                break;
            case "decisions":
                pdf.Heading(sec.TitleAr);
                if (s.Resolutions.Count == 0) { pdf.Paragraph("لا توجد قرارات أو توصيات.", 9.5, false, PdfColor.Gray); break; }
                foreach (var r in s.Resolutions)
                {
                    var head = $"{(r.Number is { } n ? n + " — " : $"({r.Position}) ")}{L(r.Type)}: {r.TitleAr}";
                    pdf.Paragraph(head, 10.5, true, PdfColor.Hex("#553D26"));
                    pdf.Paragraph(r.TextAr);
                    var meta = new List<(string, string?)>
                    {
                        ("البند", r.AgendaPosition is { } p ? $"البند {p}" : null), ("التصنيف", L(r.Category)), ("طريقة الاعتماد", L(r.AdoptionMethod)),
                        ("الحالة", L(r.Status)), ("صاحب الصلاحية", r.AuthorityText), ("الجهة الأعلى المطلوب اعتمادها", r.TargetBody),
                        ("الجهة المسؤولة عن التنفيذ", r.Responsible), (r.SupersedeKind is null ? "" : $"{L(r.SupersedeKind)} القرار", r.Supersedes),
                    }.Where(x => !string.IsNullOrWhiteSpace(x.Item2) && x.Item1.Length > 0).ToList();
                    if (meta.Count > 0) pdf.KeyValues(meta, 0.32);
                }
                break;
            case "votes":
                var voted = s.Resolutions.Where(r => r.Vote is not null).ToList();
                pdf.Heading(sec.TitleAr);
                if (voted.Count == 0) { pdf.Paragraph("لم يُجرَ تصويت؛ اعتُمدت القرارات بالتوافق.", 9.5, false, PdfColor.Gray); break; }
                pdf.Table(["القرار", "الطريقة", "مع", "ضد", "ممتنع", "النتيجة"], [0.36, 0.14, 0.08, 0.08, 0.1, 0.24],
                    voted.Select(r => (IReadOnlyList<string?>)[r.TitleAr, L(r.Vote!.Method), r.Vote.For.ToString(CultureInfo.InvariantCulture), r.Vote.Against.ToString(CultureInfo.InvariantCulture),
                        r.Vote.Abstain.ToString(CultureInfo.InvariantCulture), L(r.Vote.Result) + (r.Vote.ChairCasting ? " (بصوت الرئيس المرجح)" : "")]).ToList());
                break;
            case "reservations":
                var res = s.Reservations.Select(r => (r.Name, $"على «{r.ResolutionTitle}»: {r.Text}"))
                    .Concat(m.Signoffs.Where(x => !string.IsNullOrWhiteSpace(x.Reservation)).Select(x => (x.Name, $"عند الإقرار: {x.Reservation}"))).ToList();
                pdf.Heading(sec.TitleAr);
                if (res.Count == 0) { pdf.Paragraph("لا توجد تحفظات.", 9.5, false, PdfColor.Gray); break; }
                pdf.Table(["العضو", "التحفظ"], [0.28, 0.72], res.Select(x => (IReadOnlyList<string?>)[x.Name, x.Item2]).ToList());
                break;
            case "tasks":
                pdf.Heading(sec.TitleAr);
                if (s.Tasks.Count == 0) { pdf.Paragraph("لا توجد مهام ناتجة.", 9.5, false, PdfColor.Gray); break; }
                pdf.Table(["المهمة", "المكلف", "الموعد", "القرار"], [0.36, 0.22, 0.14, 0.28],
                    s.Tasks.Select(t => (IReadOnlyList<string?>)[(t.ReferenceNo is { } rn ? rn + " — " : "") + t.TitleAr, t.Assignee,
                        t.DueDate?.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), t.ResolutionTitle]).ToList());
                break;
            case "attachments":
                pdf.Heading(sec.TitleAr);
                if (s.Attachments.Count == 0) { pdf.Paragraph("لا توجد مرفقات.", 9.5, false, PdfColor.Gray); break; }
                pdf.Table(["الملف", "مرتبط بـ", "البصمة"], [0.45, 0.17, 0.38],
                    s.Attachments.Select(a => (IReadOnlyList<string?>)[a.FileName, a.OwnerType switch { "meeting" => "الاجتماع", "agenda_item" => "بند", "minutes" => "المحضر", "resolution" => "قرار", _ => a.OwnerType },
                        a.Sha256[..16] + "…"]).ToList());
                break;
            case "approval":
                pdf.Heading(sec.TitleAr);
                if (m.Signoffs.Count > 0)
                    pdf.Table(["الاسم", "الصفة", "الطريقة", "الحالة", "التاريخ"], [0.26, 0.14, 0.24, 0.12, 0.24],
                        m.Signoffs.Select(x => (IReadOnlyList<string?>)[x.Name, L(x.Role), L(x.Method), L(x.Status), x.SignedAt is { } d ? Date(d) : null]).ToList());
                var steps = m.Approvals.Where(a => a.Decision is not "submitted" || a.VersionNo == m.VersionNo).ToList();
                if (steps.Count == 0) { pdf.Paragraph("لم يُعتمد بعد.", 9.5, false, PdfColor.Gray); break; }
                pdf.Table(["المرحلة", "الإجراء", "بواسطة", "الطريقة", "التاريخ"], [0.2, 0.18, 0.28, 0.16, 0.18],
                    steps.Select(a => (IReadOnlyList<string?>)[L(a.Step), L(a.Decision),
                        a.Actor + (a.OnBehalfOf is { } ob ? $" (عن {ob})" : "") + (a.ExternalApprover is { } ex ? $" — المعتمد: {ex}" : "") + (a.ExternalReference is { } er ? $" — المرجع {er}" : ""),
                        L(a.Method), Date(a.At)]).ToList());
                break;
        }
    }
}
