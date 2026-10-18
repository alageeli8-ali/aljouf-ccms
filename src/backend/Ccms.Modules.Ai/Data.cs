using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;

namespace Ccms.Modules.Ai;

internal static class Labels
{
    public static string Resolution(string s) => s switch
    {
        "proposed" => "مقترح", "issued" => "صادر", "in_execution" => "قيد التنفيذ", "executed" => "منفذ", "closed" => "مغلق", "referred" => "مرفوع لجهة أعلى",
        "deferred" => "مؤجل", "rejected" => "مرفوض", "cancelled" => "ملغى", "superseded" => "معدّل بقرار لاحق", "withdrawn" => "مسحوب", _ => s,
    };

    public static string Task(string s) => s switch
    {
        "new" => "جديدة", "assigned" => "مسندة", "accepted" => "مقبولة", "in_progress" => "قيد التنفيذ", "on_hold" => "متوقفة", "pending_review" => "بانتظار المراجعة",
        "completed" => "مكتملة", "rejected" => "مرفوضة", "cancelled" => "ملغاة", _ => s,
    };

    public static string Minutes(string s) => s switch
    {
        "draft" => "مسودة", "secretary_review" => "مراجعة أمين السر", "chair_review" => "مراجعة الرئيس", "member_review" => "ملاحظات الأعضاء",
        "pending_signatures" => "بانتظار التوقيع", "pending_final_approval" => "بانتظار الاعتماد النهائي", "approved" => "معتمد", "returned" => "معاد للتعديل", _ => s,
    };

    public static string Date(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

internal sealed record MinutesCtx(Guid Id, Guid MeetingId, Guid BodyId, string BodyName, string BodyClassification, string Title, string? Reference, string Status,
    string Classification, DateOnly MeetingDate, List<(string Key, string Title, string Body)> Sections, List<AgendaRow> Agenda, List<ResolutionRow> Resolutions);
internal sealed record AgendaRow(Guid Id, int Position, string Title, string Classification, string? Discussion, string? Recommendation, string Status);
internal sealed record ResolutionRow(Guid Id, string? Number, string Title, string Text, string Status, string Classification, string Type);

/// <summary>قراءة البيانات المصدر لكل وظيفة من الوحدات الأساسية (قراءة فقط) — بعد تحقق الوحدة المستدعية من صلاحية المستخدم على السجل.</summary>
internal sealed class AiData(Db db, IAccessService access, ISearchService search, IClock clock)
{
    private const string Today = "(now() AT TIME ZONE 'Asia/Riyadh')::date";

    public static string Max(string a, string b) => DataPolicy.Rank(a) >= DataPolicy.Rank(b) ? a : b;

    public async Task<MinutesCtx> MinutesAsync(Guid id, CancellationToken ct)
    {
        var m = await db.QuerySingleAsync("""
            SELECT mi.id, mi.meeting_id, m.body_id, b.name_ar, b.classification, m.title_ar, m.reference_no, mi.status, mi.classification,
                   (m.starts_at AT TIME ZONE 'Asia/Riyadh')::date AS d,
                   CASE WHEN mi.status = 'draft' OR mi.current_version_no = 0 THEN mi.draft_content::text
                        ELSE (SELECT v.content::text FROM minutes.minutes_versions v WHERE v.minutes_id = mi.id AND v.version_no = mi.current_version_no) END AS content
            FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id JOIN bodies.bodies b ON b.id = m.body_id WHERE mi.id = @id
            """, new { id }, r => new
        {
            Id = r.GetGuid(0), Meeting = r.GetGuid(1), Body = r.GetGuid(2), BodyName = r.GetString(3), BodyCls = r.GetString(4), Title = r.GetString(5), Ref = r.Str("reference_no"),
            Status = r.GetString(7), Cls = r.GetString(8), Date = r.GetFieldValue<DateOnly>(9), Content = r.Str("content") ?? "{}",
        }, ct) ?? throw new NotFoundException();
        using var doc = JsonDocument.Parse(m.Content);
        var sections = doc.RootElement.TryGetProperty("sections", out var s)
            ? s.EnumerateArray().Select(x => (x.GetProperty("key").GetString() ?? "", x.GetProperty("title").GetString() ?? "", x.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "")).ToList()
            : [];
        var itemsText = doc.RootElement.TryGetProperty("items", out var it)
            ? it.EnumerateArray().ToDictionary(x => x.GetProperty("agendaItemId").GetGuid(),
                x => (D: x.TryGetProperty("discussion", out var d) ? d.GetString() : null, R: x.TryGetProperty("recommendation", out var rr) ? rr.GetString() : null))
            : [];
        var agenda = await db.QueryAsync("""
            SELECT id, position, title_ar, classification, status FROM meetings.agenda_items WHERE meeting_id = @mid ORDER BY position
            """, new { mid = m.Meeting }, r =>
        {
            var aid = r.GetGuid(0);
            var t = itemsText.GetValueOrDefault(aid);
            return new AgendaRow(aid, r.GetInt32(1), r.GetString(2), r.GetString(3), t.D, t.R, r.GetString(4));
        }, ct);
        var resolutions = await db.QueryAsync("""
            SELECT id, number, title_ar, text_ar, status, classification, resolution_type FROM resolutions.resolutions
            WHERE minutes_id = @id AND status NOT IN ('withdrawn') ORDER BY position, created_at
            """, new { id }, r => new ResolutionRow(r.GetGuid(0), r.Str("number"), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)), ct);
        return new MinutesCtx(m.Id, m.Meeting, m.Body, m.BodyName, m.BodyCls, m.Title, m.Ref, m.Status, m.Cls, m.Date, sections, agenda, resolutions);
    }

    /// <summary>مصادر المحضر: الأقسام النصية، وملخص كل بند، والقرارات — بمراجع ثابتة.</summary>
    public static List<AiSource> MinutesSources(MinutesCtx m, bool withResolutions = true)
    {
        var list = new List<AiSource>();
        var n = 0;
        foreach (var (key, title, body) in m.Sections.Where(x => !string.IsNullOrWhiteSpace(x.Body)))
            list.Add(new AiSource($"S{++n}", "section", title, body, Max(m.Classification, m.BodyClassification), "minutes", m.Id, $"/minutes/{m.Id}", $"{m.Reference ?? m.Title} — {title}", m.BodyId));
        foreach (var a in m.Agenda.Where(a => !string.IsNullOrWhiteSpace(a.Discussion) || !string.IsNullOrWhiteSpace(a.Recommendation)))
            list.Add(new AiSource($"S{++n}", "item", $"البند {a.Position}: {a.Title}", string.Join("\n", new[] { a.Discussion, a.Recommendation }.Where(x => !string.IsNullOrWhiteSpace(x))),
                Max(Max(m.Classification, a.Classification), m.BodyClassification), "minutes", m.Id, $"/minutes/{m.Id}", $"{m.Reference ?? m.Title} — البند {a.Position}", m.BodyId,
                new JsonObject { ["agendaRef"] = $"A{a.Position}" }));
        if (withResolutions)
        {
            var r = 0;
            foreach (var res in m.Resolutions)
                list.Add(new AiSource($"R{++r}", "resolution", res.Title, res.Text, Max(res.Classification, m.BodyClassification), "resolution", res.Id, $"/resolutions/{res.Id}",
                    res.Number ?? $"{(res.Type == "decision" ? "قرار" : "توصية")} مقترح: {ArText.Shorten(res.Title, 60)}", m.BodyId));
        }
        return list;
    }

    public static Dictionary<string, (Guid, string)> AgendaMap(MinutesCtx m) => m.Agenda.ToDictionary(a => $"A{a.Position}", a => (a.Id, a.Title));

    public async Task<List<DirectoryEntry>> DirectoryAsync(Guid bodyId, CancellationToken ct) =>
        await db.QueryAsync("""
            SELECT 'member' AS t, u.id, u.display_name_ar FROM bodies.body_members bm JOIN iam.users u ON u.id = bm.user_id
              WHERE bm.body_id = @bodyId AND bm.status = 'active'
            UNION ALL SELECT 'org_unit', o.id, o.name_ar FROM org.org_units o WHERE o.status = 'active'
            UNION ALL SELECT 'external_entity', e.id, e.name_ar FROM org.external_entities e WHERE e.status = 'active'
            """, new { bodyId }, r => new DirectoryEntry(r.GetString(0), r.GetGuid(1), r.GetString(2)), ct);

    /// <summary>المفتوح والمتأخر والمؤجل في الجهة: مدخلات مسودة جدول الأعمال.</summary>
    public async Task<(List<AiSource> Sources, string BodyName)> OpenItemsAsync(Guid bodyId, CancellationToken ct)
    {
        var body = await db.QuerySingleAsync("SELECT name_ar, classification FROM bodies.bodies WHERE id = @bodyId", new { bodyId }, r => (Name: r.GetString(0), Cls: r.GetString(1)), ct);
        var list = new List<AiSource>();
        var n = 0;
        foreach (var r in await db.QueryAsync($"""
            SELECT r.id, r.number, r.title_ar, r.status, r.issued_at, r.classification,
                   (SELECT count(*) FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status WHERE t.resolution_id = r.id AND ws.category IN ('open','active','waiting','review')) AS open_tasks,
                   (SELECT count(*) FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status WHERE t.resolution_id = r.id AND ws.category IN ('open','active','waiting','review') AND t.due_date < {Today}) AS late
            FROM resolutions.resolutions r WHERE r.body_id = @bodyId AND r.status IN ('issued','in_execution')
            ORDER BY late DESC, r.issued_at NULLS LAST LIMIT 15
            """, new { bodyId }, x => (Id: x.GetGuid(0), No: x.Str("number"), Title: x.GetString(2), Status: x.GetString(3), At: x.Time("issued_at"), Cls: x.GetString(5), Open: x.GetInt64(6), Late: x.GetInt64(7)), ct))
            list.Add(new AiSource($"S{++n}", "open_resolution", r.Title,
                $"القرار {r.No} بحالة «{Labels.Resolution(r.Status)}»{(r.At is { } d ? "، صادر في " + Labels.Date(d) : "")}؛ مهام مفتوحة: {r.Open}، متأخرة: {r.Late}.",
                Max(r.Cls, body.Cls), "resolution", r.Id, $"/resolutions/{r.Id}", r.No ?? r.Title, bodyId));
        foreach (var t in await db.QueryAsync($"""
            SELECT t.id, t.reference_no, t.title_ar, t.due_date, t.status, coalesce(u.display_name_ar, ou.name_ar, e.name_ar, '—'), {Today} - t.due_date
            FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status
            LEFT JOIN iam.users u ON u.id = coalesce(t.executor_user_id, t.assignee_user_id) LEFT JOIN org.org_units ou ON ou.id = t.assignee_org_unit_id
            LEFT JOIN org.external_entities e ON e.id = t.assignee_external_entity_id
            WHERE t.body_id = @bodyId AND ws.category IN ('open','active','waiting','review') AND t.due_date < {Today}
            ORDER BY t.due_date LIMIT 15
            """, new { bodyId }, x => (Id: x.GetGuid(0), Ref: x.Str("reference_no"), Title: x.GetString(2), Due: x.GetFieldValue<DateOnly>(3), Status: x.GetString(4), Who: x.GetString(5), Days: x.GetInt32(6)), ct))
            list.Add(new AiSource($"S{++n}", "overdue_task", t.Title,
                $"المهمة {t.Ref} مستحقة في {Labels.Date(t.Due)} ومتأخرة {t.Days} يومًا؛ المنفذ: {t.Who}؛ الحالة: {Labels.Task(t.Status)}.",
                body.Cls, "task", t.Id, $"/tasks/{t.Id}", t.Ref ?? t.Title, bodyId));
        foreach (var a in await db.QueryAsync("""
            SELECT a.id, a.title_ar, m.reference_no, m.starts_at, a.classification, m.id FROM meetings.agenda_items a JOIN meetings.meetings m ON m.id = a.meeting_id
            WHERE m.body_id = @bodyId AND a.status = 'deferred' ORDER BY m.starts_at DESC LIMIT 10
            """, new { bodyId }, x => (Id: x.GetGuid(0), Title: x.GetString(1), Ref: x.Str("reference_no"), At: x.GetDateTime(3), Cls: x.GetString(4), Meeting: x.GetGuid(5)), ct))
            list.Add(new AiSource($"S{++n}", "deferred_item", a.Title, $"بند مؤجل من الاجتماع {a.Ref} بتاريخ {Labels.Date(a.At)}.", Max(a.Cls, body.Cls),
                "meeting", a.Meeting, $"/meetings/{a.Meeting}", $"{a.Ref} — {a.Title}", bodyId));
        return (list, body.Name);
    }

    public async Task<List<AiSource>> ResolutionCandidatesAsync(Guid bodyId, CancellationToken ct)
    {
        var bodyCls = await db.ScalarAsync<string>("SELECT classification FROM bodies.bodies WHERE id = @bodyId", new { bodyId }, ct) ?? "restricted";
        var n = 0;
        return (await db.QueryAsync("""
            SELECT id, number, title_ar, text_ar, classification, status FROM resolutions.resolutions
            WHERE body_id = @bodyId AND status IN ('issued','in_execution','referred','proposed') ORDER BY created_at DESC LIMIT 40
            """, new { bodyId }, r => (Id: r.GetGuid(0), No: r.Str("number"), Title: r.GetString(2), Text: r.GetString(3), Cls: r.GetString(4), Status: r.GetString(5)), ct))
            .Select(r => new AiSource($"R{++n}", "resolution", r.Title, r.Text, Max(r.Cls, bodyCls), "resolution", r.Id, $"/resolutions/{r.Id}", r.No ?? "مقترح", bodyId)).ToList();
    }

    /// <summary>الجهات التي يطّلع عليها المستخدم (لتفسير اسم الجهة في السؤال).</summary>
    public async Task<(JsonArray Payload, Dictionary<string, (Guid, string)> Map)> BodiesAsync(CancellationToken ct)
    {
        var f = await access.GetBodyFilterAsync(Permissions.BodyView, ct);
        var rows = await db.QueryAsync("""
            SELECT id, name_ar, code FROM bodies.bodies WHERE status <> 'archived' AND (@all::boolean OR id = ANY(@ids::uuid[])) ORDER BY name_ar
            """, new { all = f.All, ids = f.BodyIds }, r => (Id: r.GetGuid(0), Name: r.GetString(1), Code: r.GetString(2)), ct);
        var arr = new JsonArray();
        var map = new Dictionary<string, (Guid, string)>();
        var i = 0;
        foreach (var b in rows)
        {
            var key = $"B{++i}";
            arr.Add(new JsonObject { ["ref"] = key, ["name"] = b.Name, ["code"] = b.Code });
            map[key] = (b.Id, b.Name);
        }
        return (arr, map);
    }

    public async Task<string> BodyClassificationAsync(Guid bodyId, CancellationToken ct) =>
        await db.ScalarAsync<string>("SELECT classification FROM bodies.bodies WHERE id = @bodyId", new { bodyId }, ct) ?? "restricted";

    // ---------------- الملخص التنفيذي ----------------

    public async Task<List<AiSource>> ExecutiveFactsAsync(Guid? bodyId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var res = await access.GetBodyFilterAsync(Permissions.ResolutionView, ct);
        var tsk = await access.GetBodyFilterAsync(Permissions.TaskView, ct);
        var mtg = await access.GetBodyFilterAsync(Permissions.MeetingView, ct);
        var mins = await access.GetBodyFilterAsync(Permissions.MinutesView, ct);
        var args = new
        {
            body = bodyId, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MaxValue),
            rAll = res.All, rIds = res.BodyIds, tAll = tsk.All, tIds = tsk.BodyIds, mAll = mtg.All, mIds = mtg.BodyIds, miAll = mins.All, miIds = mins.BodyIds,
        };
        var k = await db.QuerySingleAsync($"""
            SELECT
             (SELECT count(*) FROM meetings.meetings m WHERE m.status IN ('concluded','closed') AND m.starts_at BETWEEN @from AND @to AND (@mAll::boolean OR m.body_id = ANY(@mIds::uuid[])) AND (@body::uuid IS NULL OR m.body_id = @body)),
             (SELECT count(*) FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id WHERE mi.approved_at BETWEEN @from AND @to AND (@miAll::boolean OR m.body_id = ANY(@miIds::uuid[])) AND (@body::uuid IS NULL OR m.body_id = @body)),
             (SELECT count(*) FROM resolutions.resolutions r WHERE r.issued_at BETWEEN @from AND @to AND (@rAll::boolean OR r.body_id = ANY(@rIds::uuid[])) AND (@body::uuid IS NULL OR r.body_id = @body)),
             (SELECT count(*) FROM resolutions.resolutions r WHERE r.status IN ('issued','in_execution') AND (@rAll::boolean OR r.body_id = ANY(@rIds::uuid[])) AND (@body::uuid IS NULL OR r.body_id = @body)),
             (SELECT count(*) FROM resolutions.resolutions r WHERE r.status IN ('issued','in_execution') AND (@rAll::boolean OR r.body_id = ANY(@rIds::uuid[])) AND (@body::uuid IS NULL OR r.body_id = @body)
                AND EXISTS (SELECT 1 FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status WHERE t.resolution_id = r.id AND ws.category IN ('open','active','waiting','review') AND t.due_date < {Today})),
             (SELECT count(*) FROM tasks.tasks t WHERE t.status = 'completed' AND t.completed_at BETWEEN @from AND @to AND (@tAll::boolean OR t.body_id = ANY(@tIds::uuid[])) AND (@body::uuid IS NULL OR t.body_id = @body)),
             (SELECT count(*) FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status WHERE ws.category IN ('open','active','waiting','review') AND (@tAll::boolean OR t.body_id = ANY(@tIds::uuid[])) AND (@body::uuid IS NULL OR t.body_id = @body)),
             (SELECT count(*) FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status WHERE ws.category IN ('open','active','waiting','review') AND t.due_date < {Today} AND (@tAll::boolean OR t.body_id = ANY(@tIds::uuid[])) AND (@body::uuid IS NULL OR t.body_id = @body)),
             (SELECT count(*) FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id WHERE mi.status NOT IN ('draft','approved') AND (@miAll::boolean OR m.body_id = ANY(@miIds::uuid[])) AND (@body::uuid IS NULL OR m.body_id = @body))
            """, args, r => Enumerable.Range(0, 9).Select(r.GetInt64).ToArray(), ct) ?? new long[9];
        var period = $"من {Labels.Date(from)} إلى {Labels.Date(to)}";
        var list = new List<AiSource>();
        var f = 0;
        void Metric(string text, string label) => list.Add(new AiSource($"F{++f}", "metric", label, text, "public", "metric", null, "/reports", label, bodyId));
        Metric($"عُقد {k[0]} اجتماعًا خلال الفترة ({period})", "الاجتماعات المنعقدة");
        Metric($"اعتُمد {k[1]} محضرًا خلال الفترة", "المحاضر المعتمدة");
        Metric($"صدر {k[2]} قرارًا خلال الفترة", "القرارات الصادرة");
        Metric($"القرارات المفتوحة حاليًا: {k[3]}، منها {k[4]} متأخرة التنفيذ", "القرارات المفتوحة والمتأخرة");
        var rate = k[5] + k[6] == 0 ? 0 : (int)Math.Round(100.0 * k[5] / (k[5] + k[6]));
        Metric($"أُنجزت {k[5]} مهمة خلال الفترة، وتبقى {k[6]} مهمة مفتوحة منها {k[7]} متأخرة (نسبة الإنجاز {rate}٪)", "المهام");
        Metric($"محاضر قيد المراجعة أو الاعتماد: {k[8]}", "المحاضر قيد الاعتماد");
        foreach (var hit in await search.SearchAsync(new SearchQuery(null, [SearchTypes.Resolution], bodyId, "open", true, Limit: 5), ct))
            list.Add(new AiSource($"F{++f}", "overdue_resolution", hit.Title, $"القرار {hit.Reference} ({hit.BodyName}) متأخر التنفيذ؛ أقرب استحقاق لمهامه المفتوحة {(hit.DueDate is { } d ? Labels.Date(d) : "—")}.",
                hit.Classification, "resolution", hit.Id, hit.Link, hit.Reference ?? hit.Title, hit.BodyId));
        foreach (var hit in await search.SearchAsync(new SearchQuery(null, [SearchTypes.Task], bodyId, "open", true, Limit: 5), ct))
            list.Add(new AiSource($"F{++f}", "overdue_task", hit.Title, $"المهمة {hit.Reference} ({hit.BodyName}) متأخرة؛ استحقاقها {(hit.DueDate is { } d ? Labels.Date(d) : "—")}.",
                hit.Classification, "task", hit.Id, hit.Link, hit.Reference ?? hit.Title, hit.BodyId));
        foreach (var p in await db.QueryAsync("""
            SELECT mi.id, m.reference_no, m.title_ar, mi.status, b.name_ar, mi.classification, m.body_id FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id JOIN bodies.bodies b ON b.id = m.body_id
            WHERE mi.status NOT IN ('draft','approved') AND (@miAll::boolean OR m.body_id = ANY(@miIds::uuid[])) AND (@body::uuid IS NULL OR m.body_id = @body) ORDER BY mi.updated_at LIMIT 5
            """, args, r => (Id: r.GetGuid(0), Ref: r.Str("reference_no"), Title: r.GetString(2), Status: r.GetString(3), Body: r.GetString(4), Cls: r.GetString(5), BodyId: r.GetGuid(6)), ct))
            list.Add(new AiSource($"F{++f}", "pending_minutes", p.Title, $"محضر {p.Ref} ({p.Body}) في مرحلة «{Labels.Minutes(p.Status)}».", p.Cls, "minutes", p.Id, $"/minutes/{p.Id}", p.Ref ?? p.Title, p.BodyId));
        return list;
    }

    // ---------------- الأسئلة ----------------

    public async Task<List<AiSource>> AskFactsAsync(string intent, string? number, Guid? bodyId, string[] keywords, string question, CancellationToken ct)
    {
        var list = new List<AiSource>();
        var f = 0;
        AiSource Hit(SearchHit h, string kind, string text) =>
            new($"F{++f}", kind, h.Title, text, h.Classification, h.Type, h.Id, h.Link, h.Reference ?? h.Title, h.BodyId);
        switch (intent)
        {
            case "overdue_resolutions":
                foreach (var h in await search.SearchAsync(new SearchQuery(keywords.Length > 0 ? string.Join(' ', keywords) : null, [SearchTypes.Resolution], bodyId, "open", true, Limit: 10), ct))
                    list.Add(Hit(h, "overdue_resolution", $"القرار {h.Reference} «{h.Title}» ({h.BodyName}) متأخر التنفيذ؛ أقرب استحقاق لمهامه المفتوحة {(h.DueDate is { } d ? Labels.Date(d) : "—")}."));
                break;
            case "open_tasks":
                foreach (var h in await search.SearchAsync(new SearchQuery(keywords.Length > 0 ? string.Join(' ', keywords) : null, [SearchTypes.Task], bodyId, "open",
                             ArText.Normalize(question).Contains("متاخر"), Limit: 10), ct))
                    list.Add(Hit(h, "task", $"المهمة {h.Reference} «{h.Title}» ({h.BodyName}) بحالة «{Labels.Task(h.Status ?? "")}»؛ الاستحقاق {(h.DueDate is { } d ? Labels.Date(d) : "—")}."));
                break;
            case "last_action":
            case "resolution_status":
                var target = await FindResolutionAsync(number, keywords, bodyId, ct);
                if (target is null) break;
                var r = target;
                if (intent == "resolution_status" || true)
                    list.Add(Hit(r, "resolution", $"القرار {r.Reference} «{r.Title}» ({r.BodyName}) حالته الحالية «{Labels.Resolution(r.Status ?? "")}»."));
                foreach (var e in await TimelineAsync(r.Id, ct))
                    list.Add(new AiSource($"F{++f}", "action", r.Title, e.Text, r.Classification, e.Type, e.Id, e.Link, e.Label, r.BodyId));
                if (intent == "last_action" && list.Count > 1)
                {
                    // آخر إجراء أولًا بعد سطر الحالة
                    var latest = list[1];
                    list[1] = latest with { Text = "آخر إجراء: " + latest.Text };
                }
                break;
            default:
                foreach (var h in await search.SearchAsync(new SearchQuery(question, null, bodyId, Limit: 8), ct))
                    list.Add(Hit(h, h.Type, $"{TypeLabel(h.Type)} {h.Reference ?? ""} «{h.Title}» ({h.BodyName}): {h.Snippet}"));
                break;
        }
        return list;
    }

    private static string TypeLabel(string t) => t switch { SearchTypes.Resolution => "القرار", SearchTypes.Task => "المهمة", SearchTypes.Minutes => "المحضر", _ => "الاجتماع" };

    private async Task<SearchHit?> FindResolutionAsync(string? number, string[] keywords, Guid? bodyId, CancellationToken ct)
    {
        if (number is not null)
        {
            var hits = await search.SearchAsync(new SearchQuery(number, [SearchTypes.Resolution], bodyId, Limit: 5), ct);
            var exact = hits.FirstOrDefault(h => string.Equals(h.Reference, number, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        return keywords.Length == 0 ? null : (await search.SearchAsync(new SearchQuery(string.Join(' ', keywords), [SearchTypes.Resolution], bodyId, Limit: 3), ct)).FirstOrDefault();
    }

    private sealed record TimelineEntry(DateTime At, string Text, string Type, Guid Id, string Link, string Label);

    /// <summary>إجراءات القرار من سجل حالاته ومهامه وتحديثاتها، الأحدث أولًا.</summary>
    private async Task<List<TimelineEntry>> TimelineAsync(Guid resolutionId, CancellationToken ct)
    {
        var rows = await db.QueryAsync("""
            SELECT h.changed_at, 'status' AS k, h.to_status, h.reason, u.display_name_ar, NULL::uuid AS tid, NULL::text AS tref
              FROM resolutions.resolution_status_history h LEFT JOIN iam.users u ON u.id = h.changed_by WHERE h.resolution_id = @id
            UNION ALL
            SELECT t.created_at, 'task_created', t.title_ar, NULL, u.display_name_ar, t.id, t.reference_no
              FROM tasks.tasks t LEFT JOIN iam.users u ON u.id = t.created_by WHERE t.resolution_id = @id
            UNION ALL
            SELECT x.created_at, 'task_update:' || x.update_type, t.title_ar, x.note, u.display_name_ar, t.id, t.reference_no
              FROM tasks.task_updates x JOIN tasks.tasks t ON t.id = x.task_id LEFT JOIN iam.users u ON u.id = x.author_user_id WHERE t.resolution_id = @id
            ORDER BY 1 DESC LIMIT 6
            """, new { id = resolutionId }, r => (At: r.GetDateTime(0), Kind: r.GetString(1), What: r.Str("to_status") ?? "", Note: r.Str("reason"), Who: r.Str("display_name_ar") ?? "النظام",
            TaskId: r.Uuid("tid"), TaskRef: r.Str("tref")), ct);
        return rows.Select(x =>
        {
            var when = Labels.Date(x.At);
            if (x.Kind == "status")
                return new TimelineEntry(x.At, $"تغيرت حالة القرار إلى «{Labels.Resolution(x.What)}» في {when} بواسطة {x.Who}{(x.Note is { Length: > 0 } reason ? "؛ السبب: " + ArText.Shorten(reason, 160) : "")}.",
                    "resolution", resolutionId, $"/resolutions/{resolutionId}", "سجل حالات القرار");
            var kind = x.Kind == "task_created" ? "أُنشئت المهمة" : "حُدّثت المهمة";
            return new TimelineEntry(x.At, $"{kind} {x.TaskRef} «{ArText.Shorten(x.What, 80)}» في {when} بواسطة {x.Who}{(x.Note is { Length: > 0 } n ? ": " + ArText.Shorten(n, 160) : "")}.",
                "task", x.TaskId!.Value, $"/tasks/{x.TaskId}", x.TaskRef ?? "مهمة");
        }).ToList();
    }

    public DateOnly TodayLocal => clock.TodayLocal;
}
