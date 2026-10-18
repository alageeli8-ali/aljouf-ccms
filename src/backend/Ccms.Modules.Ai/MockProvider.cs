using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ccms.Contracts;
using Microsoft.Extensions.Hosting;

namespace Ccms.Modules.Ai;

/// <summary>حقن أعطال المزوّد التجريبي للاختبار: unavailable | timeout | error | invalid_credentials | invalid_output | hallucinate.
/// hallucinate يضيف إلى المخرج مراجع ومسؤولين وتواريخ مختلقة ليُثبت أن الخادم يُسقطها.</summary>
internal sealed class MockAiFaults
{
    private readonly Lock _lock = new();
    public string Mode { get; private set; } = "none";
    public int DelayMs { get; private set; }
    public int Remaining { get; private set; }

    public void Set(string mode, int delayMs, int count)
    {
        lock (_lock) { Mode = mode; DelayMs = Math.Clamp(delayMs, 0, 120_000); Remaining = Math.Max(0, count); }
    }

    /// <summary>العطل الذي يُطبق على هذا الطلب (ويُنقص العداد إن كان محدودًا).</summary>
    public string Take()
    {
        lock (_lock)
        {
            if (Mode == "none") return "none";
            var m = Mode;
            if (Remaining > 0 && --Remaining == 0) Mode = "none";
            return m;
        }
    }

    public object Snapshot() => new { mode = Mode, delayMs = DelayMs, remaining = Remaining };
}

/// <summary>مزوّد تجريبي للاختبار فقط — ليس نموذجًا لغويًا. يستخرج من البيانات المرفقة نفسها بقواعد حتمية (جمل، وكلمات دالة، وأنماط تواريخ)،
/// فلا يضيف معلومة من خارجها. ممنوع في الإنتاج، وكل مخرج منه موسوم «تجريبي» في الواجهة.</summary>
internal sealed partial class MockAiProvider(MockAiFaults faults, IHostEnvironment env) : IAiProvider
{
    public const string ModelName = "mock-extractive-v1";
    public string Code => "mock";
    public string NameAr => "مزوّد تجريبي (للاختبار فقط)";
    public string NameEn => "Mock provider (test only)";
    public bool TestOnly => true;

    public Task<AiReadiness> ReadinessAsync(AiSettings s, bool transcription, CancellationToken ct) =>
        Task.FromResult(env.IsProduction() ? new AiReadiness(false, "mock_blocked_in_production", true) : new AiReadiness(true, "ready", true));

    public Task<ConnectorHealth> CheckAsync(AiSettings s, CancellationToken ct) => Task.FromResult(env.IsProduction()
        ? new ConnectorHealth(ConnectorState.Failing, "المزوّد التجريبي ممنوع في بيئة الإنتاج")
        : faults.Mode is "unavailable" or "timeout"
            ? new ConnectorHealth(ConnectorState.Failing, "المزوّد التجريبي في وضع عطل محقون: " + faults.Mode)
            : new ConnectorHealth(ConnectorState.Healthy, "مزوّد تجريبي للاختبار فقط — ليس نموذجًا لغويًا", 1));

    private async Task<string> ApplyFaultAsync(TimeSpan timeout, CancellationToken ct)
    {
        var mode = faults.Take();
        if (faults.DelayMs > 0) await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(faults.DelayMs, timeout.TotalMilliseconds + 50)), ct);
        if (faults.DelayMs > timeout.TotalMilliseconds || mode == "timeout") throw new AiProviderException("timeout", $"تجاوز المزوّد المهلة ({timeout.TotalSeconds:0} ثانية)");
        return mode switch
        {
            "unavailable" => throw new AiProviderException("unavailable", "تعذر الاتصال بالمزوّد (عطل محقون)"),
            "error" => throw new AiProviderException("error", "رفض المزوّد الطلب (HTTP 500 محقون)"),
            "invalid_credentials" => throw new AiProviderException("invalid_credentials", "رفض المزوّد بيانات الاعتماد (HTTP 401 محقون)"),
            _ => mode,
        };
    }

    public async Task<AiCompletion> CompleteAsync(AiSettings s, AiPromptRequest r, CancellationToken ct)
    {
        var mode = await ApplyFaultAsync(r.Timeout, ct);
        if (mode == "invalid_output") return new AiCompletion("هذا ليس JSON {", Est(r.System + r.User), 5, true, ModelName);
        var src = Sources(r.Payload);
        JsonObject output = r.Function switch
        {
            AiFunctions.MinutesSummary => Summary(src),
            AiFunctions.AgendaDraft => Agenda(src),
            AiFunctions.MinutesDraft => Draft(r.Payload, src),
            AiFunctions.ExtractDecisions => Decisions(src),
            AiFunctions.ExtractTasks => Tasks(src),
            AiFunctions.LinkTask => Link(r.Payload, src),
            AiFunctions.SmartSearch => Interpret(r.Payload),
            AiFunctions.ExecutiveSummary => Executive(src),
            AiFunctions.Ask + ":intent" => Intent(r.Payload),
            AiFunctions.Ask => Answer(src),
            _ => throw new AiProviderException("error", "وظيفة غير مدعومة"),
        };
        if (mode == "hallucinate") Hallucinate(r.Function, output);
        var json = output.ToJsonString();
        return new AiCompletion(json, Est(r.System + r.User), Est(json), true, ModelName);
    }

    public async Task<AiTranscript> TranscribeAsync(AiSettings s, byte[] audio, string fileName, string contentType, CancellationToken ct)
    {
        await ApplyFaultAsync(s.Timeout, ct);
        // لا يفرّغ صوتًا حقيقيًا: يقرأ نصًا مضمنًا في ملفات الاختبار بعد العلامة CCMS-TEST-TRANSCRIPT:
        var raw = Encoding.UTF8.GetString(audio);
        var at = raw.IndexOf("CCMS-TEST-TRANSCRIPT:", StringComparison.Ordinal);
        if (at < 0) throw new AiProviderException("unsupported_audio", "المزوّد التجريبي لا يفرّغ صوتًا حقيقيًا؛ يقرأ فقط نصًا مضمنًا في ملفات الاختبار");
        var text = raw[(at + "CCMS-TEST-TRANSCRIPT:".Length)..];
        var end = text.IndexOf('\0');
        if (end >= 0) text = text[..end];
        return new AiTranscript(text.Trim(), ModelName, audio.Length / 4000 + 1, true);
    }

    private static int Est(string s) => Math.Max(1, s.Length / 4);

    // ---------------- قواعد الاستخراج الحتمية ----------------

    private sealed record Src(string Ref, string Kind, string Title, string Text, JsonObject Raw);

    private static List<Src> Sources(JsonObject payload) =>
        (payload["sources"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => new Src(o["ref"]!.GetValue<string>(), o["kind"]?.GetValue<string>() ?? "", o["title"]?.GetValue<string>() ?? "", o["text"]?.GetValue<string>() ?? "", o))
            .ToList();

    private static JsonObject Stmt(string text, params string[] refs) => new() { ["text"] = text, ["refs"] = new JsonArray(refs.Select(x => (JsonNode)x).ToArray()) };

    private static JsonObject Summary(List<Src> src)
    {
        var points = new JsonArray();
        foreach (var s in src.Where(s => s.Kind is "section" or "item").Take(10))
            if (ArText.Sentences(s.Text).FirstOrDefault() is { } first) points.Add(Stmt(ArText.Shorten((s.Kind == "item" ? s.Title + ": " : "") + first, 260), s.Ref));
        var decisions = new JsonArray(src.Where(s => s.Kind == "resolution").Take(10)
            .Select(s => (JsonNode)Stmt(ArText.Shorten(s.Title + " — " + (ArText.Sentences(s.Text).FirstOrDefault() ?? ""), 260), s.Ref)).ToArray());
        var follow = new JsonArray(src.Where(s => s.Kind == "task").Take(8).Select(s => (JsonNode)Stmt(s.Title, s.Ref)).ToArray());
        return new JsonObject { ["points"] = points, ["decisions"] = decisions, ["followUps"] = follow };
    }

    private static JsonObject Agenda(List<Src> src)
    {
        var items = new JsonArray();
        foreach (var s in src.OrderBy(s => s.Kind switch { "overdue_task" => 0, "open_resolution" => 1, "deferred_item" => 2, _ => 3 }).Take(8))
        {
            var (title, type) = s.Kind switch
            {
                "open_resolution" => ("متابعة تنفيذ القرار: " + s.Title, "for_discussion"),
                "overdue_task" => ("متابعة المهمة المتأخرة: " + s.Title, "for_discussion"),
                "deferred_item" => (s.Title, "for_decision"),
                _ => (s.Title, "for_information"),
            };
            items.Add(new JsonObject { ["title"] = ArText.Shorten(title, 280), ["reason"] = ArText.Shorten(s.Text, 300), ["itemType"] = type, ["refs"] = new JsonArray(s.Ref) });
        }
        return new JsonObject { ["items"] = items };
    }

    [GeneratedRegex("افتتح|افتتاح|بدا الاجتماع|استهل|رحب")] private static partial Regex OpeningWords();
    [GeneratedRegex("اختتم|اختتام|انتهي الاجتماع|رفع الاجتماع|رفعت الجلسه")] private static partial Regex ClosingWords();
    [GeneratedRegex("^(توصيه|التوصيه|اوصي|اوصت|يوصي|قرر|قررت|تقرر|الموافقه علي)")] private static partial Regex RecommendationStart();

    private static JsonObject Draft(JsonObject payload, List<Src> notes)
    {
        var agenda = (payload["agendaItems"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(a => (Ref: a["ref"]!.GetValue<string>(), Title: a["title"]!.GetValue<string>(), Tokens: ArText.Tokens(a["title"]!.GetValue<string>()))).ToList();
        var keys = (payload["sectionKeys"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(k => (Key: k["key"]!.GetValue<string>(), Title: k["title"]!.GetValue<string>())).ToList();
        string? Key(string kind) => keys.FirstOrDefault(k => k.Key.Contains(kind) || (kind == "opening" ? k.Title.Contains("افتتاح") : k.Title.Contains("اختتام") || k.Title.Contains("ختام"))).Key;

        var sections = new Dictionary<string, (List<string> Lines, List<string> Refs)>();
        var items = new Dictionary<string, (List<string> Disc, List<string> Rec, List<string> Refs)>();
        string? current = null;
        foreach (var n in notes)
        {
            var line = ArText.Clean(n.Text);
            if (line.Length == 0) continue;
            var norm = ArText.Normalize(line);
            var tokens = ArText.Tokens(line);
            var match = agenda.Select(a => (a.Ref, Score: ArText.Overlap(a.Tokens, tokens))).OrderByDescending(x => x.Score).FirstOrDefault();
            if (match.Ref is not null && match.Score >= 0.5) current = match.Ref;
            string? sectionKey = OpeningWords().IsMatch(norm) && current is null ? Key("opening") : ClosingWords().IsMatch(norm) ? Key("closing") : null;
            if (sectionKey is not null)
            {
                if (!sections.TryGetValue(sectionKey, out var sec)) sections[sectionKey] = sec = ([], []);
                sec.Lines.Add(line);
                sec.Refs.Add(n.Ref);
                if (ClosingWords().IsMatch(norm)) current = null;
                continue;
            }
            if (current is null) continue; // لا يُخمن موضع ملاحظة لا تُنسب لبند ولا لقسم
            if (!items.TryGetValue(current, out var it)) items[current] = it = ([], [], []);
            (RecommendationStart().IsMatch(norm) ? it.Rec : it.Disc).Add(line);
            it.Refs.Add(n.Ref);
        }
        return new JsonObject
        {
            ["sections"] = new JsonArray(sections.Select(kv => (JsonNode)new JsonObject
            {
                ["key"] = kv.Key, ["title"] = keys.First(k => k.Key == kv.Key).Title, ["body"] = string.Join("\n", kv.Value.Lines),
                ["refs"] = new JsonArray(kv.Value.Refs.Distinct().Select(x => (JsonNode)x).ToArray()),
            }).ToArray()),
            ["items"] = new JsonArray(items.Select(kv => (JsonNode)new JsonObject
            {
                ["agendaRef"] = kv.Key, ["discussion"] = string.Join("\n", kv.Value.Disc), ["recommendation"] = string.Join("\n", kv.Value.Rec),
                ["refs"] = new JsonArray(kv.Value.Refs.Distinct().Select(x => (JsonNode)x).ToArray()),
            }).ToArray()),
        };
    }

    [GeneratedRegex("قرر|قررت|تقرر|اعتمدت|اعتمد|الموافقه علي|وافقت|وافق")] private static partial Regex DecisionWords();
    [GeneratedRegex("اوصت|توصي|يوصي|اوصي|التوصيه|توصيه")] private static partial Regex RecommendationWords();

    private static JsonObject Decisions(List<Src> src)
    {
        var items = new JsonArray();
        foreach (var s in src)
            foreach (var sentence in ArText.Sentences(s.Text))
            {
                var norm = ArText.Normalize(sentence);
                var kind = DecisionWords().IsMatch(norm) ? "decision" : RecommendationWords().IsMatch(norm) ? "recommendation" : null;
                if (kind is null) continue;
                var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                items.Add(new JsonObject
                {
                    ["kind"] = kind, ["title"] = ArText.Shorten(string.Join(' ', words.Skip(words.Length > 4 ? 1 : 0).Take(10)), 120), ["text"] = sentence, ["quote"] = sentence,
                    ["refs"] = new JsonArray(s.Ref), ["agendaRef"] = s.Raw["agendaRef"]?.GetValue<string>(),
                });
            }
        return new JsonObject { ["items"] = items };
    }

    [GeneratedRegex(@"(?:تكليف|يكلف|يُكلف|يُكلّف|كلفت|كلّفت|كُلف|كُلّف|كلف)\s+(?<who>[^،.؛]+?)\s+ب(?=[ء-ي])")] private static partial Regex Assignment();
    [GeneratedRegex(@"(?<due>\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{4}|خلال\s+(?:\d+\s+)?(?:أسبوعين|أسابيع|أسبوع|شهرين|أشهر|شهر|يومًا|يوما|أيام|يوم)|(?:قبل|بنهاية|حتى)\s+(?:نهاية\s+)?(?:شهر\s+)?(?:يناير|فبراير|مارس|أبريل|مايو|يونيو|يوليو|أغسطس|سبتمبر|أكتوبر|نوفمبر|ديسمبر)(?:\s+\d{4})?)")] private static partial Regex DuePhrase();

    private static JsonObject Tasks(List<Src> src)
    {
        var items = new JsonArray();
        foreach (var s in src)
            foreach (var sentence in ArText.Sentences(s.Text))
            {
                var m = Assignment().Match(sentence);
                if (!m.Success) continue;
                var rest = sentence[(m.Index + m.Length - 0)..];
                var due = DuePhrase().Match(sentence);
                var title = rest;
                if (due.Success && due.Index > m.Index + m.Length) title = sentence[(m.Index + m.Length)..due.Index];
                title = ArText.Clean(title.TrimEnd('،', '.', ' ', 'و'));
                items.Add(new JsonObject
                {
                    ["title"] = ArText.Shorten("" + title, 160), ["quote"] = sentence, ["assigneeText"] = m.Groups["who"].Value.Trim(),
                    ["dueText"] = due.Success ? due.Groups["due"].Value : null, ["refs"] = new JsonArray(s.Ref),
                    ["resolutionRef"] = s.Kind == "resolution" ? s.Ref : null,
                });
            }
        return new JsonObject { ["items"] = items };
    }

    private static JsonObject Link(JsonObject payload, List<Src> src)
    {
        var task = payload["task"] as JsonObject;
        var tokens = ArText.Tokens((task?["title"]?.GetValue<string>() ?? "") + " " + (task?["description"]?.GetValue<string>() ?? ""));
        var scored = src.Select(s =>
        {
            var rt = ArText.Tokens(s.Title + " " + s.Text);
            return (s.Ref, Score: ArText.Overlap(tokens, rt), Common: tokens.Where(rt.Contains).Take(4).ToArray());
        }).OrderByDescending(x => x.Score).ToList();
        var best = scored.FirstOrDefault();
        return new JsonObject
        {
            ["best"] = best.Ref is not null && best.Score >= 0.25
                ? new JsonObject { ["ref"] = best.Ref, ["confidence"] = Math.Round(Math.Min(0.95, best.Score), 2), ["reason"] = "كلمات مشتركة: " + string.Join("، ", best.Common) }
                : null,
            ["alternatives"] = new JsonArray(scored.Skip(1).Where(x => x.Score >= 0.15).Take(2)
                .Select(x => (JsonNode)new JsonObject { ["ref"] = x.Ref, ["confidence"] = Math.Round(Math.Min(0.9, x.Score), 2) }).ToArray()),
        };
    }

    private static readonly string[] Months = ["يناير", "فبراير", "مارس", "ابريل", "مايو", "يونيو", "يوليو", "اغسطس", "سبتمبر", "اكتوبر", "نوفمبر", "ديسمبر"];

    private static JsonObject Interpret(JsonObject payload)
    {
        var q = payload["query"]?.GetValue<string>() ?? payload["question"]?.GetValue<string>() ?? "";
        var norm = ArText.Normalize(q);
        var types = new JsonArray();
        var typeWords = new List<string>();
        void T(string type, params string[] words)
        {
            foreach (var w in words.Where(norm.Contains)) { if (!types.Any(x => x!.GetValue<string>() == type)) types.Add(type); typeWords.Add(w); }
        }
        T("resolution", "قرار", "قرارات", "توصيه", "توصيات");
        T("task", "مهمه", "مهام", "تكليف", "تكليفات");
        T("minutes", "محضر", "محاضر");
        T("meeting", "اجتماع", "اجتماعات");
        var overdue = norm.Contains("متاخر") || norm.Contains("تاخر");
        var open = overdue || norm.Contains("مفتوح") || norm.Contains("قائم") || norm.Contains("جاري") || norm.Contains("لم تنفذ") || norm.Contains("غير منفذ");
        string? bodyRef = null;
        var bodyTokens = new List<string>();
        foreach (var b in (payload["bodies"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var code = b["code"]?.GetValue<string>() ?? "";
            var nameTokens = ArText.Tokens(b["name"]?.GetValue<string>()).Where(t => t is not ("لجنه" or "مجلس" or "اللجنه")).ToArray();
            var hits = nameTokens.Where(t => norm.Contains(t)).ToArray();
            if ((code.Length > 1 && q.Contains(code, StringComparison.OrdinalIgnoreCase)) || hits.Length >= Math.Min(2, nameTokens.Length) && hits.Length > 0)
            {
                bodyRef = b["ref"]!.GetValue<string>();
                bodyTokens.AddRange(nameTokens);
                bodyTokens.Add(ArText.Normalize(code));
                break;
            }
        }
        string? from = null, to = null;
        var year = Regex.Match(q, @"\b(20\d{2})\b");
        var month = Months.Select((m, i) => (m, i)).FirstOrDefault(x => norm.Contains(x.m));
        if (year.Success && month.m is not null) { var d = new DateOnly(int.Parse(year.Value), month.i + 1, 1); from = d.ToString("yyyy-MM-dd"); to = d.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd"); }
        else if (year.Success) { from = $"{year.Value}-01-01"; to = $"{year.Value}-12-31"; }
        var skip = new HashSet<string>(["متاخره", "متاخر", "المتاخره", "مفتوحه", "قائمه", "جاريه", "اعرض", "ابحث", "عن", "جميع", "كل", "الخاصه", "التي", "لم", "تنفذ", "منفذه", "غير", "تاخر", "لجنه", "مجلس"]);
        var keywords = ArText.Tokens(q).Where(t => !skip.Contains(t) && !typeWords.Any(w => w.Contains(t) || t.Contains(w)) && !bodyTokens.Contains(t)
            && !Months.Contains(t) && !Regex.IsMatch(t, @"^20\d{2}$")).Take(5);
        return new JsonObject
        {
            ["types"] = types, ["status"] = open ? "open" : null, ["overdue"] = overdue, ["bodyRef"] = bodyRef, ["from"] = from, ["to"] = to,
            ["keywords"] = new JsonArray(keywords.Select(k => (JsonNode)k).ToArray()),
        };
    }

    private static JsonObject Executive(List<Src> src)
    {
        var metrics = src.Where(s => s.Kind == "metric").ToList();
        var head = metrics.Take(3).ToList();
        return new JsonObject
        {
            ["headline"] = head.Count == 0 ? null : Stmt(string.Join("، ", head.Select(m => m.Text)), head.Select(m => m.Ref).ToArray()),
            ["highlights"] = new JsonArray(metrics.Skip(3).Select(m => (JsonNode)Stmt(m.Text, m.Ref)).ToArray()),
            ["risks"] = new JsonArray(src.Where(s => s.Kind is "overdue_resolution" or "overdue_task").Take(6).Select(s => (JsonNode)Stmt(s.Text, s.Ref)).ToArray()),
            ["attention"] = new JsonArray(src.Where(s => s.Kind is "pending_minutes").Take(5).Select(s => (JsonNode)Stmt(s.Text, s.Ref)).ToArray()),
        };
    }

    [GeneratedRegex(@"[A-Z][A-Z0-9]{1,7}(?:[-/][A-Z0-9]{1,8}){1,4}")] private static partial Regex RecordNumber();

    private static JsonObject Intent(JsonObject payload)
    {
        var q = payload["question"]?.GetValue<string>() ?? "";
        var norm = ArText.Normalize(q);
        var number = RecordNumber().Match(q);
        var intent = norm.Contains("اخر اجراء") || norm.Contains("اخر تحديث") || norm.Contains("ماذا تم") || norm.Contains("اين وصل") ? "last_action"
            : (norm.Contains("متاخر") || norm.Contains("تاخر")) && norm.Contains("قرار") ? "overdue_resolutions"
            : norm.Contains("مهام") && (norm.Contains("مفتوح") || norm.Contains("قائم") || norm.Contains("متاخر")) ? "open_tasks"
            : norm.Contains("حاله") && (norm.Contains("قرار") || number.Success) ? "resolution_status"
            : "general";
        var interp = Interpret(payload);
        return new JsonObject
        {
            ["intent"] = intent, ["resolutionNumber"] = number.Success ? number.Value : null, ["bodyRef"] = interp["bodyRef"]?.DeepClone(),
            ["keywords"] = interp["keywords"]!.DeepClone(),
        };
    }

    private static JsonObject Answer(List<Src> src)
    {
        if (src.Count == 0) return new JsonObject { ["noAnswer"] = true, ["answer"] = new JsonArray() };
        return new JsonObject
        {
            ["noAnswer"] = false,
            ["answer"] = new JsonArray(src.Take(6).Select(s => (JsonNode)Stmt(ArText.Shorten(s.Text, 300), s.Ref)).ToArray()),
        };
    }

    /// <summary>وضع اختبار: يضيف مراجع ومسؤولين وتواريخ لا وجود لها في البيانات.</summary>
    private static void Hallucinate(string fn, JsonObject o)
    {
        var fake = Stmt("قررت اللجنة شراء منظومة جديدة بقيمة عشرة ملايين ريال", "S999");
        switch (fn)
        {
            case AiFunctions.MinutesSummary: ((JsonArray)o["decisions"]!).Add(fake); ((JsonArray)o["points"]!).Add(new JsonObject { ["text"] = "عبارة بلا مرجع", ["refs"] = new JsonArray() }); break;
            case AiFunctions.ExtractDecisions:
                ((JsonArray)o["items"]!).Add(new JsonObject { ["kind"] = "decision", ["title"] = "قرار مختلق", ["text"] = "قررت اللجنة إنشاء إدارة جديدة", ["quote"] = "قررت اللجنة إنشاء إدارة جديدة", ["refs"] = new JsonArray("S1") });
                break;
            case AiFunctions.ExtractTasks:
                foreach (var it in ((JsonArray)o["items"]!).OfType<JsonObject>()) { it["assigneeText"] = "مدير المشاريع الوهمي"; it["dueText"] = "2027-01-01"; }
                ((JsonArray)o["items"]!).Add(new JsonObject { ["title"] = "مهمة مختلقة", ["quote"] = "تكليف جهة غير موجودة بإعداد خطة", ["assigneeText"] = "جهة غير موجودة", ["dueText"] = "2027-02-01", ["refs"] = new JsonArray("S999") });
                break;
            case AiFunctions.Ask: ((JsonArray)o["answer"]!).Add(new JsonObject { ["text"] = "القرار رقم ABC-9999 متأخر منذ عام", ["refs"] = new JsonArray("F999") }); break;
            case AiFunctions.ExecutiveSummary: ((JsonArray)o["risks"]!).Add(Stmt("خطر مختلق دون مرجع", "F999")); break;
            case AiFunctions.LinkTask: o["best"] = new JsonObject { ["ref"] = "R999", ["confidence"] = 0.99, ["reason"] = "مختلق" }; break;
            case AiFunctions.AgendaDraft: ((JsonArray)o["items"]!).Add(new JsonObject { ["title"] = "بند مختلق", ["reason"] = "", ["itemType"] = "for_decision", ["refs"] = new JsonArray("S999") }); break;
        }
    }
}
