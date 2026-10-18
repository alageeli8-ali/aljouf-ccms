using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.Extensions.Logging;

namespace Ccms.Modules.Ai;

internal sealed record AiRun(string Function, string SubjectType, Guid? SubjectId, Guid? BodyId, List<AiSource> Sources, JsonObject Extra, GroundingContext Context)
{
    /// <summary>مفتاح القالب إن اختلف عن الوظيفة (مثل ask:intent).</summary>
    public string? PromptKey { get; init; }
    /// <summary>يُحتسب في حدود الطلبات (طلب واحد لكل إجراء مستخدم وإن تعددت الاستدعاءات).</summary>
    public bool Counted { get; init; } = true;
    public bool Save { get; init; } = true;
    /// <summary>إن خلت المصادر: يُعاد مخرج فارغ دون استدعاء المزوّد.</summary>
    public JsonObject? EmptyContent { get; init; }
}

internal sealed record AiResult(Guid? OutputId, JsonObject Content, List<Citation> Citations, List<string> Warnings, bool TestOnly, string Provider, string Model,
    int Excluded, int Redactions, bool NoSources);

/// <summary>عدّاد الطلبات الجارية لدى المزوّد في هذه النسخة (لشاشة أثر التعطيل).</summary>
internal sealed class AiInflight
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public IDisposable Enter() { Interlocked.Increment(ref _count); return new Exit(this); }
    private sealed class Exit(AiInflight o) : IDisposable { public void Dispose() => Interlocked.Decrement(ref o._count); }
}

/// <summary>منسق كل طلب ذكاء اصطناعي (22 §3): المفتاح ← الحدود ← جاهزية المزوّد ← التصنيف ← التقليل ← الاستدعاء ← التحقق ← الحفظ مسودة.
/// كل خطوة تُسجل في سجل التشغيل دون محتوى. لا يكتب في أي جدول خارج مخطط ai.</summary>
internal sealed class AiService(Db db, IFeatureService features, AiProviderRegistry providers, ICurrentUser me, IClock clock, INotifier notifier,
    IAuditWriter audit, AiInflight inflight, ILogger<AiService> log)
{
    public async Task<AiSettings> SettingsAsync(CancellationToken ct) => await AiSettings.LoadAsync(features, ct);

    /// <summary>الوظيفة مفعلة فعليًا (مع المفتاح الأعلى)؛ وإلا يُسجل الرفض ويُعاد 404 — لا تنفيذ ولا نتيجة تبدو مولدة.</summary>
    public async Task DemandFunctionAsync(string function, string? subjectType = null, Guid? subjectId = null, CancellationToken ct = default)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        var flag = AiFunctions.Flag[function];
        if (await features.IsEnabledAsync(flag, ct)) return;
        await LogAsync(new LogRow(function, "blocked_disabled") { SubjectType = subjectType, SubjectId = subjectId, ErrorCode = await features.IsEnabledAsync(FeatureKeys.AiEnabled, ct) ? flag : FeatureKeys.AiEnabled }, ct);
        throw new FeatureDisabledException(await features.IsEnabledAsync(FeatureKeys.AiEnabled, ct) ? flag : FeatureKeys.AiEnabled);
    }

    public async Task<(IAiProvider Provider, AiSettings Settings)> DemandProviderAsync(string function, bool transcription, string? subjectType, Guid? subjectId, CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        var (p, ready) = await providers.ResolveAsync(s, transcription, ct);
        if (p is null || !ready.Ready)
        {
            await LogAsync(new LogRow(function, "not_configured") { SubjectType = subjectType, SubjectId = subjectId, ErrorCode = ready.Reason, Provider = s.Provider }, ct);
            throw new ProviderUnavailableException("ai", ready.Reason);
        }
        return (p, s);
    }

    public async Task DemandQuotaAsync(string function, AiSettings s, string? subjectType, Guid? subjectId, CancellationToken ct)
    {
        var u = await UsageAsync(ct);
        string? limit = u.MyToday >= s.DailyPerUser ? "daily_per_user" : u.MonthRequests >= s.MonthlyRequests ? "monthly_requests" : u.MonthTokens >= s.MonthlyTokens ? "monthly_tokens" : null;
        if (limit is null) return;
        await LogAsync(new LogRow(function, "blocked_quota") { SubjectType = subjectType, SubjectId = subjectId, ErrorCode = limit, Provider = s.Provider }, ct);
        throw new AiQuotaExceededException(limit, limit switch
        {
            "daily_per_user" => $"بلغت حد الطلبات اليومي ({s.DailyPerUser})؛ يُتاح مجددًا غدًا",
            "monthly_requests" => $"بلغ النظام حد الطلبات الشهري ({s.MonthlyRequests}); راجع مدير النظام",
            _ => $"بلغ النظام حد الاستهلاك الشهري ({s.MonthlyTokens} وحدة)؛ راجع مدير النظام",
        });
    }

    public async Task<AiResult> RunAsync(AiRun run, CancellationToken ct)
    {
        var fn = run.Function;
        var (provider, s) = await DemandProviderAsync(fn, false, run.SubjectType, run.SubjectId, ct);
        if (run.Counted) await DemandQuotaAsync(fn, s, run.SubjectType, run.SubjectId, ct);

        // 1) التصنيف: لا يُرسل ما يتجاوز السياسة
        var allowed = DataPolicy.AllowedRank(s);
        var over = run.Sources.Where(x => DataPolicy.Rank(x.Classification) > allowed).ToList();
        var highest = run.Sources.Count == 0 ? "public" : run.Sources.OrderByDescending(x => DataPolicy.Rank(x.Classification)).First().Classification;
        var row = new LogRow(fn, "succeeded") { SubjectType = run.SubjectType, SubjectId = run.SubjectId, BodyId = run.BodyId, Provider = provider.Code, PromptVersion = Prompts.Version, MaxClassification = highest };
        var blocked = AiFunctions.IsSubjectBound(fn) ? over.Count > 0
            : run.Sources.Count == 0 ? allowed < 0
            : over.Count == run.Sources.Count && run.EmptyContent is null;
        if (blocked)
        {
            await LogAsync(row with { Status = "blocked_classification", ErrorCode = "classification_" + highest, SourceCount = run.Sources.Count, Excluded = over.Count }, ct);
            throw new AiClassificationBlockedException(highest, DataPolicy.AllowedLabel(s), over.Count);
        }
        var sources = run.Sources.Except(over).ToList();
        row = row with { Excluded = over.Count, SourceCount = sources.Count, MaxClassification = sources.Count == 0 ? null : sources.Max(x => DataPolicy.Rank(x.Classification)) is var r && r >= 0 ? Classification.Levels[r] : null };

        // 2) لا مصادر: لا يُستدعى المزوّد
        if (sources.Count == 0 && run.EmptyContent is not null)
        {
            await LogAsync(row with { Status = "no_sources" }, ct);
            return new AiResult(null, run.EmptyContent, [], [], provider.TestOnly, provider.Code, provider.TestOnly ? MockAiProvider.ModelName : s.Model, over.Count, 0, true);
        }

        // 3) التقليل لمزوّد خارجي: حجب المعرّفات الشخصية
        var redactions = 0;
        if (s.Hosting == "external")
        {
            sources = sources.Select(x =>
            {
                var (t, n1) = Redactor.Apply(x.Text);
                var (ti, n2) = Redactor.Apply(x.Title);
                redactions += n1 + n2;
                return x with { Text = t, Title = ti };
            }).ToList();
            foreach (var k in run.Extra.Select(kv => kv.Key).ToList())
                if (run.Extra[k] is JsonValue v && v.TryGetValue<string>(out var str)) { var (t, n) = Redactor.Apply(str); redactions += n; run.Extra[k] = t; }
        }
        var payload = new JsonObject
        {
            ["function"] = fn, ["today"] = clock.TodayLocal.ToString("yyyy-MM-dd"),
            ["sources"] = new JsonArray(sources.Select(x =>
            {
                var o = new JsonObject { ["ref"] = x.Ref, ["kind"] = x.Kind, ["title"] = x.Title, ["text"] = x.Text };
                if (x.Extra is not null) foreach (var (k, v) in x.Extra) o[k] = v?.DeepClone();
                return (JsonNode)o;
            }).ToArray()),
        };
        foreach (var (k, v) in run.Extra) payload[k] = v?.DeepClone();
        var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToJsonString()))).ToLowerInvariant();
        row = row with { Redactions = redactions, InputSha256 = inputHash, Counted = run.Counted };

        // 4) الاستدعاء (محاولة تصحيح واحدة لمخرج غير صالح من مزوّد حقيقي)
        var sw = Stopwatch.StartNew();
        var key = run.PromptKey ?? fn;
        var ctx = new GroundingContext
        {
            Sources = sources, Directory = run.Context.Directory, AgendaItems = run.Context.AgendaItems, SectionKeys = run.Context.SectionKeys,
            Bodies = run.Context.Bodies, MeetingDate = run.Context.MeetingDate, Today = clock.TodayLocal,
        };
        AiCompletion? completion = null;
        Grounded grounded;
        int inTok = 0, outTok = 0;
        var estimated = false;
        using (inflight.Enter())
        {
            var attempts = provider.TestOnly ? 1 : 2;
            while (true)
            {
                try
                {
                    completion = await provider.CompleteAsync(s, Prompts.Build(key, payload, s.Timeout), ct);
                    inTok += completion.InputTokens ?? 0;
                    outTok += completion.OutputTokens ?? 0;
                    estimated |= completion.UsageEstimated;
                    grounded = Grounding.Validate(key, completion.Json, ctx);
                    break;
                }
                catch (AiProviderException e)
                {
                    var status = e.Kind == "timeout" ? "timeout" : e.Transient ? "provider_unavailable" : "failed";
                    await LogAsync(row with { Status = status, ErrorCode = e.Kind, DurationMs = (int)sw.ElapsedMilliseconds, InputTokens = inTok, OutputTokens = outTok, Estimated = estimated }, ct);
                    log.LogWarning("AI {Function} provider failure {Kind}", fn, e.Kind);
                    if (e.Transient) throw new ProviderUnavailableException("ai", e.Kind);
                    throw new AiRequestFailedException(e.Kind, e.Message);
                }
                catch (AiOutputInvalidException e)
                {
                    if (--attempts > 0) continue;
                    await LogAsync(row with { Status = "invalid_output", ErrorCode = "invalid_json", DurationMs = (int)sw.ElapsedMilliseconds, InputTokens = inTok, OutputTokens = outTok, Estimated = estimated }, ct);
                    throw new AiRequestFailedException("invalid_output", "أعاد المزوّد مخرجًا غير صالح ولم يُحفظ شيء: " + e.Message);
                }
            }
        }
        row = row with { DurationMs = (int)sw.ElapsedMilliseconds, InputTokens = inTok, OutputTokens = outTok, Estimated = estimated, Model = completion!.Model };

        // 5) عُطلت الخدمة أثناء المعالجة: تُحفظ محجوبة ولا تُعرض
        var stillOn = await features.IsEnabledAsync(AiFunctions.Flag[fn], ct);
        Guid? outputId = null;
        if (run.Save || !stillOn)
        {
            outputId = Guid.CreateVersion7();
            await db.InTransactionAsync(async tx =>
            {
                await tx.ExecuteAsync("""
                    INSERT INTO ai.outputs (id, function, subject_type, subject_id, body_id, requested_by, status, content, citations, warnings, provider, model, test_only,
                                            prompt_version, input_sha256, classification, source_count, held_reason)
                    VALUES (@id, @fn, @st, @sid, @body, @me, @status, @content::jsonb, @cit::jsonb, @warn::jsonb, @provider, @model, @test, @pv, @hash, @cls, @n, @held)
                    """, new
                {
                    id = outputId, fn, st = run.SubjectType, sid = run.SubjectId, body = run.BodyId, me = me.UserId, status = stillOn ? "draft" : "held",
                    content = grounded.Content.ToJsonString(), cit = Json.Serialize(grounded.Citations), warn = Json.Serialize(grounded.Warnings),
                    provider = provider.Code, model = completion.Model, test = provider.TestOnly, pv = Prompts.Version, hash = inputHash,
                    cls = row.MaxClassification ?? "public", n = sources.Count, held = stillOn ? null : "disabled_during_processing",
                }, ct);
                await audit.WriteAsync(tx, new AuditEntry("AI", stillOn ? "ai.output_created" : "ai.output_held", "ai_output", outputId.ToString(),
                    After: new { function = fn, subjectType = run.SubjectType, subjectId = run.SubjectId, provider = provider.Code, testOnly = provider.TestOnly, sources = sources.Count }), ct);
            }, ct);
        }
        await LogAsync(row with { Status = stillOn ? "succeeded" : "held", OutputId = outputId }, ct);
        await AlertsAsync(s, ct);
        if (!stillOn) throw new AiHeldException(outputId!.Value);
        return new AiResult(outputId, grounded.Content, grounded.Citations, grounded.Warnings, provider.TestOnly, provider.Code, completion.Model, over.Count, redactions, false);
    }

    /// <summary>حفظ مخرج مركّب في الخادم من نتيجة استدعاء ناجح (مثل نتائج البحث بعد تفسير السؤال). لا يُستدعى إلا بعد RunAsync ناجح.</summary>
    public async Task<Guid> SaveAsync(string fn, string subjectType, Guid? subjectId, Guid? bodyId, JsonObject content, List<Citation> citations, List<string> warnings,
        AiResult from, int sourceCount, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync("""
            INSERT INTO ai.outputs (id, function, subject_type, subject_id, body_id, requested_by, status, content, citations, warnings, provider, model, test_only,
                                    prompt_version, input_sha256, classification, source_count)
            VALUES (@id, @fn, @st, @sid, @body, @me, 'draft', @content::jsonb, @cit::jsonb, @warn::jsonb, @provider, @model, @test, @pv, @hash, 'public', @n)
            """, new
        {
            id, fn, st = subjectType, sid = subjectId, body = bodyId, me = me.UserId, content = content.ToJsonString(), cit = Json.Serialize(citations),
            warn = Json.Serialize(warnings), provider = from.Provider, model = from.Model, test = from.TestOnly, pv = Prompts.Version,
            hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToJsonString()))).ToLowerInvariant(), n = sourceCount,
        }, ct);
        return id;
    }

    // ---------------- الاستهلاك والحدود ----------------

    public sealed record Usage(long MyToday, long MonthRequests, long MonthTokens, string Period);

    public async Task<Usage> UsageAsync(CancellationToken ct)
    {
        var today = clock.TodayLocal;
        var dayStart = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), Riyadh);
        var monthStart = TimeZoneInfo.ConvertTimeToUtc(new DateOnly(today.Year, today.Month, 1).ToDateTime(TimeOnly.MinValue), Riyadh);
        var r = await db.QuerySingleAsync("""
            SELECT (SELECT count(*) FROM ai.requests WHERE counted AND user_id = @me AND created_at >= @day),
                   (SELECT count(*) FROM ai.requests WHERE counted AND created_at >= @month),
                   (SELECT coalesce(sum(coalesce(input_tokens,0) + coalesce(output_tokens,0)), 0)::bigint FROM ai.requests WHERE created_at >= @month)
            """, new { me = me.UserId, day = dayStart, month = monthStart }, x => new Usage(x.GetInt64(0), x.GetInt64(1), x.GetInt64(2), today.ToString("yyyy-MM")), ct);
        return r!;
    }

    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    /// <summary>تنبيه مديري النظام مرة واحدة لكل (شهر، مقياس، عتبة) عند بلوغ نسبة التنبيه ثم عند بلوغ الحد.</summary>
    public async Task AlertsAsync(AiSettings s, CancellationToken ct)
    {
        var u = await UsageAsync(ct);
        foreach (var (metric, value, limit) in new[] { ("requests", u.MonthRequests, s.MonthlyRequests), ("tokens", u.MonthTokens, s.MonthlyTokens) })
        {
            if (limit <= 0) continue;
            var pct = (int)(value * 100 / limit);
            foreach (var threshold in new[] { s.AlertPercent, 100 }.Where(t => pct >= t))
            {
                await db.InTransactionAsync(async tx =>
                {
                    var inserted = await tx.ExecuteAsync("""
                        INSERT INTO ai.usage_alerts (period, metric, threshold, value, limit_value) VALUES (@p, @metric, @threshold, @value, @limit) ON CONFLICT DO NOTHING
                        """, new { p = u.Period, metric, threshold, value, limit }, ct);
                    if (inserted == 0) return;
                    var admins = await tx.QueryAsync("""
                        SELECT DISTINCT a.user_id FROM iam.system_role_assignments a JOIN iam.users u ON u.id = a.user_id
                        WHERE a.role_code = 'system_admin' AND u.status = 'active' AND (a.valid_to IS NULL OR a.valid_to >= (now() AT TIME ZONE 'Asia/Riyadh')::date)
                        """, null, x => x.GetGuid(0), ct);
                    var what = metric == "requests" ? "الطلبات" : "وحدات الاستهلاك";
                    foreach (var a in admins)
                        await notifier.NotifyAsync(tx, new NotificationRequest(a, "ai.usage_threshold",
                            threshold >= 100 ? $"بلغ الذكاء الاصطناعي الحد الشهري لـ{what} ({value} من {limit}) وتوقفت الطلبات الجديدة" : $"بلغ استهلاك الذكاء الاصطناعي {pct}٪ من الحد الشهري لـ{what} ({value} من {limit})",
                            threshold >= 100 ? $"AI reached the monthly {metric} limit ({value}/{limit}); new requests are blocked" : $"AI usage reached {pct}% of the monthly {metric} limit ({value}/{limit})",
                            "/ai-admin", $"ai-usage:{u.Period}:{metric}:{threshold}:{a}", threshold >= 100 ? "critical" : "warning"), ct);
                }, ct);
            }
        }
    }

    // ---------------- سجل التشغيل ----------------

    public sealed record LogRow(string Function, string Status)
    {
        public string? SubjectType { get; init; }
        public Guid? SubjectId { get; init; }
        public Guid? BodyId { get; init; }
        public string? ErrorCode { get; init; }
        public int? DurationMs { get; init; }
        public int? InputTokens { get; init; }
        public int? OutputTokens { get; init; }
        public bool Estimated { get; init; }
        public string? Provider { get; init; }
        public string? Model { get; init; }
        public string? PromptVersion { get; init; }
        public string? InputSha256 { get; init; }
        public int? SourceCount { get; init; }
        public int Excluded { get; init; }
        public int Redactions { get; init; }
        public string? MaxClassification { get; init; }
        public Guid? OutputId { get; init; }
        public Guid? JobId { get; init; }
        public bool Counted { get; init; }
        public Guid? UserId { get; init; }
    }

    public async Task LogAsync(LogRow r, CancellationToken ct) =>
        await db.ExecuteAsync("""
            INSERT INTO ai.requests (id, function, user_id, body_id, subject_type, subject_id, status, error_code, duration_ms, input_tokens, output_tokens, usage_estimated,
                                     provider, model, prompt_version, input_sha256, source_count, excluded_sources, redactions, max_classification, output_id, job_id, counted)
            VALUES (@id, @fn, @user, @body, @st, @sid, @status, @err, @dur, @in, @out, @est, @provider, @model, @pv, @hash, @n, @excluded, @redactions, @cls, @output, @job, @counted)
            """, new
        {
            id = Guid.CreateVersion7(), fn = r.Function, user = r.UserId ?? (me.IsAuthenticated ? me.UserId : (Guid?)null), body = r.BodyId, st = r.SubjectType, sid = r.SubjectId,
            status = r.Status, err = r.ErrorCode, dur = r.DurationMs, @in = r.InputTokens, @out = r.OutputTokens, est = r.Estimated, provider = r.Provider, model = r.Model,
            pv = r.PromptVersion, hash = r.InputSha256, n = r.SourceCount, excluded = r.Excluded, redactions = r.Redactions, cls = r.MaxClassification, output = r.OutputId,
            job = r.JobId, counted = r.Counted && r.Status is "succeeded" or "held" or "invalid_output" or "failed" or "timeout" or "provider_unavailable",
        }, ct);
}
