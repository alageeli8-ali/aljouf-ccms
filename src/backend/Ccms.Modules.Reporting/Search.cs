using System.Text;
using System.Text.RegularExpressions;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Http;

namespace Ccms.Modules.Reporting;

/// <summary>تطبيع عربي مطابق لدالة public.ar_norm، وتقسيم الاستعلام إلى كلمات دالة.</summary>
public static partial class ArabicText
{
    private static readonly HashSet<string> Stop =
    [
        "في", "من", "على", "الى", "الي", "عن", "ما", "ماذا", "هل", "التي", "الذي", "الذين", "او", "ام", "ثم", "مع", "هذا", "هذه", "ذلك", "تلك", "كل",
        "اي", "كم", "متي", "اين", "كيف", "لماذا", "قد", "تم", "لم", "لا", "ان", "انه", "هو", "هي", "به", "بها", "له", "لها", "عند", "بعد", "قبل",
        "the", "of", "and", "or", "in", "on", "for", "to", "what", "which", "is", "are", "a", "an",
    ];

    [GeneratedRegex("[ً-ْـ]")] private static partial Regex Diacritics();
    [GeneratedRegex(@"[^\p{L}\p{Nd}\-/]+")] private static partial Regex Separators();

    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            sb.Append(ch switch { 'أ' or 'إ' or 'آ' or 'ٱ' => 'ا', 'ى' => 'ي', 'ة' => 'ه', _ => ch });
        return Diacritics().Replace(sb.ToString(), "");
    }

    /// <summary>كلمات البحث بعد التطبيع وحذف أدوات الربط وأداة التعريف (حتى ست كلمات).</summary>
    public static string[] Tokens(string? q)
    {
        var words = Separators().Split(Normalize(q)).Where(w => w.Length > 0);
        var list = new List<string>();
        foreach (var raw in words)
        {
            var w = raw;
            if (Stop.Contains(w)) continue;
            if (w.Length > 4 && (w.StartsWith("وال") || w.StartsWith("بال") || w.StartsWith("لل"))) w = w[(w.StartsWith("لل") ? 2 : 3)..];
            else if (w.Length > 3 && w.StartsWith("ال")) w = w[2..];
            if (w.Length < 2 || Stop.Contains(w)) continue;
            if (!list.Contains(w)) list.Add(w);
        }
        return list.Take(6).ToArray();
    }

    /// <summary>مقطع قصير حول أول ورود لكلمة من الاستعلام.</summary>
    public static string Snippet(string? text, string[] tokens, int width = 220)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        var norm = Normalize(flat);
        var at = tokens.Select(t => norm.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - width / 3);
        var piece = flat.Substring(start, Math.Min(width, flat.Length - start));
        return (start > 0 ? "… " : "") + piece + (start + width < flat.Length ? " …" : "");
    }
}

/// <summary>البحث التقليدي في المحاضر والقرارات والمهام والاجتماعات. أساسي ومستقل عن الذكاء الاصطناعي، وكل نتيجة مقيدة بصلاحيات المستخدم داخل الاستعلام نفسه.</summary>
internal sealed class SearchService(Db db, IAccessService access, ICurrentUser me) : ISearchService
{
    private const string Today = "(now() AT TIME ZONE 'Asia/Riyadh')::date";

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery q, CancellationToken ct = default)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        var tokens = ArabicText.Tokens(q.Text);
        var types = (q.Types is { Length: > 0 } ? q.Types : SearchTypes.All).Where(SearchTypes.All.Contains).ToHashSet();
        var limit = Math.Clamp(q.Limit, 1, 50);
        var hits = new List<SearchHit>();
        if (tokens.Length == 0 && q.Status is null && !q.OverdueOnly && q.BodyId is null && q.From is null && q.To is null) return hits;

        var args = new Dictionary<string, object?>
        {
            ["me"] = me.UserId, ["body"] = q.BodyId, ["status"] = q.Status, ["overdue"] = q.OverdueOnly,
            ["from"] = q.From?.ToDateTime(TimeOnly.MinValue), ["to"] = q.To?.ToDateTime(TimeOnly.MaxValue), ["limit"] = limit * 2,
        };
        for (var i = 0; i < tokens.Length; i++) args[$"t{i}"] = "%" + tokens[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        string Score(string title, string body) => tokens.Length == 0 ? "1" :
            string.Join(" + ", Enumerable.Range(0, tokens.Length).Select(i =>
                $"(CASE WHEN public.ar_norm({title}) LIKE @t{i} THEN 2 ELSE 0 END + CASE WHEN public.ar_norm({body}) LIKE @t{i} THEN 1 ELSE 0 END)"));

        async Task<BodyFilter> Filter(string perm, string prefix)
        {
            var f = await access.GetBodyFilterAsync(perm, ct);
            args[prefix + "All"] = f.All;
            args[prefix + "Ids"] = f.BodyIds;
            return f;
        }

        if (types.Contains(SearchTypes.Resolution))
        {
            await Filter(Permissions.ResolutionView, "r");
            var score = Score("r.title_ar || ' ' || coalesce(r.number, '')", "r.text_ar");
            hits.AddRange(await db.QueryAsync($"""
                SELECT * FROM (
                  SELECT r.id, r.title_ar, r.number, r.body_id, b.name_ar, r.status, COALESCE(r.issued_at, r.created_at) AS at, r.text_ar, r.classification,
                         ({score}) AS score,
                         EXISTS (SELECT 1 FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status
                                 WHERE t.resolution_id = r.id AND ws.category IN ('open','active','waiting','review') AND t.due_date < {Today}) AS late,
                         (SELECT min(t.due_date) FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status
                                 WHERE t.resolution_id = r.id AND ws.category IN ('open','active','waiting','review')) AS next_due
                  FROM resolutions.resolutions r JOIN bodies.bodies b ON b.id = r.body_id
                  WHERE (@rAll::boolean OR r.body_id = ANY(@rIds::uuid[])) AND r.status NOT IN ('withdrawn')
                    AND (@body::uuid IS NULL OR r.body_id = @body)
                    AND (@status::text IS NULL OR r.status = @status OR (@status = 'open' AND r.status IN ('issued','in_execution','referred')))
                    AND (@from::timestamptz IS NULL OR COALESCE(r.issued_at, r.created_at) >= @from) AND (@to::timestamptz IS NULL OR COALESCE(r.issued_at, r.created_at) <= @to)
                ) x WHERE score > 0 AND (NOT @overdue::boolean OR (late AND status IN ('issued','in_execution')))
                ORDER BY score DESC, at DESC LIMIT @limit
                """, args, r => new SearchHit(SearchTypes.Resolution, r.GetGuid(0), r.GetString(1), r.Str("number"), r.GetGuid(3), r.GetString(4), r.GetString(5),
                r.GetDateTime(6), ArabicText.Snippet(r.GetString(7), tokens), r.GetString(8), $"/resolutions/{r.GetGuid(0)}", r.GetInt32(9), r.Date("next_due")), ct));
        }

        if (types.Contains(SearchTypes.Task))
        {
            await Filter(Permissions.TaskView, "t");
            var score = Score("t.title_ar || ' ' || coalesce(t.reference_no, '')", "coalesce(t.description, '') || ' ' || coalesce(t.acceptance_criteria, '')");
            hits.AddRange(await db.QueryAsync($"""
                SELECT * FROM (
                  SELECT t.id, t.title_ar, t.reference_no, t.body_id, b.name_ar, t.status, t.created_at, coalesce(t.description, '') AS d, b.classification,
                         ({score}) AS score, t.due_date, ws.category
                  FROM tasks.tasks t JOIN bodies.bodies b ON b.id = t.body_id JOIN tasks.workflow_statuses ws ON ws.code = t.status
                  WHERE (@tAll::boolean OR t.body_id = ANY(@tIds::uuid[])
                         OR t.assignee_user_id = @me OR t.executor_user_id = @me OR t.accountable_user_id = @me OR t.verifier_user_id = @me)
                    AND (@body::uuid IS NULL OR t.body_id = @body)
                    AND (@status::text IS NULL OR t.status = @status OR (@status = 'open' AND ws.category IN ('open','active','waiting','review')))
                    AND (@from::timestamptz IS NULL OR t.created_at >= @from) AND (@to::timestamptz IS NULL OR t.created_at <= @to)
                ) x WHERE score > 0 AND (NOT @overdue::boolean OR (category IN ('open','active','waiting','review') AND due_date < {Today}))
                ORDER BY score DESC, created_at DESC LIMIT @limit
                """, args, r => new SearchHit(SearchTypes.Task, r.GetGuid(0), r.GetString(1), r.Str("reference_no"), r.GetGuid(3), r.GetString(4), r.GetString(5),
                r.GetDateTime(6), ArabicText.Snippet(r.GetString(7), tokens), r.GetString(8), $"/tasks/{r.GetGuid(0)}", r.GetInt32(9), r.Date("due_date")), ct));
        }

        if (types.Contains(SearchTypes.Minutes) && !q.OverdueOnly)
        {
            await Filter(Permissions.MinutesView, "mi");
            // المحتوى من آخر إصدار مرفوع؛ المسودة غير المرفوعة يُبحث في عنوانها فقط
            const string body = """
                coalesce((SELECT string_agg(coalesce(s->>'title', '') || ': ' || coalesce(s->>'body', ''), E'\n')
                          FROM jsonb_array_elements(v.content->'sections') s), '')
                || ' ' || coalesce((SELECT string_agg(coalesce(i->>'discussion', '') || ' ' || coalesce(i->>'recommendation', ''), E'\n')
                          FROM jsonb_array_elements(v.content->'items') i), '')
                """;
            var score = Score("m.title_ar || ' ' || coalesce(m.reference_no, '')", body);
            hits.AddRange(await db.QueryAsync($"""
                SELECT * FROM (
                  SELECT mi.id, m.title_ar, m.reference_no, m.body_id, b.name_ar, mi.status, m.starts_at, {body} AS txt, mi.classification, ({score}) AS score
                  FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id JOIN bodies.bodies b ON b.id = m.body_id
                  LEFT JOIN minutes.minutes_versions v ON v.minutes_id = mi.id AND v.version_no = mi.current_version_no
                  WHERE (@miAll::boolean OR m.body_id = ANY(@miIds::uuid[]))
                    AND (@body::uuid IS NULL OR m.body_id = @body) AND (@status::text IS NULL OR mi.status = @status OR @status = 'open')
                    AND (@from::timestamptz IS NULL OR m.starts_at >= @from) AND (@to::timestamptz IS NULL OR m.starts_at <= @to)
                ) x WHERE score > 0 ORDER BY score DESC, starts_at DESC LIMIT @limit
                """, args, r => new SearchHit(SearchTypes.Minutes, r.GetGuid(0), r.GetString(1), r.Str("reference_no"), r.GetGuid(3), r.GetString(4), r.GetString(5),
                r.GetDateTime(6), ArabicText.Snippet(r.GetString(7), tokens), r.GetString(8), $"/minutes/{r.GetGuid(0)}", r.GetInt32(9)), ct));
        }

        if (types.Contains(SearchTypes.Meeting) && !q.OverdueOnly)
        {
            await Filter(Permissions.MeetingView, "m");
            var score = Score("m.title_ar || ' ' || coalesce(m.reference_no, '')", "coalesce(m.location, '')");
            hits.AddRange(await db.QueryAsync($"""
                SELECT * FROM (
                  SELECT m.id, m.title_ar, m.reference_no, m.body_id, b.name_ar, m.status, m.starts_at, coalesce(m.location, '') AS loc, m.classification, ({score}) AS score
                  FROM meetings.meetings m JOIN bodies.bodies b ON b.id = m.body_id
                  WHERE (@mAll::boolean OR m.body_id = ANY(@mIds::uuid[])) AND (@body::uuid IS NULL OR m.body_id = @body)
                    AND (@status::text IS NULL OR m.status = @status OR @status = 'open')
                    AND (@from::timestamptz IS NULL OR m.starts_at >= @from) AND (@to::timestamptz IS NULL OR m.starts_at <= @to)
                ) x WHERE score > 0 ORDER BY score DESC, starts_at DESC LIMIT @limit
                """, args, r => new SearchHit(SearchTypes.Meeting, r.GetGuid(0), r.GetString(1), r.Str("reference_no"), r.GetGuid(3), r.GetString(4), r.GetString(5),
                r.GetDateTime(6), ArabicText.Snippet(r.GetString(7), tokens), r.GetString(8), $"/meetings/{r.GetGuid(0)}", r.GetInt32(9)), ct));
        }

        return hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.Date).Take(limit).ToList();
    }

    public static async Task<IResult> Endpoint(string? q, string? types, Guid? bodyId, string? status, bool? overdue, ISearchService search, CancellationToken ct)
    {
        var list = await search.SearchAsync(new SearchQuery(q, types?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), bodyId,
            string.IsNullOrWhiteSpace(status) ? null : status, overdue ?? false, Limit: 30), ct);
        return Results.Ok(new { query = q, tokens = ArabicText.Tokens(q), items = list });
    }
}
