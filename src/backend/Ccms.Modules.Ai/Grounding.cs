using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ccms.Contracts;

namespace Ccms.Modules.Ai;

/// <summary>مصدر بيانات مرسل للمزوّد بمرجع قصير (S1، R2…) يرتبط بسجل حقيقي. لا يُعرض مرجع إلا إن كان من هذه القائمة.</summary>
internal sealed record AiSource(string Ref, string Kind, string Title, string Text, string Classification,
    string? RecordType = null, Guid? RecordId = null, string? Link = null, string? Label = null, Guid? BodyId = null, JsonObject? Extra = null);

internal sealed record Citation(string Ref, string Type, Guid? Id, string Label, string? Link);
internal sealed record DirectoryEntry(string Type, Guid Id, string Name);

internal sealed class GroundingContext
{
    public required List<AiSource> Sources { get; init; }
    public List<DirectoryEntry> Directory { get; init; } = [];
    public Dictionary<string, (Guid Id, string Title)> AgendaItems { get; init; } = [];
    public Dictionary<string, string> SectionKeys { get; init; } = [];
    public Dictionary<string, (Guid Id, string Name)> Bodies { get; init; } = [];
    public DateOnly? MeetingDate { get; init; }
    public DateOnly Today { get; init; }
}

internal sealed record Grounded(JsonObject Content, List<Citation> Citations, List<string> Warnings, int Dropped);

internal sealed class AiOutputInvalidException(string detail) : Exception(detail);

/// <summary>التحقق في الخادم من كل مخرج قبل حفظه (22 §6): يُسقط كل عبارة بلا مرجع صحيح، وكل اقتباس لا يرد في مصدره، ولا يقبل مكلفًا
/// إلا من الدليل الفعلي، ولا تاريخًا إلا إن ورد نصه في الاقتباس. يُطبق على كل المزوّدين بالطريقة نفسها.</summary>
internal static partial class Grounding
{
    public static Grounded Validate(string function, string raw, GroundingContext ctx)
    {
        JsonObject o;
        try { o = JsonNode.Parse(ExtractJson(raw)) as JsonObject ?? throw new AiOutputInvalidException("المخرج ليس كائن JSON"); }
        catch (JsonException e) { throw new AiOutputInvalidException("المخرج ليس JSON صالحًا: " + e.Message); }
        var g = new Run(ctx);
        var content = function switch
        {
            AiFunctions.MinutesSummary => new JsonObject { ["points"] = g.Statements(o["points"]), ["decisions"] = g.Statements(o["decisions"]), ["followUps"] = g.Statements(o["followUps"]) },
            AiFunctions.AgendaDraft => new JsonObject { ["items"] = g.AgendaItems(o["items"]) },
            AiFunctions.MinutesDraft => g.Draft(o),
            AiFunctions.ExtractDecisions => new JsonObject { ["items"] = g.Decisions(o["items"]) },
            AiFunctions.ExtractTasks => new JsonObject { ["items"] = g.Tasks(o["items"]) },
            AiFunctions.LinkTask => g.Link(o),
            AiFunctions.SmartSearch => g.Filters(o),
            AiFunctions.ExecutiveSummary => new JsonObject
            {
                ["headline"] = g.Statement(o["headline"]), ["highlights"] = g.Statements(o["highlights"]), ["risks"] = g.Statements(o["risks"]), ["attention"] = g.Statements(o["attention"]),
            },
            AiFunctions.Ask + ":intent" => g.Intent(o),
            AiFunctions.Ask => g.Answer(o),
            _ => throw new AiOutputInvalidException("وظيفة غير معروفة"),
        };
        if (g.Dropped > 0) g.Warnings.Insert(0, $"أُسقط {g.Dropped} عنصرًا لعدم إسناده إلى مصدر صحيح أو لعدم وروده في البيانات");
        var citations = g.Used.Select(r => ctx.Sources.First(s => s.Ref == r)).Where(s => s.RecordType is not null || s.Label is not null)
            .Select(s => new Citation(s.Ref, s.RecordType ?? s.Kind, s.RecordId, s.Label ?? s.Title, s.Link)).ToList();
        return new Grounded(content, citations, g.Warnings, g.Dropped);
    }

    private static string ExtractJson(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("```")) s = s.Trim('`').Replace("json\n", "", StringComparison.OrdinalIgnoreCase);
        var a = s.IndexOf('{');
        var b = s.LastIndexOf('}');
        return a >= 0 && b > a ? s[a..(b + 1)] : s;
    }

    private static string? Str(JsonNode? n, int max = 2000)
    {
        if (n is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
        s = s.Trim();
        return s.Length == 0 ? null : s.Length > max ? s[..max] : s;
    }

    private sealed class Run(GroundingContext ctx)
    {
        public int Dropped;
        public readonly List<string> Warnings = [];
        public readonly HashSet<string> Used = [];
        private readonly Dictionary<string, AiSource> _src = ctx.Sources.ToDictionary(s => s.Ref);

        private List<string> Refs(JsonNode? n, Func<AiSource, bool>? kind = null) =>
            (n as JsonArray ?? []).Select(x => Str(x, 20)).Where(r => r is not null && _src.TryGetValue(r, out var s) && (kind?.Invoke(s) ?? true)).Select(r => r!).Distinct().ToList();

        private JsonArray Mark(List<string> refs)
        {
            foreach (var r in refs) Used.Add(r);
            return new JsonArray(refs.Select(r => (JsonNode)r).ToArray());
        }

        public JsonObject? Statement(JsonNode? n)
        {
            if (n is not JsonObject o) return null;
            var text = Str(o["text"]);
            var refs = Refs(o["refs"]);
            if (text is null || refs.Count == 0) { Dropped++; return null; }
            return new JsonObject { ["text"] = text, ["refs"] = Mark(refs) };
        }

        public JsonArray Statements(JsonNode? n) => new((n as JsonArray ?? []).Select(Statement).Where(x => x is not null).Take(30).Select(x => (JsonNode)x!).ToArray());

        public JsonArray AgendaItems(JsonNode? n)
        {
            var list = new JsonArray();
            foreach (var o in (n as JsonArray ?? []).OfType<JsonObject>())
            {
                var title = Str(o["title"], 300);
                var refs = Refs(o["refs"]);
                if (title is null || refs.Count == 0) { Dropped++; continue; }
                var type = Str(o["itemType"]) is "for_decision" or "for_information" or "for_discussion" ? Str(o["itemType"]) : "for_discussion";
                list.Add(new JsonObject { ["title"] = title, ["reason"] = Str(o["reason"], 500), ["itemType"] = type, ["refs"] = Mark(refs) });
            }
            return list;
        }

        public JsonObject Draft(JsonObject o)
        {
            var sections = new JsonArray();
            foreach (var s in (o["sections"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var key = Str(s["key"], 60);
                var body = Str(s["body"], 20000);
                var refs = Refs(s["refs"], x => x.Kind == "note");
                if (key is null || !ctx.SectionKeys.ContainsKey(key) || body is null || refs.Count == 0) { Dropped++; continue; }
                sections.Add(new JsonObject { ["key"] = key, ["title"] = ctx.SectionKeys[key], ["body"] = body, ["refs"] = Mark(refs) });
            }
            var items = new JsonArray();
            foreach (var s in (o["items"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var a = Str(s["agendaRef"], 20);
                var refs = Refs(s["refs"], x => x.Kind == "note");
                var disc = Str(s["discussion"], 20000);
                var rec = Str(s["recommendation"], 5000);
                if (a is null || !ctx.AgendaItems.TryGetValue(a, out var item) || refs.Count == 0 || (disc is null && rec is null)) { Dropped++; continue; }
                items.Add(new JsonObject
                {
                    ["agendaItemId"] = item.Id.ToString(), ["agendaTitle"] = item.Title, ["discussion"] = disc, ["recommendation"] = rec, ["refs"] = Mark(refs),
                });
            }
            Warnings.Add("مسودة مصاغة آليًا من الملاحظات: راجع مطابقتها قبل نقلها إلى المحضر");
            return new JsonObject { ["sections"] = sections, ["items"] = items };
        }

        private bool QuoteGrounded(string quote, List<string> refs) => refs.Any(r => ArText.Contains(_src[r].Text, quote) || ArText.Contains(_src[r].Title, quote));

        public JsonArray Decisions(JsonNode? n)
        {
            var list = new JsonArray();
            foreach (var o in (n as JsonArray ?? []).OfType<JsonObject>())
            {
                var kind = Str(o["kind"]);
                var quote = Str(o["quote"], 3000);
                var refs = Refs(o["refs"]);
                var text = Str(o["text"], 3000) ?? quote;
                if (kind is not ("decision" or "recommendation") || quote is null || refs.Count == 0 || !QuoteGrounded(quote, refs)) { Dropped++; continue; }
                var agenda = Str(o["agendaRef"], 20) is { } a && ctx.AgendaItems.TryGetValue(a, out var ai) ? ai : default;
                list.Add(new JsonObject
                {
                    ["kind"] = kind, ["title"] = Str(o["title"], 300) ?? ArText.Shorten(quote, 120), ["text"] = text, ["quote"] = quote, ["refs"] = Mark(refs),
                    ["agendaItemId"] = agenda.Id == Guid.Empty ? null : agenda.Id.ToString(), ["agendaTitle"] = agenda.Title,
                });
            }
            return list;
        }

        public JsonArray Tasks(JsonNode? n)
        {
            var list = new JsonArray();
            foreach (var o in (n as JsonArray ?? []).OfType<JsonObject>())
            {
                var quote = Str(o["quote"], 3000);
                var refs = Refs(o["refs"]);
                var title = Str(o["title"], 300);
                if (quote is null || title is null || refs.Count == 0 || !QuoteGrounded(quote, refs)) { Dropped++; continue; }
                var item = new JsonObject { ["title"] = title, ["quote"] = quote, ["refs"] = Mark(refs) };

                // المكلف: من الدليل الفعلي فقط؛ نص المكلف يُقبل فقط إن ورد في الاقتباس
                var who = Str(o["assigneeText"], 200);
                if (who is not null && !ArText.Contains(quote, who)) { Warnings.Add($"أُسقط مكلف لم يرد في النص: «{who}»"); who = null; }
                var match = who is null ? null : MatchDirectory(who);
                item["assignee"] = match is not null
                    ? new JsonObject { ["matched"] = true, ["type"] = match.Type, ["id"] = match.Id.ToString(), ["name"] = match.Name, ["rawText"] = who }
                    : who is not null ? new JsonObject { ["matched"] = false, ["rawText"] = who } : null;

                // الموعد: يُقبل فقط إن ورد نصه في الاقتباس، ويُحسب صراحة من نصه
                var dueText = Str(o["dueText"], 100);
                if (dueText is not null && !ArText.Contains(quote, dueText) && !quote.Contains(dueText)) { Warnings.Add($"أُسقط موعد لم يرد في النص: «{dueText}»"); dueText = null; }
                var (due, basis) = dueText is null ? (null, null) : ParseDue(dueText);
                item["dueText"] = dueText;
                item["dueDate"] = due?.ToString("yyyy-MM-dd");
                item["dueBasis"] = basis;

                var resRef = Str(o["resolutionRef"], 20);
                if (resRef is not null && _src.TryGetValue(resRef, out var rs) && rs.Kind == "resolution")
                {
                    Used.Add(resRef);
                    item["resolutionRef"] = resRef;
                    item["resolutionId"] = rs.RecordId?.ToString();
                    item["resolutionTitle"] = rs.Title;
                }
                list.Add(item);
            }
            return list;
        }

        private DirectoryEntry? MatchDirectory(string who)
        {
            var w = ArText.Normalize(who);
            var wt = ArText.Tokens(who);
            DirectoryEntry? best = null;
            var bestScore = 0.0;
            foreach (var d in ctx.Directory)
            {
                var n = ArText.Normalize(d.Name);
                var score = n == w ? 1.0 : (n.Length >= 4 && (w.Contains(n) || n.Contains(w))) ? 0.9 : Math.Min(ArText.Overlap(ArText.Tokens(d.Name), wt), ArText.Overlap(wt, ArText.Tokens(d.Name)));
                if (score > bestScore) { bestScore = score; best = d; }
            }
            return bestScore >= 0.6 ? best : null;
        }

        private static readonly string[] Months = ["يناير", "فبراير", "مارس", "ابريل", "مايو", "يونيو", "يوليو", "اغسطس", "سبتمبر", "اكتوبر", "نوفمبر", "ديسمبر"];

        private (DateOnly?, string?) ParseDue(string text)
        {
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)) return (iso, "explicit");
            if (DateOnly.TryParseExact(text, ["d/M/yyyy", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dmY)) return (dmY, "explicit");
            var norm = ArText.Normalize(text);
            var anchor = ctx.MeetingDate ?? ctx.Today;
            var rel = Regex.Match(norm, @"خلال\s+(?:(\d+)\s+)?(يوم|ايام|يوما|اسبوعين|اسبوع|اسابيع|شهرين|شهر|اشهر)");
            if (rel.Success)
            {
                var n = rel.Groups[1].Success ? int.Parse(rel.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
                var unit = rel.Groups[2].Value;
                var date = unit switch
                {
                    "اسبوعين" => anchor.AddDays(14), "شهرين" => anchor.AddMonths(2),
                    "اسبوع" or "اسابيع" => anchor.AddDays(7 * n), "شهر" or "اشهر" => anchor.AddMonths(n),
                    _ => anchor.AddDays(n),
                };
                return (date, $"relative:{(ctx.MeetingDate is null ? "today" : "meeting_date")}");
            }
            var m = Months.Select((x, i) => (x, i)).FirstOrDefault(x => norm.Contains(x.x));
            if (m.x is not null)
            {
                var y = Regex.Match(norm, @"(20\d{2})");
                var year = y.Success ? int.Parse(y.Value, CultureInfo.InvariantCulture) : anchor.Year + (m.i + 1 < anchor.Month ? 1 : 0);
                var end = new DateOnly(year, m.i + 1, 1).AddMonths(1).AddDays(-1);
                return (end, "month_end");
            }
            return (null, null);
        }

        public JsonObject Link(JsonObject o)
        {
            JsonObject? Pick(JsonNode? n)
            {
                if (n is not JsonObject b) return null;
                var r = Str(b["ref"], 20);
                if (r is null || !_src.TryGetValue(r, out var s) || s.Kind != "resolution") { Dropped++; return null; }
                Used.Add(r);
                var conf = b["confidence"] is JsonValue cv && cv.TryGetValue<double>(out var c) ? Math.Clamp(c, 0, 1) : 0;
                return new JsonObject { ["ref"] = r, ["resolutionId"] = s.RecordId?.ToString(), ["title"] = s.Title, ["number"] = s.Label, ["confidence"] = Math.Round(conf, 2), ["reason"] = Str(b["reason"], 300) };
            }
            var best = o["best"] is null ? null : Pick(o["best"]);
            var alts = new JsonArray((o["alternatives"] as JsonArray ?? []).Select(Pick).Where(x => x is not null).Take(3).Select(x => (JsonNode)x!).ToArray());
            return new JsonObject { ["best"] = best, ["alternatives"] = alts };
        }

        public JsonObject Filters(JsonObject o)
        {
            var types = (o["types"] as JsonArray ?? []).Select(x => Str(x, 20)).Where(t => t is not null && SearchTypes.All.Contains(t)).Distinct().Select(t => (JsonNode)t!).ToArray();
            var bodyRef = Str(o["bodyRef"], 20);
            var body = bodyRef is not null && ctx.Bodies.TryGetValue(bodyRef, out var b) ? b : default;
            DateOnly? D(JsonNode? n) => DateOnly.TryParseExact(Str(n, 10) ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            return new JsonObject
            {
                ["types"] = new JsonArray(types), ["status"] = Str(o["status"]) == "open" ? "open" : null,
                ["overdue"] = o["overdue"] is JsonValue ov && ov.TryGetValue<bool>(out var od) && od,
                ["bodyId"] = body.Id == Guid.Empty ? null : body.Id.ToString(), ["bodyName"] = body.Name,
                ["from"] = D(o["from"])?.ToString("yyyy-MM-dd"), ["to"] = D(o["to"])?.ToString("yyyy-MM-dd"),
                ["keywords"] = new JsonArray((o["keywords"] as JsonArray ?? []).Select(x => Str(x, 40)).Where(x => x is not null).Take(6).Select(x => (JsonNode)x!).ToArray()),
            };
        }

        public JsonObject Intent(JsonObject o)
        {
            var intent = Str(o["intent"]) is { } i && i is "overdue_resolutions" or "last_action" or "open_tasks" or "resolution_status" ? i : "general";
            var f = Filters(new JsonObject { ["bodyRef"] = o["bodyRef"]?.DeepClone(), ["keywords"] = o["keywords"]?.DeepClone() });
            return new JsonObject { ["intent"] = intent, ["resolutionNumber"] = Str(o["resolutionNumber"], 40), ["bodyId"] = f["bodyId"]?.DeepClone(), ["keywords"] = f["keywords"]!.DeepClone() };
        }

        public JsonObject Answer(JsonObject o)
        {
            var answer = Statements(o["answer"]);
            return new JsonObject { ["noAnswer"] = answer.Count == 0, ["answer"] = answer };
        }
    }
}
