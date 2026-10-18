using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ccms.Modules.Ai;

/// <summary>نقاط نهاية الوظائف العشر. الترتيب ثابت: المفتاح (404) ← صلاحية السجل (404/403) ← ai.use (403) ← الحدود (429) ← المزوّد (503) ← التصنيف (403).
/// كل استجابة مسودة موسومة بأنها مولدة آليًا، بمراجع لسجلات حقيقية، ولا تُطبق على أي سجل أساسي.</summary>
internal static class Functions
{
    public sealed record NotesRequest([property: Required(ErrorMessage = "الملاحظات مطلوبة"), MinLength(20, ErrorMessage = "الملاحظات قصيرة جدًا"), MaxLength(20000)] string Notes);
    public sealed record TextRequest([property: MaxLength(20000)] string? Text);
    public sealed record LinkRequest([property: Required, MinLength(5), MaxLength(500)] string Title, [property: MaxLength(3000)] string? Description);
    public sealed record QueryRequest([property: Required(ErrorMessage = "اكتب سؤالًا"), MinLength(3), MaxLength(500)] string Query);
    public sealed record ExecRequest(Guid? BodyId, DateOnly? From, DateOnly? To);
    public sealed record AgendaRequest(Guid? MeetingId);

    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/minutes/{id:guid}/summary", Summary);
        g.MapPost("/bodies/{id:guid}/agenda-draft", AgendaDraft);
        g.MapPost("/minutes/{id:guid}/draft-from-notes", DraftFromNotes).Validate<NotesRequest>();
        g.MapPost("/minutes/{id:guid}/extract-decisions", ExtractDecisions);
        g.MapPost("/minutes/{id:guid}/extract-tasks", ExtractTasks);
        g.MapPost("/tasks/{id:guid}/link-suggestion", LinkForTask);
        g.MapPost("/bodies/{id:guid}/link-suggestion", LinkForText).Validate<LinkRequest>();
        g.MapPost("/search", SmartSearch).Validate<QueryRequest>();
        g.MapPost("/executive-summary", Executive);
        g.MapPost("/ask", Ask).Validate<QueryRequest>();
    }

    private static async Task DemandAiUse(IAccessService access, Guid bodyId, CancellationToken ct)
    {
        if (!await access.HasAsync(Permissions.AiUse, Scope.Body(bodyId), ct)) throw new ForbiddenException(permission: Permissions.AiUse);
    }

    private static object Result(AiResult r, object? extra = null) => new
    {
        outputId = r.OutputId, generated = true, draft = true, testOnly = r.TestOnly, provider = r.Provider, model = r.Model,
        content = r.Content, citations = r.Citations, warnings = r.Warnings, excludedSources = r.Excluded, redactions = r.Redactions, noSources = r.NoSources, extra,
    };

    // ---------------- 1. تلخيص المحضر ----------------
    private static async Task<IResult> Summary(Guid id, AiService svc, AiData data, IAccessService access, CancellationToken ct)
    {
        await svc.DemandFunctionAsync(AiFunctions.MinutesSummary, "minutes", id, ct);
        await access.DemandAsync(Permissions.MinutesView, Scope.Record("minutes", id), ct);
        var m = await data.MinutesAsync(id, ct);
        await DemandAiUse(access, m.BodyId, ct);
        var r = await svc.RunAsync(new AiRun(AiFunctions.MinutesSummary, "minutes", id, m.BodyId, AiData.MinutesSources(m), new JsonObject { ["title"] = m.Title },
            new GroundingContext { Sources = [] })
        { EmptyContent = new JsonObject { ["points"] = new JsonArray(), ["decisions"] = new JsonArray(), ["followUps"] = new JsonArray() } }, ct);
        return Results.Ok(Result(r));
    }

    // ---------------- 2. مسودة جدول الأعمال ----------------
    private static async Task<IResult> AgendaDraft(Guid id, AgendaRequest? req, AiService svc, AiData data, IAccessService access, CancellationToken ct)
    {
        await svc.DemandFunctionAsync(AiFunctions.AgendaDraft, "body", id, ct);
        await access.DemandAsync(Permissions.MeetingManage, Scope.Body(id), ct);
        await DemandAiUse(access, id, ct);
        var (sources, bodyName) = await data.OpenItemsAsync(id, ct);
        var r = await svc.RunAsync(new AiRun(AiFunctions.AgendaDraft, req?.MeetingId is { } mid ? "meeting" : "body", req?.MeetingId ?? id, id, sources,
            new JsonObject { ["body"] = bodyName }, new GroundingContext { Sources = [] })
        { EmptyContent = new JsonObject { ["items"] = new JsonArray() } }, ct);
        return Results.Ok(Result(r));
    }

    // ---------------- 3. مسودة المحضر من الملاحظات ----------------
    private static async Task<IResult> DraftFromNotes(Guid id, NotesRequest req, AiService svc, AiData data, IAccessService access, CancellationToken ct)
    {
        await svc.DemandFunctionAsync(AiFunctions.MinutesDraft, "minutes", id, ct);
        await access.DemandAsync(Permissions.MinutesEdit, Scope.Record("minutes", id), ct);
        var m = await data.MinutesAsync(id, ct);
        await DemandAiUse(access, m.BodyId, ct);
        if (m.Status != "draft") throw new ConflictException("minutes-not-draft", "المسودة الآلية متاحة للمحضر في مرحلة المسودة فقط");
        var cls = AiData.Max(m.Classification, m.BodyClassification);
        var n = 0;
        var notes = req.Notes.Split('\n').Select(ArText.Clean).Where(l => l.Length > 0).Take(400)
            .Select(l => new AiSource($"N{++n}", "note", "", l, cls)).ToList();
        var extra = new JsonObject
        {
            ["meeting"] = m.Title,
            ["agendaItems"] = new JsonArray(m.Agenda.Select(a => (JsonNode)new JsonObject { ["ref"] = $"A{a.Position}", ["title"] = a.Title }).ToArray()),
            ["sectionKeys"] = new JsonArray(m.Sections.Select(s => (JsonNode)new JsonObject { ["key"] = s.Key, ["title"] = s.Title }).ToArray()),
        };
        var r = await svc.RunAsync(new AiRun(AiFunctions.MinutesDraft, "minutes", id, m.BodyId, notes, extra, new GroundingContext
        {
            Sources = [], AgendaItems = AiData.AgendaMap(m), SectionKeys = m.Sections.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.First().Title),
        }), ct);
        return Results.Ok(Result(r, new { notesLines = notes.Count }));
    }

    // ---------------- 5. استخراج القرارات والتوصيات ----------------
    private static async Task<IResult> ExtractDecisions(Guid id, TextRequest? req, AiService svc, AiData data, IAccessService access, CancellationToken ct)
    {
        await svc.DemandFunctionAsync(AiFunctions.ExtractDecisions, "minutes", id, ct);
        await access.DemandAsync(Permissions.MinutesEdit, Scope.Record("minutes", id), ct);
        var m = await data.MinutesAsync(id, ct);
        await DemandAiUse(access, m.BodyId, ct);
        var sources = string.IsNullOrWhiteSpace(req?.Text)
            ? AiData.MinutesSources(m, withResolutions: false)
            : [new AiSource("S1", "text", "نص مُدخل", req!.Text!, AiData.Max(m.Classification, m.BodyClassification), "minutes", m.Id, $"/minutes/{m.Id}", "النص المُدخل", m.BodyId)];
        var r = await svc.RunAsync(new AiRun(AiFunctions.ExtractDecisions, "minutes", id, m.BodyId, sources, new JsonObject { ["meeting"] = m.Title },
            new GroundingContext { Sources = [], AgendaItems = AiData.AgendaMap(m) })
        { EmptyContent = new JsonObject { ["items"] = new JsonArray() } }, ct);
        // تنبيه للتكرار مع قرارات مسجلة مسبقًا في المحضر
        foreach (var item in (r.Content["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var tokens = ArText.Tokens(item["text"]?.GetValue<string>());
            var dup = m.Resolutions.Select(x => (x, s: ArText.Overlap(tokens, ArText.Tokens(x.Title + " " + x.Text)))).OrderByDescending(x => x.s).FirstOrDefault();
            if (dup.x is not null && dup.s >= 0.6) item["possibleDuplicateOf"] = new JsonObject { ["id"] = dup.x.Id.ToString(), ["title"] = dup.x.Title, ["number"] = dup.x.Number };
        }
        return Results.Ok(Result(r));
    }

    // ---------------- 6. استخراج المهام والمسؤولين والتواريخ ----------------
    private static async Task<IResult> ExtractTasks(Guid id, AiService svc, AiData data, IAccessService access, CancellationToken ct)
    {
        await svc.DemandFunctionAsync(AiFunctions.ExtractTasks, "minutes", id, ct);
        var perms = await access.GetPermissionsAsync(Scope.Record("minutes", id), ct);
        if (!perms.Contains(Permissions.MinutesView)) throw new NotFoundException();
        if (!perms.Contains(Permissions.MinutesEdit) && !perms.Contains(Permissions.ResolutionManage)) throw new ForbiddenException(permission: Permissions.ResolutionManage);
        var m = await data.MinutesAsync(id, ct);
        await DemandAiUse(access, m.BodyId, ct);
        var r = await svc.RunAsync(new AiRun(AiFunctions.ExtractTasks, "minutes", id, m.BodyId, AiData.MinutesSources(m), new JsonObject { ["meetingDate"] = Labels.Date(m.MeetingDate) },
            new GroundingContext { Sources = [], Directory = await data.DirectoryAsync(m.BodyId, ct), MeetingDate = m.MeetingDate })
        { EmptyContent = new JsonObject { ["items"] = new JsonArray() } }, ct);
        return Results.Ok(Result(r));
    }

    // ---------------- 7. اقتراح ربط المهمة بالقرار ----------------
    private static async Task<IResult> LinkForTask(Guid id, AiService svc, AiData data, IAccessService access, Ccms.BuildingBlocks.Data.Db db, CancellationToken ct)
    {
        await svc.DemandFunctionAsync(AiFunctions.LinkTask, "task", id, ct);
        await access.DemandAsync(Permissions.TaskManage, Scope.Task(id), ct);
        var t = await db.QuerySingleAsync("SELECT body_id, title_ar, coalesce(description, ''), resolution_id FROM tasks.tasks WHERE id = @id", new { id },
            x => (Body: x.GetGuid(0), Title: x.GetString(1), Desc: x.GetString(2), Res: x.IsDBNull(3) ? (Guid?)null : x.GetGuid(3)), ct);
        await DemandAiUse(access, t.Body, ct);
        return Results.Ok(await LinkAsync(svc, data, t.Body, "task", id, t.Title, t.Desc, ct));
    }

    private static async Task<IResult> LinkForText(Guid id, LinkRequest req, AiService svc, AiData data, IAccessService access, CancellationToken ct)
    {
        await svc.DemandFunctionAsync(AiFunctions.LinkTask, "body", id, ct);
        await access.DemandAsync(Permissions.TaskManage, Scope.Body(id), ct);
        await DemandAiUse(access, id, ct);
        return Results.Ok(await LinkAsync(svc, data, id, "body", id, req.Title, req.Description ?? "", ct));
    }

    private static async Task<object> LinkAsync(AiService svc, AiData data, Guid bodyId, string subjectType, Guid subjectId, string title, string desc, CancellationToken ct)
    {
        var candidates = await data.ResolutionCandidatesAsync(bodyId, ct);
        var cls = await data.BodyClassificationAsync(bodyId, ct);
        var r = await svc.RunAsync(new AiRun(AiFunctions.LinkTask, subjectType, subjectId, bodyId, candidates,
            new JsonObject { ["task"] = new JsonObject { ["title"] = title, ["description"] = desc }, ["taskClassification"] = cls }, new GroundingContext { Sources = [] })
        { EmptyContent = new JsonObject { ["best"] = null, ["alternatives"] = new JsonArray() } }, ct);
        return Result(r);
    }

    // ---------------- 8. البحث باللغة الطبيعية ----------------
    private static async Task<IResult> SmartSearch(QueryRequest req, AiService svc, AiData data, IAccessService access, ISearchService search, CancellationToken ct)
    {
        await svc.DemandFunctionAsync(AiFunctions.SmartSearch, "query", null, ct);
        var scope = await access.GetBodyFilterAsync(Permissions.AiUse, ct);
        if (!scope.All && scope.BodyIds.Length == 0) throw new ForbiddenException(permission: Permissions.AiUse);
        var (bodies, map) = await data.BodiesAsync(ct);
        var r = await svc.RunAsync(new AiRun(AiFunctions.SmartSearch, "query", null, null, [], new JsonObject { ["query"] = req.Query, ["bodies"] = bodies },
            new GroundingContext { Sources = [], Bodies = map }) { Save = false }, ct);
        var f = r.Content;
        var keywords = string.Join(' ', (f["keywords"] as JsonArray ?? []).Select(x => x!.GetValue<string>()));
        Guid? bodyId = f["bodyId"] is JsonValue bv && Guid.TryParse(bv.GetValue<string>(), out var b) ? b : null;
        DateOnly? D(string k) => f[k] is JsonValue v && DateOnly.TryParseExact(v.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        var types = (f["types"] as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToArray();
        var hits = (await search.SearchAsync(new SearchQuery(keywords.Length > 0 ? keywords : null, types.Length > 0 ? types : null, bodyId,
            f["status"]?.GetValue<string>(), f["overdue"]?.GetValue<bool>() ?? false, D("from"), D("to"), 25), ct))
            .Where(h => scope.All || scope.BodyIds.Contains(h.BodyId)).ToList();
        var settings = await svc.SettingsAsync(ct);
        var allowed = DataPolicy.AllowedRank(settings);
        var citations = hits.Select((h, i) => new Citation($"H{i + 1}", h.Type, h.Id, h.Reference ?? h.Title, h.Link)).ToList();
        var content = new JsonObject { ["interpretation"] = f.DeepClone(), ["resultCount"] = hits.Count };
        var outputId = await svc.SaveAsync(AiFunctions.SmartSearch, "query", null, null, content, citations, r.Warnings, r, hits.Count, ct);
        return Results.Ok(new
        {
            outputId, generated = true, draft = true, testOnly = r.TestOnly, provider = r.Provider, model = r.Model, interpretation = f, warnings = r.Warnings,
            items = hits.Select(h => new { h.Type, h.Id, h.Title, h.Reference, h.BodyId, h.BodyName, h.Status, h.Date, h.Snippet, h.Link, h.DueDate,
                aiProcessable = DataPolicy.Rank(h.Classification) <= allowed }),
        });
    }

    // ---------------- 9. الملخص التنفيذي ----------------
    private static async Task<IResult> Executive(ExecRequest? req, AiService svc, AiData data, IAccessService access, CancellationToken ct)
    {
        await svc.DemandFunctionAsync(AiFunctions.ExecutiveSummary, req?.BodyId is null ? "portfolio" : "body", req?.BodyId, ct);
        var exec = await access.HasAsync(Permissions.ReportsExecutive, Scope.System, ct);
        if (req?.BodyId is { } bid)
        {
            if (!exec) await access.DemandAsync(Permissions.ReportsBody, Scope.Body(bid), ct);
            await DemandAiUse(access, bid, ct);
        }
        else if (!exec) throw new ForbiddenException(permission: Permissions.ReportsExecutive);
        else if (!await access.HasAsync(Permissions.AiUse, Scope.System, ct)) throw new ForbiddenException(permission: Permissions.AiUse);
        var to = req?.To ?? data.TodayLocal;
        var from = req?.From ?? to.AddDays(-90);
        if (from > to) throw AppValidationException.For("from", "بداية الفترة بعد نهايتها");
        var facts = await data.ExecutiveFactsAsync(req?.BodyId, from, to, ct);
        var r = await svc.RunAsync(new AiRun(AiFunctions.ExecutiveSummary, req?.BodyId is null ? "portfolio" : "body", req?.BodyId, req?.BodyId, facts,
            new JsonObject { ["from"] = Labels.Date(from), ["to"] = Labels.Date(to) }, new GroundingContext { Sources = [] }), ct);
        return Results.Ok(Result(r, new { from, to, facts = facts.Select(f => new { f.Ref, f.Kind, f.Title, f.Text, f.Link }) }));
    }

    // ---------------- 10. الأسئلة ----------------
    private static async Task<IResult> Ask(QueryRequest req, AiService svc, AiData data, IAccessService access, CancellationToken ct)
    {
        await svc.DemandFunctionAsync(AiFunctions.Ask, "query", null, ct);
        var scope = await access.GetBodyFilterAsync(Permissions.AiUse, ct);
        if (!scope.All && scope.BodyIds.Length == 0) throw new ForbiddenException(permission: Permissions.AiUse);
        var (provider, s) = await svc.DemandProviderAsync(AiFunctions.Ask, false, "query", null, ct);
        await svc.DemandQuotaAsync(AiFunctions.Ask, s, "query", null, ct);
        var (bodies, map) = await data.BodiesAsync(ct);
        var intent = await svc.RunAsync(new AiRun(AiFunctions.Ask, "query", null, null, [], new JsonObject
        {
            ["question"] = req.Query, ["bodies"] = bodies, ["intents"] = new JsonArray("overdue_resolutions", "last_action", "open_tasks", "resolution_status", "general"),
        }, new GroundingContext { Sources = [], Bodies = map }) { PromptKey = AiFunctions.Ask + ":intent", Counted = false, Save = false }, ct);
        var i = intent.Content;
        Guid? bodyId = i["bodyId"] is JsonValue bv && Guid.TryParse(bv.GetValue<string>(), out var b) ? b : null;
        var keywords = (i["keywords"] as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToArray();
        var facts = (await data.AskFactsAsync(i["intent"]!.GetValue<string>(), i["resolutionNumber"]?.GetValue<string>(), bodyId, keywords, req.Query, ct))
            .Where(f => f.BodyId is null || scope.All || scope.BodyIds.Contains(f.BodyId.Value)).ToList();
        var r = await svc.RunAsync(new AiRun(AiFunctions.Ask, "query", null, bodyId, facts, new JsonObject { ["question"] = req.Query }, new GroundingContext { Sources = [] })
        { EmptyContent = new JsonObject { ["noAnswer"] = true, ["answer"] = new JsonArray() } }, ct);
        var warnings = r.Warnings.ToList();
        if (r.Excluded > 0) warnings.Add($"استُبعد {r.Excluded} سجلًا من المعالجة الآلية لأن تصنيفه أعلى من المسموح؛ يمكنك الاطلاع عليه مباشرة");
        return Results.Ok(new
        {
            outputId = r.OutputId, generated = true, draft = true, testOnly = r.TestOnly, provider = r.Provider, model = r.Model, intent = i, content = r.Content,
            citations = r.Citations, warnings, noSources = r.NoSources,
            facts = facts.Select(f => new { f.Ref, f.Kind, f.Title, f.Text, f.Link, type = f.RecordType, id = f.RecordId, label = f.Label }),
        });
    }
}
