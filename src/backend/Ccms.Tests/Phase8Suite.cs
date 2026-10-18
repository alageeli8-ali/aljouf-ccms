using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.Data.PgWire;

namespace Ccms.Tests;

/// <summary>خط أساس قبل تهيئة أي تكامل (التطبيق نفسه للمراحل السابقة): الموصلات تُعرض بمتطلباتها الناقصة، والفحص يُرجع «غير مهيأ» ولا يُدّعى عمل أي تكامل.</summary>
public sealed class Phase8BaselineSuite(TestContext ctx)
{
    [Test("P8-00", "التكاملات دون بيانات اعتماد: واجهة التكامل موجودة، والمتطلبات الناقصة موثقة، والفحص «غير مهيأ»، ولا شيء «يعمل فعليًا»", 0)]
    public async Task Baseline()
    {
        var admin = ctx.AdminClient!;
        var list = (await admin.GetJsonAsync("/api/v1/admin/integrations")).AsArray().OfType<JsonObject>().ToList();
        var codes = list.Select(x => x.S("code")).ToHashSet();
        foreach (var c in new[] { "directory", "sso", "email", "correspondence", "document_repository", "esign", "ai", "video_meetings" })
            Check.That(codes.Contains(c), "موصل مستقل: " + c);
        var dir = list.Single(x => x.S("code") == "directory");
        Check.Equal("DIRECTORY_SYNC_ENABLED", dir.S("featureKey"), "كل تكامل بمفتاحه");
        var missing = dir["missing"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Check.That(missing.Contains("Integrations:Graph:TenantId") && missing.Contains("GRAPH_CLIENT_SECRET"), "المتطلبات الناقصة موثقة بالاسم");
        foreach (var code in new[] { "directory", "video_meetings", "document_repository", "correspondence", "email" })
        {
            var t = (await Check.Status(await admin.PostAsync($"/api/v1/admin/integrations/{code}/test"), 200, "فحص " + code))!;
            Check.Equal("not_configured", t.S("status"), "دون بيانات اعتماد: غير مهيأ — " + code);
        }
        list = (await admin.GetJsonAsync("/api/v1/admin/integrations")).AsArray().OfType<JsonObject>().ToList();
        Check.That(list.Where(x => x.S("code") is "directory" or "video_meetings" or "document_repository" or "correspondence" or "email")
            .All(x => !x["verified"]!.GetValue<bool>()), "لا يُعد أي تكامل عاملًا دون فحص ناجح");
    }
}

/// <summary>وحدة التقارير واللوحات والبحث والأرشفة والتكاملات (ADR-022). بيانات حتمية في فترة مغلقة (مارس 2025) لاختبار دقة كل مؤشر بقيمته المتوقعة،
/// وخوادم وهمية للتكاملات (FakeIntegrationServer، FakeSmtpServer) لاختبار المصادقة والصلاحيات والمهلة وإعادة المحاولة وقاطع الدائرة والتعطيل.</summary>
public sealed class Phase8Suite(TestContext ctx, FakeIntegrationServer fake, FakeSmtpServer smtp) : IAsyncDisposable
{
    private const string Pw = "Test@Password2026";
    private const string Period = "from=2025-03-01&to=2025-03-31";
    private string Url => ctx.App!.BaseUrl;
    private readonly Dictionary<string, Guid> _u = [];
    private readonly Dictionary<string, ApiClient> _clients = [];
    private readonly PgWireDataSource _ownerDs = new(ctx.OwnerConnectionString);
    private Db Owner => new(_ownerDs);
    private Guid _bodyA, _bodyB, _unit, _entity, _m1, _m2, _m3, _mB, _minutes1, _r1, _r2, _docSecret, _minutesPdf;
    private string _r1Number = "";

    public async ValueTask DisposeAsync() => await _ownerDs.DisposeAsync();

    private ApiClient Admin => ctx.AdminClient!;

    private async Task<ApiClient> As(string user)
    {
        if (_clients.TryGetValue(user, out var c)) return c;
        c = new ApiClient(Url);
        await Check.Status(await c.LoginAsync($"p8-{user}@test.jsdo.gov.sa", Pw), 200, "دخول " + user);
        return _clients[user] = c;
    }

    private async Task Flags(params (string Key, string Value)[] flags)
    {
        var current = await Admin.GetJsonAsync("/api/v1/features");
        foreach (var (k, v) in flags)
            if (current[k]!["value"]!.GetValue<string>() != v)
                await Check.Status(await Admin.PutAsync($"/api/v1/admin/features/{k}", new { value = v, reason = $"اختبار وحدة التقارير والتكاملات: {k}", confirmImpact = true }), 200, k + "=" + v);
    }

    private static IEnumerable<JsonObject> Arr(JsonNode? n) => (n as JsonArray ?? []).OfType<JsonObject>();
    private static double? D(JsonNode? n, string k) => n?[k] is JsonValue v ? v.GetValue<double>() : null;
    private static long L(JsonNode? n, string k) => n![k]!.GetValue<long>();

    private static MultipartFormDataContent File(string name, string text, string ownerType, Guid ownerId, string purpose = "attachment", string? note = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.4 " + text));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", name);
        content.Add(new StringContent(ownerType), "ownerType");
        content.Add(new StringContent(ownerId.ToString()), "ownerId");
        content.Add(new StringContent(purpose), "purpose");
        if (note is not null) content.Add(new StringContent(note), "note");
        return content;
    }

    private async Task<long> Calls(string connector, string status) =>
        await Owner.ScalarAsync<long>("SELECT count(*) FROM integ.connector_calls WHERE connector = @connector AND status = @status", new { connector, status });

    // ============================ التهيئة ============================

    [Test("P8-SETUP", "تهيئة: لجنة ومجلس، واجتماع منعقد بمحضر معتمد وقرارين، ومهام بإنجاز في الموعد ومتأخر ومفتوح، مؤرخة في مارس 2025", 0)]
    public async Task Setup()
    {
        async Task User(string k, string[] roles) =>
            _u[k] = (await Check.Status(await Admin.PostAsync("/api/v1/users", new { email = $"p8-{k}@test.jsdo.gov.sa", displayNameAr = "م8 " + k, systemRoles = roles, initialPassword = Pw, userType = "internal" }), 201, "مستخدم " + k)).G("id");
        foreach (var k in new[] { "chair", "sec", "m1", "m2", "m3", "m4", "bchair", "bsec", "outsider" }) await User(k, []);
        await User("gov", ["governance_officer"]);
        await User("aud", ["auditor"]);
        await User("exec", ["executive"]);
        _unit = (await Check.Status(await Admin.PostAsync("/api/v1/org-units", new { code = "P8-PMO", nameAr = "إدارة المشاريع (م8)" }), 201, "إدارة")).G("id");
        _entity = Guid.CreateVersion7();
        await Owner.ExecuteAsync("INSERT INTO org.external_entities (id, name_ar, liaison_user_id) VALUES (@id, 'أمانة الجوف (م8)', @l)", new { id = _entity, l = _u["sec"] });

        var gov = await As("gov");
        object Policy() => new
        {
            effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)), quorumType = "majority", chairRequired = false, countRemote = true, allowOverride = false,
            votingThreshold = "majority_of_votes_cast", abstentions = "excluded", tieBreaker = "chair_casting_vote", secretAllowed = false, signoffRequired = false,
            minutesReviewDays = 3, extensionApprover = "chair", allowProxy = false, maxProxiesPerMember = 1, allowRemoteAttendance = true, allowCirculation = false,
            reason = "سياسة اختبار التقارير", minutesChairReview = false, minutesMemberReview = false, minutesSignatures = "none", minutesFinalApproval = "chair",
        };
        _bodyA = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code = "P8-RPT", bodyType = "permanent_committee", nameAr = "لجنة التقارير والمؤشرات", nameEn = "P8 Reports" }), 201, "لجنة")).G("id");
        foreach (var (u, role) in new[] { ("chair", "chair"), ("sec", "secretary"), ("m1", "member"), ("m2", "member"), ("m3", "member"), ("m4", "member") })
            await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_bodyA}/members", new { userId = _u[u], role }), 201, "عضوية " + u);
        await Check.Status(await gov.PutAsync($"/api/v1/bodies/{_bodyA}/policy", Policy()), 200, "سياسة");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_bodyA}/activate"), 200, "تفعيل");
        _bodyB = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code = "P8-EXC", bodyType = "executive_council", nameAr = "المجلس التنفيذي (م8)", nameEn = "P8 Council" }), 201, "مجلس")).G("id");
        foreach (var (u, role) in new[] { ("bchair", "chair"), ("bsec", "secretary") })
            await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_bodyB}/members", new { userId = _u[u], role }), 201, "عضوية " + u);
        await Check.Status(await gov.PutAsync($"/api/v1/bodies/{_bodyB}/policy", Policy()), 200, "سياسة ب");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_bodyB}/activate"), 200, "تفعيل ب");

        // الاجتماع المنعقد: 4 حضور من 6 أعضاء (غائب ومعتذر)، ومحضر معتمد بقرارين
        var sec = await As("sec");
        _m1 = (await Check.Status(await sec.PostAsync("/api/v1/meetings", new { bodyId = _bodyA, titleAr = "اجتماع المراجعة الربعية", meetingType = "regular", startsAt = DateTime.UtcNow.AddMinutes(30), location = "القاعة" }), 201, "اجتماع 1")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/schedule"), 200, "جدولة");
        var item = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/agenda-items", new { titleAr = "مؤشرات الأداء" }), 201, "بند")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/invitations/send"), 200, "دعوات");
        await Check.Status(await sec.PutAsync($"/api/v1/meetings/{_m1}/attendance", new
        {
            records = new object[]
            {
                new { userId = _u["chair"], status = "present", mode = "in_person" }, new { userId = _u["sec"], status = "present", mode = "in_person" },
                new { userId = _u["m1"], status = "present", mode = "in_person" }, new { userId = _u["m2"], status = "present", mode = "remote" },
                new { userId = _u["m3"], status = "absent" }, new { userId = _u["m4"], status = "excused", note = "مهمة رسمية" },
            },
        }), 200, "حضور");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/open", new { }), 200, "افتتاح");
        _minutes1 = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/minutes"), 201, "محضر")).G("id");
        await Check.Status(await sec.PutAsync($"/api/v1/minutes/{_minutes1}/draft", new
        {
            sections = new[] { new { key = "opening", title = "الافتتاح", body = "افتتح الرئيس الاجتماع." } },
            items = new[] { new { agendaItemId = item, discussion = "ناقشت اللجنة مؤشرات الأداء.", recommendation = "" } },
        }), 200, "مسودة");
        foreach (var t in new[] { "اعتماد لوحة مؤشرات الأداء الربعية", "اعتماد دليل احتساب المؤشرات" })
            await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes1}/resolutions", new { resolutionType = "decision", titleAr = t, textAr = t + " وتعميمه.", agendaItemId = item, category = "technical" }), 201, "قرار");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m1}/close"), 200, "إغلاق");
        await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutes1}/submit", new { }), 200, "رفع");
        var hash = (await sec.GetJsonAsync($"/api/v1/minutes/{_minutes1}"))["currentVersion"].S("hash");
        await Check.Status(await (await As("chair")).PostAsync($"/api/v1/minutes/{_minutes1}/approve", new { contentHash = hash }), 200, "اعتماد");
        var res = await Owner.QueryAsync("SELECT id, number FROM resolutions.resolutions WHERE minutes_id = @id ORDER BY position, created_at", new { id = _minutes1 }, r => (r.GetGuid(0), r.GetString(1)));
        Check.Equal(2, res.Count, "قراران صادران");
        (_r1, _r1Number) = res[0];
        _r2 = res[1].Item1;

        _m2 = (await Check.Status(await sec.PostAsync("/api/v1/meetings", new { bodyId = _bodyA, titleAr = "اجتماع المتابعة المخطط", meetingType = "regular", startsAt = DateTime.UtcNow.AddDays(2), location = "القاعة" }), 201, "اجتماع 2")).G("id");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m2}/schedule"), 200, "جدولة 2");
        _m3 = (await Check.Status(await sec.PostAsync("/api/v1/meetings", new { bodyId = _bodyA, titleAr = "مسودة اجتماع لم يُجدول", meetingType = "regular", startsAt = DateTime.UtcNow.AddDays(3), location = "القاعة" }), 201, "اجتماع 3")).G("id");
        var bsec = await As("bsec");
        _mB = (await Check.Status(await bsec.PostAsync("/api/v1/meetings", new { bodyId = _bodyB, titleAr = "اجتماع المجلس السري", meetingType = "regular", startsAt = DateTime.UtcNow.AddDays(4), location = "القاعة" }), 201, "اجتماع ب")).G("id");
        _docSecret = (await Check.Status(await bsec.PostContentAsync("/api/v1/documents", File("p8-council-secret-plan.pdf", "خطة سرية للمجلس", "meeting", _mB)), 201, "مرفق المجلس")).G("id");

        // التأريخ الحتمي في مارس 2025 (بحساب المالك) لاختبار الأرقام بدقة
        var args = new { m1 = _m1, m2 = _m2, m3 = _m3, mi = _minutes1, r1 = _r1, r2 = _r2, by = _u["chair"] };
        foreach (var sql in new[]
        {
            "UPDATE meetings.meetings SET starts_at = '2025-03-10 07:00Z', actual_started_at = '2025-03-10 07:00Z', actual_ended_at = '2025-03-10 10:00Z' WHERE id = @m1",
            "UPDATE meetings.meetings SET starts_at = '2025-03-20 07:00Z' WHERE id = @m2",
            "UPDATE meetings.meetings SET starts_at = '2025-03-25 07:00Z' WHERE id = @m3",
            "UPDATE minutes.minutes SET approved_at = '2025-03-13 10:00Z' WHERE id = @mi",
            "UPDATE resolutions.resolutions SET issued_at = '2025-03-10 11:00Z' WHERE id IN (@r1, @r2)",
            "UPDATE resolutions.resolutions SET status = 'executed' WHERE id = @r2",
            "INSERT INTO resolutions.resolution_status_history (id, resolution_id, from_status, to_status, changed_by, changed_at) VALUES (gen_random_uuid(), @r2, 'issued', 'executed', @by, '2025-03-28 08:00Z')",
        }) await Owner.ExecuteAsync(sql, args);
        async Task TaskRow(string title, string status, string assigned, string due, string? completed, Guid? unit, Guid? entity, Guid? resolution) =>
            await Owner.ExecuteAsync("""
                INSERT INTO tasks.tasks (id, body_id, resolution_id, title_ar, assignee_type, assignee_org_unit_id, assignee_external_entity_id, accountable_user_id,
                                         status, due_date, assigned_at, completed_at, created_at, verification_required, completion_method)
                VALUES (gen_random_uuid(), @body, @resolution, @title, @type, @unit, @entity, @acc, @status, @due::date, @assigned::timestamptz, @completed::timestamptz,
                        @assigned::timestamptz, false, CASE WHEN @status = 'completed' THEN 'self_declared' END)
                """, new { body = _bodyA, resolution, title, type = entity is null ? "org_unit" : "external_entity", unit, entity, acc = _u["sec"], status, due, assigned, completed });
        await TaskRow("م8 مهمة أنجزت في الموعد", "completed", "2025-03-01 08:00Z", "2025-03-08", "2025-03-05 08:00Z", _unit, null, null);
        await TaskRow("م8 مهمة أنجزت متأخرة", "completed", "2025-03-01 08:00Z", "2025-03-06", "2025-03-11 08:00Z", _unit, null, null);
        await TaskRow("م8 مهمة مفتوحة متأخرة", "assigned", "2025-03-02 08:00Z", "2025-03-20", null, _unit, null, _r1);
        await TaskRow("م8 مهمة الجهة الخارجية", "assigned", "2025-03-03 08:00Z", DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd"), null, null, _entity, null);
    }

    // ============================ أ. لوحة الإدارة العليا ============================

    [Test("P8-01", "لوحة الإدارة العليا: كل مؤشر بقيمته المتوقعة من البيانات الفعلية، وتعريف موثق لكل مؤشر، والمرشحات (الفترة، الجهة، النوع، الإدارة، الجهة الخارجية)، والنطاق بالصلاحية", 1)]
    public async Task Dashboard()
    {
        var exec = await As("exec");
        var e = await exec.GetJsonAsync($"/api/v1/reports/executive?{Period}&bodyId={_bodyA}");
        var k = e["kpis"]!;
        Check.Equal(1L, L(k, "bodies_active"), "الجهات الفعالة");
        Check.Equal(2L, L(k, "meetings_planned"), "المخططة: المنعقد + المجدول (المسودة مستبعدة)");
        Check.Equal(1L, L(k, "meetings_held"), "المنعقدة");
        Check.Equal(4L, L(k, "attendance_present"), "الحاضرون");
        Check.Equal(6L, L(k, "attendance_expected"), "سجلات الأعضاء");
        Check.Equal(66.7, D(k, "attendance_rate"), "نسبة الحضور 4/6 (المعتذر غير حاضر)");
        Check.Equal(2L, L(k, "resolutions_issued"), "القرارات الصادرة");
        Check.Equal(1L, L(k, "resolutions_executed"), "المنفذة (من سجل الحالات)");
        Check.Equal(1L, L(k, "resolutions_overdue"), "المتأخرة (لها مهمة مفتوحة متأخرة)");
        Check.Equal(2L, L(k, "tasks_open"), "المهام المفتوحة");
        Check.Equal(2L, L(k, "tasks_completed"), "المكتملة في الفترة");
        Check.Equal(1L, L(k, "tasks_overdue"), "المتأخرة");
        Check.Equal(7.0, D(k, "task_avg_days"), "متوسط مدة التنفيذ (4 + 10) / 2");
        Check.Equal(50.0, D(k, "task_on_time_rate"), "الإنجاز في الموعد 1/2");
        Check.Equal(3.0, D(k, "minutes_avg_approval_days"), "متوسط زمن الاعتماد (3 أيام من نهاية الاجتماع)");
        Check.Equal(0L, L(k, "minutes_pending_review") + L(k, "minutes_pending_signatures") + L(k, "minutes_pending_approval"), "لا محاضر معلقة");

        var row = Arr(e["bodies"]).Single(b => b.G("bodyId") == _bodyA);
        Check.That(L(row, "meetingsHeld") == 1 && D(row, "attendanceRate") == 66.7 && L(row, "resolutionsIssued") == 2 && L(row, "resolutionsOverdue") == 1
            && L(row, "tasksCompleted") == 2 && L(row, "tasksOverdue") == 1 && D(row, "onTimeRate") == 50.0 && D(row, "avgApprovalDays") == 3.0, "أداء اللجنة");
        var unit = Arr(e["compliance"]).Single(c => c.G("assigneeId") == _unit);
        Check.That(L(unit, "assigned") == 3 && L(unit, "completed") == 2 && L(unit, "completedOnTime") == 1 && L(unit, "openOverdue") == 1
            && D(unit, "complianceIndex") == 33.3 && D(unit, "avgDelayDays") == 5.0, "التزام الإدارة: 1 / (2 + 1)");
        var ent = Arr(e["compliance"]).Single(c => c.G("assigneeId") == _entity);
        Check.That(ent.S("assigneeType") == "external_entity" && L(ent, "assigned") == 1 && ent["complianceIndex"] is null, "الجهة الخارجية: لا مكتمل ولا متأخر → لا مؤشر (لا قسمة على صفر)");

        var defs = Arr(e["definitions"]).ToList();
        Check.Equal(17, defs.Count, "تعريف لكل مؤشر");
        Check.That(defs.All(d => d.S("formulaAr").Length > 20 && d.S("formulaEn").Length > 20 && d.S("basis") is "period" or "snapshot" or "mixed"), "طريقة الاحتساب بالعربية والإنجليزية وأساسها");

        // المرشحات
        var unitK = (await exec.GetJsonAsync($"/api/v1/reports/executive?{Period}&bodyId={_bodyA}&orgUnitId={_unit}"))["kpis"]!;
        Check.That(L(unitK, "tasks_completed") == 2 && L(unitK, "tasks_open") == 1 && L(unitK, "resolutions_issued") == 1, "تصفية الإدارة: مهامها وقراراتها فقط");
        var entK = (await exec.GetJsonAsync($"/api/v1/reports/executive?{Period}&bodyId={_bodyA}&entityId={_entity}"))["kpis"]!;
        Check.That(L(entK, "tasks_open") == 1 && L(entK, "tasks_completed") == 0, "تصفية الجهة الخارجية");
        var council = await exec.GetJsonAsync($"/api/v1/reports/executive?{Period}&bodyKind=council");
        Check.That(Arr(council["bodies"]).All(b => b.S("bodyType").EndsWith("council")) && Arr(council["bodies"]).Any(b => b.G("bodyId") == _bodyB), "تصفية المجالس");
        var committee = await exec.GetJsonAsync($"/api/v1/reports/executive?{Period}&bodyKind=committee&bodyId={_bodyB}");
        Check.Equal(0L, L(committee, "scopeBodies"), "المجلس خارج تصفية اللجان");
        var y2024 = (await exec.GetJsonAsync($"/api/v1/reports/executive?from=2024-01-01&to=2024-12-31&bodyId={_bodyA}"))["kpis"]!;
        Check.That(L(y2024, "meetings_held") == 0 && L(y2024, "tasks_completed") == 0 && L(y2024, "tasks_open") == 2, "مؤشرات الفترة تتغير، واللقطات الآنية ثابتة");
        await Check.Problem(await exec.GetAsync("/api/v1/reports/executive?from=2025-04-01&to=2025-03-01"), 400, "validation", "بداية بعد النهاية");
        await Check.Problem(await exec.GetAsync("/api/v1/reports/executive?bodyKind=other"), 400, "validation", "نوع غير معروف");

        // النطاق بالصلاحية
        var chair = await As("chair");
        var other = await chair.GetJsonAsync($"/api/v1/reports/executive?{Period}&bodyId={_bodyB}");
        Check.That(L(other, "scopeBodies") == 0 && L(other["kpis"], "meetings_planned") == 0, "رئيس اللجنة لا يرى أرقام المجلس");
        Check.Equal(1L, L(await chair.GetJsonAsync($"/api/v1/reports/executive?{Period}"), "scopeBodies"), "رئيس اللجنة يرى لجنته فقط");
        await Check.Problem(await (await As("outsider")).GetAsync("/api/v1/reports/executive"), 403, "forbidden-action", "من لا يملك صلاحية التقارير");
    }

    // ============================ ب. التقارير والتصدير ============================

    [Test("P8-02", "التقارير الثمانية: دقة الصفوف، والتصدير إلى PDF وExcel بحسب الصلاحية، وتسجيل كل تصدير في التدقيق، وتصدير التدقيق بصلاحية مستقلة", 2)]
    public async Task Reports()
    {
        var exec = await As("exec");
        var cat = Arr((await exec.GetJsonAsync("/api/v1/reports/catalog"))["items"]).Select(i => i["report"].S("code")).ToList();
        foreach (var c in new[] { "meetings", "minutes", "resolutions", "overdue_tasks", "body_performance", "signatures", "compliance" })
            Check.That(cat.Contains(c), "تقرير متاح: " + c);
        Check.That(!cat.Contains("audit"), "تقرير التدقيق لا يظهر لغير المدقق");

        var q = $"{Period}&bodyId={_bodyA}";
        var meetings = await exec.GetJsonAsync($"/api/v1/reports/run/meetings?{q}");
        Check.Equal(3L, L(meetings, "rowCount"), "تقرير الاجتماعات: كل اجتماعات الفترة بحالاتها");
        var held = Arr(meetings["rows"]).Single(r => r.S("title") == "اجتماع المراجعة الربعية");
        Check.That(L(held, "present") == 4 && L(held, "expected") == 6 && D(held, "attendance") == 66.7 && held.S("minutes") == "approved", "صف الاجتماع المنعقد");
        var minutes = await exec.GetJsonAsync($"/api/v1/reports/run/minutes?{q}");
        var mrow = Arr(minutes["rows"]).Single();
        Check.That(mrow.S("status") == "approved" && D(mrow, "approvalDays") == 3.0 && mrow.S("approvedAt") == "2025-03-13", "تقرير المحاضر وحالة الاعتماد");
        var res = Arr((await exec.GetJsonAsync($"/api/v1/reports/run/resolutions?{q}"))["rows"]).ToList();
        Check.Equal(2, res.Count, "تقرير القرارات");
        Check.That(res.Single(r => r.S("number") == _r1Number) is var r1 && L(r1, "tasksOverdue") == 1 && r1.S("nextDue") == "2025-03-20", "القرار المتأخر وأقرب استحقاق");
        var overdue = Arr((await exec.GetJsonAsync($"/api/v1/reports/run/overdue_tasks?bodyId={_bodyA}"))["rows"]).Single();
        var days = (long)(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).DayNumber - new DateOnly(2025, 3, 20).DayNumber);
        Check.That(overdue.S("title") == "م8 مهمة مفتوحة متأخرة" && L(overdue, "daysOverdue") == days && overdue.S("resolution") == _r1Number, "تقرير المهام المتأخرة وأيام التأخر");
        var perf = Arr((await exec.GetJsonAsync($"/api/v1/reports/run/body_performance?{q}"))["rows"]).Single();
        Check.That(L(perf, "held") == 1 && D(perf, "onTime") == 50.0, "تقرير أداء الجهات يطابق اللوحة");
        var comp = Arr((await exec.GetJsonAsync($"/api/v1/reports/run/compliance?{q}"))["rows"]).Single(r => r.S("type") == "org_unit");
        Check.That(D(comp, "index") == 33.3 && D(comp, "avgDelay") == 5.0, "تقرير الالتزام بمواعيد التنفيذ");
        Check.Equal(0L, L(await exec.GetJsonAsync($"/api/v1/reports/run/signatures?{q}"), "rowCount"), "تقرير التوقيعات (لا طلبات في سياسة بلا توقيع)");

        // التصدير
        var before = await Owner.ScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action = 'report.export'");
        var pdf = await (await exec.GetAsync($"/api/v1/reports/run/meetings?{q}&format=pdf")).Content.ReadAsByteArrayAsync();
        Check.That(Encoding.ASCII.GetString(pdf, 0, 5) == "%PDF-" && pdf.Length > 5000, "PDF صالح");
        var xlsxResp = await exec.GetAsync($"/api/v1/reports/run/compliance?{q}&format=xlsx");
        Check.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsxResp.Content.Headers.ContentType?.MediaType, "نوع Excel");
        using (var zip = new ZipArchive(new MemoryStream(await xlsxResp.Content.ReadAsByteArrayAsync())))
        {
            var names = zip.Entries.Select(x => x.FullName).ToHashSet();
            Check.That(names.Contains("xl/workbook.xml") && names.Contains("xl/worksheets/sheet1.xml") && names.Contains("xl/worksheets/sheet2.xml") && names.Contains("[Content_Types].xml"), "حزمة OOXML كاملة بورقتين");
            using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var xml = await sheet.ReadToEndAsync();
            Check.That(xml.Contains("إدارة المشاريع (م8)") && xml.Contains("<v>33.3</v>") && xml.Contains("rightToLeft=\"1\""), "القيم رقمية والورقة من اليمين لليسار");
        }
        Check.Equal(before + 2, await Owner.ScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action = 'report.export'"), "كل تصدير مسجل في التدقيق");
        await Check.Problem(await exec.GetAsync($"/api/v1/reports/run/meetings?format=docx"), 400, "validation", "صيغة غير مدعومة");
        await Check.Problem(await exec.GetAsync("/api/v1/reports/run/unknown"), 404, "not-found", "تقرير غير موجود");
        await Check.Problem(await (await As("outsider")).GetAsync($"/api/v1/reports/run/meetings?format=pdf"), 403, "forbidden-action", "التصدير بالصلاحية");

        // تقرير التدقيق: audit.read للعرض وaudit.export للتصدير
        await Check.Problem(await exec.GetAsync("/api/v1/reports/run/audit"), 403, "forbidden-action", "التنفيذي لا يقرأ التدقيق");
        var aud = await As("aud");
        var audit = await aud.GetJsonAsync($"/api/v1/reports/run/audit?from={DateTime.UtcNow:yyyy-MM-dd}&to={DateTime.UtcNow.AddDays(1):yyyy-MM-dd}");
        Check.That(L(audit, "rowCount") > 0 && Arr(audit["rows"]).Any(r => r.S("action") == "report.export"), "تقرير التغييرات يتضمن التصدير نفسه");
        Check.That(Arr(audit["rows"]).All(r => !r.ContainsKey("before") && !r.ContainsKey("after")), "أسماء الحقول المتغيرة دون قيمها");
        var audXlsx = await aud.GetAsync($"/api/v1/reports/run/audit?format=xlsx");
        Check.Equal(200, (int)audXlsx.StatusCode, "المدقق يصدّر التدقيق");
        var audCat = Arr((await aud.GetJsonAsync("/api/v1/reports/catalog"))["items"]).Single(i => i["report"].S("code") == "audit");
        Check.That(audCat["report"].S("permission") == "audit.read" && audCat["report"].S("exportPermission") == "audit.export" && audCat["canExport"]!.GetValue<bool>(),
            "العرض بـ audit.read والتصدير بصلاحية مستقلة audit.export");
    }

    private async Task<ApiClient> NewSession(string user)
    {
        var c = new ApiClient(Url);
        await Check.Status(await c.LoginAsync($"p8-{user}@test.jsdo.gov.sa", Pw), 200, "دخول " + user);
        return c;
    }

    // ============================ ج. البحث الموحد ============================

    [Test("P8-03", "البحث الموحد: الجهات والأعضاء والاجتماعات والمحاضر والقرارات والمهام والمرفقات، برقم القرار والعنوان والجهة والفترة، والنتائج والمرفقات مقيدة بالصلاحية", 3)]
    public async Task Search()
    {
        var m1 = await As("m1");
        var bsec = await As("bsec");
        async Task<List<JsonObject>> S(ApiClient c, string query) => Arr((await c.GetJsonAsync("/api/v1/search?" + query))["items"]).ToList();

        var byNumber = await S(m1, "q=" + Uri.EscapeDataString(_r1Number));
        Check.That(byNumber.Any(h => h.S("type") == "resolution" && h.G("id") == _r1), "البحث برقم القرار");
        Check.That((await S(bsec, "q=" + Uri.EscapeDataString(_r1Number))).All(h => h.G("bodyId") != _bodyA), "عضو جهة أخرى لا يرى القرار");
        Check.That((await S(m1, "q=" + Uri.EscapeDataString("لجنة التقارير"))).Any(h => h.S("type") == "body" && h.G("id") == _bodyA), "البحث عن الجهات");
        Check.That((await S(m1, "q=" + Uri.EscapeDataString("م8 m2"))).Any(h => h.S("type") == "member" && h.G("bodyId") == _bodyA), "البحث عن الأعضاء");
        Check.That((await S(m1, "q=" + Uri.EscapeDataString("المراجعة الربعية") + "&types=meeting,minutes")).Select(h => h.S("type")).ToHashSet().SetEquals(["meeting", "minutes"]), "الاجتماع والمحضر بالعنوان");
        Check.That((await S(m1, "q=" + Uri.EscapeDataString("مؤشرات") + $"&bodyId={_bodyA}&types=resolution&from=2025-03-01&to=2025-03-31")).Count == 2, "بالجهة والفترة");
        Check.That((await S(m1, "q=" + Uri.EscapeDataString("مؤشرات") + "&types=resolution&from=2024-01-01&to=2024-12-31")).Count == 0, "خارج الفترة لا نتائج");
        Check.That((await S(await As("sec"), "q=" + Uri.EscapeDataString("م8 مهمة") + "&types=task")).Count >= 3, "البحث في المهام");

        // المرفقات: مرفق المجلس لا يظهر لعضو اللجنة، ويظهر لأمين سر المجلس مع رابط سجله المالك
        Check.That((await S(m1, "q=council-secret-plan")).Count == 0, "مرفق جهة أخرى محجوب في نتائج البحث");
        var hit = (await S(bsec, "q=council-secret-plan")).Single();
        Check.That(hit.S("type") == "document" && hit.G("id") == _docSecret && hit.S("link") == $"/meetings/{_mB}", "مرفق مسموح برابط سجله المالك");
        await Check.Problem(await m1.GetAsync($"/api/v1/documents/{_docSecret}/content"), 404, "not-found", "ولا يُنزّل بالمعرّف مباشرة");
        Check.That((await S(m1, "q=minutes-")).Any(h => h.S("type") == "document" && h.G("bodyId") == _bodyA), "PDF المحضر المعتمد قابل للبحث لعضو اللجنة");
        Check.That((await S(await As("outsider"), "q=" + Uri.EscapeDataString("لجنة التقارير"))).Count == 0, "مستخدم دون عضوية لا يرى شيئًا");
        await Check.Problem(await m1.GetAsync("/api/v1/search?q=x&from=2025-05-01&to=2025-01-01"), 400, "validation", "فترة غير صالحة");
    }

    // ============================ ج. الإصدارات والاحتفاظ والإتلاف ============================

    [Test("P8-04", "إدارة الإصدارات: الإصدار الجديد يحفظ السابق، والملف الموقّع/المعتمد لا يُستبدل إلا بإصدار مسبب من مسؤول السجلات، والقاعدة تمنع تعديل المحتوى أو حذفه", 4)]
    public async Task Versions()
    {
        var sec = await As("sec");
        var v1 = (await Check.Status(await sec.PostContentAsync("/api/v1/documents", File("p8-agenda-memo.pdf", "مذكرة الإصدار الأول", "meeting", _m2, "agenda_memo")), 201, "رفع")).G("id");
        var d1 = (await sec.GetJsonAsync($"/api/v1/documents/{v1}"))["document"]!;
        Check.That(d1["versionNo"]!.GetValue<int>() == 1 && !d1["locked"]!.GetValue<bool>() && d1.S("recordsClass") == "working_papers"
            && d1.S("retentionUntil") == DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).AddYears(10).ToString("yyyy-MM-dd"), "التصنيف السجلي ومدة الاحتفاظ من الغرض");
        var original = await sec.GetBytesAsync($"/api/v1/documents/{v1}/content");
        var v2Resp = (await Check.Status(await sec.PostContentAsync($"/api/v1/documents/{v1}/versions", File("p8-agenda-memo.pdf", "مذكرة الإصدار الثاني المعدل", "meeting", _m2, note: "تحديث الأرقام")), 201, "إصدار جديد"))!;
        var v2 = v2Resp.G("id");
        Check.Equal(2, v2Resp["versionNo"]!.GetValue<int>(), "رقم الإصدار");
        var list = (await sec.GetJsonAsync($"/api/v1/documents?ownerType=meeting&ownerId={_m2}")).AsArray();
        Check.That(list.Count == 1 && list[0].G("id") == v2, "قائمة المرفقات تعرض الإصدار الحالي فقط");
        var versions = (await sec.GetJsonAsync($"/api/v1/documents/{v2}/versions")).AsArray();
        Check.That(versions.Count == 2 && !versions[1]!["isCurrent"]!.GetValue<bool>() && versions[0]!["isCurrent"]!.GetValue<bool>(), "سجل الإصدارات");
        Check.That((await sec.GetBytesAsync($"/api/v1/documents/{v1}/content")).SequenceEqual(original), "الإصدار السابق محفوظ كما هو");
        await Check.Problem(await sec.PostContentAsync($"/api/v1/documents/{v1}/versions", File("x.pdf", "أي", "meeting", _m2)), 409, "not-current-version", "لا إصدار على إصدار مُستبدل");
        await Check.Problem(await sec.PostContentAsync($"/api/v1/documents/{v2}/versions", File("p8-agenda-memo.pdf", "مذكرة الإصدار الثاني المعدل", "meeting", _m2)), 409, "same-content", "محتوى مطابق");

        // PDF المحضر المعتمد مقفل
        _minutesPdf = (await sec.GetJsonAsync($"/api/v1/documents?ownerType=minutes&ownerId={_minutes1}")).AsArray().Single(d => d.S("purpose") == "minutes_pdf").G("id");
        var locked = (await sec.GetJsonAsync($"/api/v1/documents/{_minutesPdf}"))["document"]!;
        Check.That(locked["locked"]!.GetValue<bool>() && locked.S("recordsClass") == "governance_records" && locked["retentionUntil"] is null, "المحضر المعتمد: مقفل، حفظ دائم");
        await Check.Problem(await sec.PostContentAsync($"/api/v1/documents/{_minutesPdf}/versions", File("m.pdf", "محاولة استبدال", "minutes", _minutes1, note: "استبدال مباشر للمحضر")), 403, "forbidden-action", "أمين السر لا يستبدل المعتمد");
        var gov = await As("gov");
        await Check.Problem(await gov.PostContentAsync($"/api/v1/documents/{_minutesPdf}/versions", File("m.pdf", "نسخة مصححة", "minutes", _minutes1)), 400, "validation", "دون سبب");
        var corrected = (await Check.Status(await gov.PostContentAsync($"/api/v1/documents/{_minutesPdf}/versions", File("m.pdf", "نسخة مصححة", "minutes", _minutes1, note: "تصحيح خطأ طباعي في الترويسة بقرار الرئيس")), 201, "إصدار مسبب")).G("id");
        Check.That((await gov.GetJsonAsync($"/api/v1/documents/{corrected}"))["document"]!["locked"]!.GetValue<bool>(), "الإصدار الجديد مقفل أيضًا");
        Check.Equal(1L, await Owner.ScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE action = 'document.version_registered' AND entity_id = @id", new { id = corrected.ToString() }), "مسجل في التدقيق");
        _minutesPdf = corrected;

        // حماية قاعدة البيانات بحساب التطبيق نفسه
        await using var app = new PgWireDataSource(ctx.AppConnectionString);
        var appDb = new Db(app);
        async Task<string> Fails(string sql)
        {
            try { await appDb.ExecuteAsync(sql, new { id = corrected }); return ""; }
            catch (Exception e) { return e.Message; }
        }
        Check.That((await Fails("UPDATE docs.documents SET sha256 = 'tampered' WHERE id = @id")).Contains("immutable"), "تعديل البصمة مرفوض");
        Check.That((await Fails("UPDATE docs.documents SET storage_key = 'x' WHERE id = @id")).Contains("immutable"), "استبدال الملف مرفوض");
        Check.That((await Fails("UPDATE docs.documents SET locked = false WHERE id = @id")).Contains("unlocked"), "فك القفل مرفوض");
        Check.That((await Fails("DELETE FROM docs.documents WHERE id = @id")).Length > 0, "حذف سجل المستند مرفوض");
    }

    [Test("P8-05", "الاحتفاظ والأرشفة والإتلاف: قواعد قابلة للتعديل، وأرشفة، وحجز قانوني، وإتلاف بطلب ومعتمد مختلف وشهادة، والحفظ الدائم لا يُتلف", 5)]
    public async Task Retention()
    {
        var gov = await As("gov");
        var sec = await As("sec");
        var rules = (await gov.GetJsonAsync("/api/v1/records/retention-rules")).AsArray();
        Check.That(rules.Count >= 5 && rules.Any(r => r.S("code") == "governance_records" && r.S("finalAction") == "permanent"), "قواعد الاحتفاظ الافتراضية");
        await Check.Problem(await sec.GetAsync("/api/v1/records/retention-rules"), 403, "forbidden-action", "القواعد لمسؤول السجلات");
        await Check.Problem(await gov.PutAsync("/api/v1/records/retention-rules/working_papers", new { nameAr = "أوراق العمل", nameEn = "Working papers", retentionYears = 0, archiveAfterYears = 1, finalAction = "destroy" }), 400, "validation", "صفر سنوات مع الإتلاف");
        await Check.Status(await gov.PutAsync("/api/v1/records/retention-rules/working_papers", new { nameAr = "أوراق العمل والمذكرات", nameEn = "Working papers and memos", retentionYears = 5, archiveAfterYears = 1, finalAction = "destroy", legalBasis = "اختبار" }), 204, "تعديل القاعدة");
        var memo = (await sec.GetJsonAsync($"/api/v1/documents?ownerType=meeting&ownerId={_m2}")).AsArray().Single().G("id");
        Check.Equal(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).AddYears(5).ToString("yyyy-MM-dd"), (await gov.GetJsonAsync($"/api/v1/documents/{memo}"))["document"].S("retentionUntil"), "إعادة الحساب للمستندات القائمة");

        // أرشفة: لا تمس المحتوى
        await Check.Status(await gov.PostAsync($"/api/v1/documents/{memo}/archive", new { reason = "انتهاء الحاجة التشغيلية" }), 204, "أرشفة");
        Check.Equal("archived", (await gov.GetJsonAsync($"/api/v1/documents/{memo}"))["document"].S("lifecycleStatus"), "مؤرشف");
        await Check.Status(await sec.GetAsync($"/api/v1/documents/{memo}/content"), 200, "المؤرشف قابل للتنزيل");

        // إتلاف قبل انقضاء المدة مرفوض؛ الحفظ الدائم مرفوض
        await Check.Problem(await gov.PostAsync("/api/v1/records/destruction-requests", new { documentIds = new[] { memo }, reason = "طلب إتلاف تجريبي قبل الأوان" }), 409, "not-eligible-for-destruction", "قبل انقضاء الاحتفاظ");
        await Owner.ExecuteAsync("UPDATE docs.documents SET retention_until = '2024-01-01' WHERE series_id = (SELECT series_id FROM docs.documents WHERE id = @memo) OR id = @pdf", new { memo, pdf = _minutesPdf });
        await Check.Problem(await gov.PostAsync("/api/v1/records/destruction-requests", new { documentIds = new[] { _minutesPdf }, reason = "محاولة إتلاف محضر معتمد" }), 409, "not-eligible-for-destruction", "الحفظ الدائم لا يُتلف");
        var due = Arr((await gov.GetJsonAsync("/api/v1/records/register?due=true"))["items"]).Select(x => x.G("id")).ToList();
        Check.That(due.Contains(memo) && !due.Contains(_minutesPdf), "سجل المستحق للإتلاف");

        // الحجز القانوني يمنع الإتلاف
        var hold = (await Check.Status(await sec.PostContentAsync("/api/v1/documents", File("p8-hold.pdf", "مستند محجوز", "meeting", _m2)), 201, "مستند")).G("id");
        await Owner.ExecuteAsync("UPDATE docs.documents SET retention_until = '2024-01-01' WHERE id = @hold", new { hold });
        await Check.Problem(await gov.PostAsync($"/api/v1/documents/{hold}/legal-hold", new { hold = true }), 400, "validation", "الحجز بلا سبب");
        await Check.Status(await gov.PostAsync($"/api/v1/documents/{hold}/legal-hold", new { hold = true, reason = "دعوى قائمة رقم 12" }), 204, "حجز قانوني");
        await Check.Problem(await gov.PostAsync("/api/v1/records/destruction-requests", new { documentIds = new[] { hold }, reason = "محاولة إتلاف مستند محجوز" }), 409, "not-eligible-for-destruction", "المحجوز لا يُتلف");

        // طلب ← اعتماد من مختلف ← تنفيذ ← شهادة
        var reqId = (await Check.Status(await gov.PostAsync("/api/v1/records/destruction-requests", new { documentIds = new[] { memo }, reason = "انقضاء مدة الاحتفاظ وفق القاعدة المعتمدة" }), 201, "طلب إتلاف")).G("id");
        Check.Equal(2L, await Owner.ScalarAsync<long>("SELECT count(*) FROM docs.documents WHERE series_id = (SELECT series_id FROM docs.documents WHERE id = @memo) AND lifecycle_status = 'destruction_pending'", new { memo }), "الطلب يشمل كل الإصدارات");
        await Check.Problem(await gov.PostAsync($"/api/v1/records/destruction-requests/{reqId}/execute"), 409, "not-approved", "لا تنفيذ قبل الاعتماد");
        await Check.Problem(await gov.PostAsync($"/api/v1/records/destruction-requests/{reqId}/decide", new { approve = true }), 409, "four-eyes", "مقدم الطلب لا يعتمد طلبه");
        await Check.Problem(await sec.PostAsync($"/api/v1/records/destruction-requests/{reqId}/decide", new { approve = true }), 403, "forbidden-action", "غير المخول لا يعتمد");
        await Check.Status(await (await As("aud")).PostAsync($"/api/v1/records/destruction-requests/{reqId}/decide", new { approve = true, note = "مطابق للقاعدة" }), 204, "اعتماد المدقق");
        var cert = (await Check.Status(await gov.PostAsync($"/api/v1/records/destruction-requests/{reqId}/execute"), 200, "تنفيذ"))!;
        Check.That(cert["destroyed"]!.AsArray().Count == 2 && cert["destroyed"]!.AsArray().All(x => x.S("sha256").Length == 64), "الشهادة بالبصمات");
        await Check.Problem(await sec.GetAsync($"/api/v1/documents/{memo}/content"), 409, "document-destroyed", "المحتوى متلف");
        var meta = (await gov.GetJsonAsync($"/api/v1/documents/{memo}"))["document"]!;
        Check.That(meta.S("lifecycleStatus") == "destroyed" && meta.S("sha256").Length == 64 && meta["destroyedAt"] is not null, "السجل الوصفي باقٍ");
        var certPdf = await gov.GetBytesAsync($"/api/v1/records/destruction-requests/{reqId}/certificate?format=pdf");
        Check.That(Encoding.ASCII.GetString(certPdf, 0, 5) == "%PDF-", "شهادة الإتلاف PDF");
        Check.That(!Arr((await (await As("m1")).GetJsonAsync("/api/v1/search?q=p8-agenda-memo"))["items"]).Any(), "المتلف لا يظهر في البحث");
        await using var app = new PgWireDataSource(ctx.AppConnectionString);
        try { await new Db(app).ExecuteAsync("UPDATE docs.documents SET lifecycle_status = 'active', destroyed_at = NULL WHERE id = @memo", new { memo }); Check.That(false, "استعادة المتلف"); }
        catch (Exception e) when (e is not AssertionException) { Check.That(e.Message.Contains("restored"), "المتلف لا يُستعاد"); }
    }

    // ============================ د/هـ. التكاملات ============================

    [Test("P8-06", "الموصلات: المصادقة (رمز Entra) والتحقق من أدوار التطبيق، والفحص الفعلي قبل اعتبار التكامل عاملًا، ورفض بيانات الاعتماد الخاطئة دون تسريب السر", 6)]
    public async Task Connectors()
    {
        await Flags(("DIRECTORY_SYNC_ENABLED", "false"), ("VIDEO_MEETINGS_ENABLED", "false"), ("DOCUMENT_REPOSITORY_ENABLED", "false"));
        var list = Arr(await Admin.GetJsonAsync("/api/v1/admin/integrations")).ToList();
        var dir = list.Single(x => x.S("code") == "directory");
        Check.That(dir["missing"]!.AsArray().Count == 0 && !dir["verified"]!.GetValue<bool>(), "مهيأ لكنه غير مُتحقق منه قبل الفحص");
        Check.That(dir["requiredPermissions"]!.AsArray().Any(p => p!.GetValue<string>() == "User.Read.All"), "الصلاحيات المطلوبة معلنة");
        foreach (var code in new[] { "directory", "video_meetings", "document_repository", "correspondence", "email" })
            Check.Equal("healthy", (await Check.Status(await Admin.PostAsync($"/api/v1/admin/integrations/{code}/test"), 200, "فحص " + code))!.S("status"), "فحص ناجح: " + code);
        Check.That(fake.SeenAuth.Any(a => a.Contains(fake.CorrespondenceKey)), "مفتاح المراسلات أُرسل في الترويسة");
        list = Arr(await Admin.GetJsonAsync("/api/v1/admin/integrations")).ToList();
        Check.That(list.Where(x => x.S("code") is "directory" or "video_meetings" or "document_repository").All(x => x["verified"]!.GetValue<bool>() && !x["enabled"]!.GetValue<bool>()),
            "حالة الاتصال مستقلة عن التفعيل");

        // أدوار ناقصة: الفحص يفشل ويسمي الصلاحية
        fake.Roles = ["User.Read.All"];
        var t = (await Check.Status(await Admin.PostAsync("/api/v1/admin/integrations/video_meetings/test"), 200, "فحص بلا صلاحية"))!;
        Check.That(t.S("status") == "failing" && t.S("detail").Contains("OnlineMeetings.ReadWrite.All"), "الصلاحية الناقصة مسماة");
        fake.Roles = ["User.Read.All", "OnlineMeetings.ReadWrite.All", "Sites.Selected"];
        // سر خاطئ لدى خادم الهوية (يُرجع السر في رسالته عمدًا): لا يُسرب
        fake.DenyToken = true;
        t = (await Check.Status(await Admin.PostAsync("/api/v1/admin/integrations/directory/test"), 200, "فحص برمز مرفوض"))!;
        Check.That(t.S("status") == "failing" && t.S("detail").Contains("invalid_client") && !t.ToJsonString().Contains(fake.ClientSecret), "رفض المصادقة دون تسريب السر");
        fake.DenyToken = false;
        await Check.Status(await Admin.PostAsync("/api/v1/admin/integrations/directory/test"), 200, "استعادة");
        await Check.Problem(await (await As("sec")).PostAsync("/api/v1/admin/integrations/directory/test"), 403, "forbidden-action", "الفحص لمسؤول النظام");
    }

    [Test("P8-07", "مزامنة الدليل: معطلة بمفتاحها، ثم معاينة وتطبيق بالصفحات (تحديث الاسم، والربط، وتعطيل حساب معطل في الدليل وإبطال جلساته) دون إنشاء حسابات أو أدوار", 7)]
    public async Task DirectorySync()
    {
        fake.Users.Clear();
        fake.Users.AddRange([
            ("oid-chair", "p8-chair@test.jsdo.gov.sa", "م8 chair", null, true),
            ("oid-m1", "p8-m1@test.jsdo.gov.sa", "م8 العضو الأول (محدّث)", "مهندس بيانات", true),
            ("oid-m3", "p8-m3@test.jsdo.gov.sa", "م8 m3", null, false),
            ("oid-new", "new.person@test.jsdo.gov.sa", "موظف جديد", null, true),
        ]);
        var preview = await Admin.GetAsync("/api/v1/admin/directory-sync/preview");
        var body = (await Check.Status(preview, 409, "المزامنة معطلة"))!;
        Check.That(body.S("code") == "integration-failed" && body.S("reason") == "disabled", "تعطيل المفتاح يوقف الاستدعاء");
        Check.That(await Calls("directory", "skipped_disabled") >= 1, "مسجل: تخطٍّ بسبب التعطيل");
        await Flags(("DIRECTORY_SYNC_ENABLED", "true"));
        var p = (await Check.Status(await Admin.GetAsync("/api/v1/admin/directory-sync/preview"), 200, "معاينة"))!;
        Check.Equal(4L, L(p, "directoryUsers"), "كل الصفحات (nextLink)");
        var changes = Arr(p["changes"]).ToList();
        Check.That(changes.Any(c => c.S("email") == "p8-m1@test.jsdo.gov.sa" && c.S("kind") == "update") && changes.Any(c => c.S("email") == "p8-m3@test.jsdo.gov.sa" && c.S("kind") == "disable")
            && changes.Any(c => c.S("email") == "p8-chair@test.jsdo.gov.sa" && c.S("kind") == "link"), "التغييرات المقترحة");
        Check.Equal(1L, L(p, "notProvisionedCount"), "غير المسجل يُعرض ولا يُنشأ");
        var m3 = await NewSession("m3");
        var applied = (await Check.Status(await Admin.PostAsync("/api/v1/admin/directory-sync/apply"), 200, "تطبيق"))!;
        Check.That(L(applied, "disabled") == 1 && L(applied, "updated") == 2, "التطبيق");
        Check.Equal("م8 العضو الأول (محدّث)", await Owner.ScalarAsync<string>("SELECT display_name_ar FROM iam.users WHERE id = @id", new { id = _u["m1"] }), "الاسم محدث");
        Check.Equal("oid-chair", await Owner.ScalarAsync<string>("SELECT entra_object_id FROM iam.users WHERE id = @id", new { id = _u["chair"] }), "الربط بمعرّف Entra");
        Check.Equal(0L, await Owner.ScalarAsync<long>("SELECT count(*) FROM iam.users WHERE lower(email) = 'new.person@test.jsdo.gov.sa'"), "لا إنشاء آلي");
        Check.Equal(401, (int)(await m3.GetAsync("/api/v1/me")).StatusCode, "جلسة الحساب المعطل أُبطلت");
        await Owner.ExecuteAsync("UPDATE iam.users SET status = 'active' WHERE id = @id", new { id = _u["m3"] });
    }

    [Test("P8-08", "الاجتماعات المرئية: معطلة بمفتاحها دون أثر على الاجتماع، وإعادة محاولة آمنة بعد أعطال مؤقتة دون اجتماع مكرر (createOrGet)، ورفض الاستدعاء عند غياب الصلاحية قبل الاتصال", 8)]
    public async Task Video()
    {
        var sec = await As("sec");
        await Check.Problem(await sec.PostAsync($"/api/v1/meetings/{_m2}/online-meeting"), 404, "feature-disabled", "المفتاح معطل");
        await Flags(("VIDEO_MEETINGS_ENABLED", "true"));
        await Check.Problem(await sec.PostAsync($"/api/v1/meetings/{_m2}/online-meeting"), 409, "in-person-meeting", "الاجتماع الحضوري لا يحتاج رابطًا");
        await Owner.ExecuteAsync("UPDATE meetings.meetings SET mode = 'hybrid' WHERE id = @id", new { id = _m2 });
        var retriesBefore = await Calls("video_meetings", "retrying");
        fake.FailNext = 2;
        var r = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m2}/online-meeting"), 200, "إنشاء بعد عطلين"))!;
        Check.That(r.S("onlineLink").StartsWith("https://teams.microsoft.com/"), "الرابط محفوظ");
        Check.Equal(retriesBefore + 2, await Calls("video_meetings", "retrying"), "محاولتان معادتان مسجلتان");
        Check.Equal(1, fake.MeetingsCreated, "اجتماع واحد لدى المنصة");
        var again = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m2}/online-meeting"), 200, "إعادة الطلب"))!;
        Check.That(again.S("onlineLink") == r.S("onlineLink") && fake.MeetingsCreated == 1, "عدم التكرار");
        Check.Equal(r.S("onlineLink"), (await sec.GetJsonAsync($"/api/v1/meetings/{_m2}"))["meeting"].S("onlineLink"), "الرابط في بطاقة الاجتماع");

        fake.Roles = ["User.Read.All", "Sites.Selected"];
        await Admin.PostAsync("/api/v1/admin/integrations/video_meetings/test");
        var graphBefore = fake.GraphRequests;
        var denied = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_m2}/online-meeting"), 409, "دون صلاحية"))!;
        Check.That(denied.S("reason") == "denied" && fake.GraphRequests == graphBefore, "الرفض قبل أي استدعاء لـ Graph");
        fake.Roles = ["User.Read.All", "OnlineMeetings.ReadWrite.All", "Sites.Selected"];
        await Admin.PostAsync("/api/v1/admin/integrations/video_meetings/test");
    }

    [Test("P8-09", "المهلة وقاطع الدائرة: انتهاء المهلة يُسجل ويُعاد للعمليات الآمنة، والإخفاقات المتتالية تفتح الدائرة فتتوقف الاستدعاءات، ثم تُستأنف", 9)]
    public async Task TimeoutAndCircuit()
    {
        fake.SlowMs = 2500;
        var t = (await Check.Status(await Admin.GetAsync("/api/v1/admin/directory-sync/preview"), 409, "مهلة"))!;
        Check.Equal("timeout", t.S("reason"), "سبب الفشل: المهلة");
        Check.That(await Calls("directory", "timeout") >= 1, "مسجل: انتهاء المهلة");
        fake.SlowMs = 0;
        fake.FailNext = 10;
        await Check.Status(await Admin.GetAsync("/api/v1/admin/directory-sync/preview"), 409, "إخفاق متتالٍ");
        var graphBefore = fake.GraphRequests;
        var open = (await Check.Status(await Admin.GetAsync("/api/v1/admin/directory-sync/preview"), 409, "الدائرة مفتوحة"))!;
        Check.That(open.S("reason") == "circuit_open" && fake.GraphRequests == graphBefore, "لا استدعاء والدائرة مفتوحة");
        var c = Arr(await Admin.GetJsonAsync("/api/v1/admin/integrations")).Single(x => x.S("code") == "directory");
        Check.Equal("open", c["circuit"].S("state"), "حالة القاطع في شاشة التكاملات");
        fake.FailNext = 0;
        await Check.Status(await Admin.PostAsync("/api/v1/admin/integrations/directory/circuit/reset"), 204, "إعادة ضبط");
        await Check.Status(await Admin.GetAsync("/api/v1/admin/directory-sync/preview"), 200, "استئناف");
        var calls = (await Admin.GetJsonAsync("/api/v1/admin/integrations/directory/calls")).AsArray();
        Check.That(calls.Any(x => x.S("status") == "circuit_open") && calls.Any(x => x.S("status") == "retrying"), "سجل الاستدعاءات");
    }

    [Test("P8-10", "مستودع الوثائق: الإيداع معطل بمفتاحه ويُسجل فشله، ثم إيداع فعلي مطابق للبايتات، وإعادة الإيداع لا تكرر، والمتلف لا يُودع", 10)]
    public async Task Repository()
    {
        var gov = await As("gov");
        var off = (await Check.Status(await gov.PostAsync($"/api/v1/documents/{_minutesPdf}/repository-export"), 409, "معطل"))!;
        Check.Equal("disabled", off.S("reason"), "السبب");
        Check.Equal(1L, await Owner.ScalarAsync<long>("SELECT count(*) FROM docs.repository_exports WHERE document_id = @id AND status = 'failed'", new { id = _minutesPdf }), "الفشل مسجل");
        await Flags(("DOCUMENT_REPOSITORY_ENABLED", "true"));
        var ok = (await Check.Status(await gov.PostAsync($"/api/v1/documents/{_minutesPdf}/repository-export"), 200, "إيداع"))!;
        Check.That(ok.S("remoteId").StartsWith("item-"), "معرّف المستودع");
        var bytes = await (await As("sec")).GetBytesAsync($"/api/v1/documents/{_minutesPdf}/content");
        var stored = fake.Files.Single(f => f.Key.StartsWith("CCMS/") && f.Key.EndsWith("-v2.pdf"));
        Check.That(stored.Value.SequenceEqual(bytes), "المحتوى المودع مطابق");
        await Check.Status(await gov.PostAsync($"/api/v1/documents/{_minutesPdf}/repository-export"), 200, "إعادة الإيداع");
        Check.Equal(1, fake.Files.Count(f => f.Key == stored.Key), "المسار نفسه — لا تكرار");
        var destroyed = await Owner.ScalarAsync<Guid>("SELECT id FROM docs.documents WHERE lifecycle_status = 'destroyed' LIMIT 1");
        await Check.Problem(await gov.PostAsync($"/api/v1/documents/{destroyed}/repository-export"), 409, "document-destroyed", "المتلف لا يُودع");
        await Check.Problem(await (await As("sec")).PostAsync($"/api/v1/documents/{_minutesPdf}/repository-export"), 403, "forbidden-action", "الإيداع لمسؤول السجلات");
    }

    [Test("P8-11", "البريد المؤسسي: فشل مؤقت لا يُعاد داخل الموصل (عدم تكرار الرسالة) ويؤجله صندوق الصادر، ثم يُرسل؛ وتعطيله يسجل الرسائل دون إرسال", 11)]
    public async Task Email()
    {
        await Flags(("EMAIL_INTEGRATION_ENABLED", "true"));
        async Task<Guid> Message(string tag)
        {
            var id = Guid.CreateVersion7();
            await Owner.ExecuteAsync("""
                INSERT INTO notify.outbox_messages (id, event_type, channel, recipient_user_id, recipient_address, subject, dedupe_key)
                VALUES (@id, 'p8.test', 'email', @u, 'p8-m1@test.jsdo.gov.sa', @s, @k)
                """, new { id, u = _u["m1"], s = "رسالة اختبار " + tag, k = "p8:" + tag + ":" + id });
            return id;
        }
        async Task<(string Status, int Attempts)> State(Guid id) =>
            (await Owner.QuerySingleAsync("SELECT status, attempts FROM notify.outbox_messages WHERE id = @id", new { id }, r => (r.GetString(0), r.GetInt32(1))))!;

        smtp.Transient451 = true;
        var m = await Message("transient");
        // عزل رسالة الاختبار عن رسائل معلقة أقدم من المراحل السابقة (الدفعة 50 رسالة بالأقدم)
        await Owner.ExecuteAsync("UPDATE notify.outbox_messages SET next_attempt_at = now() + interval '1 day' WHERE status = 'pending' AND channel = 'email' AND id <> @m", new { m });
        await Check.Status(await Admin.PostAsync("/api/v1/admin/outbox/dispatch"), 200, "توزيع");
        var s = await State(m);
        Check.That(s.Status == "pending" && s.Attempts >= 1, $"مؤجل للمحاولة التالية بتباعد ({s.Status}/{s.Attempts}/{await Owner.ScalarAsync<string>("SELECT last_error FROM notify.outbox_messages WHERE id = @m", new { m })})");
        Check.That(await Owner.ScalarAsync<long>("SELECT count(*) FROM integ.connector_calls WHERE connector = 'email' AND operation = 'send' AND attempt > 1") == 0, "لا إعادة داخل الموصل لعملية غير آمنة");
        Check.That(await Calls("email", "failed") >= 1, "الفشل مسجل");
        smtp.Transient451 = false;
        await Owner.ExecuteAsync("UPDATE notify.outbox_messages SET next_attempt_at = now() WHERE id = @m", new { m });
        await Check.Status(await Admin.PostAsync("/api/v1/admin/outbox/dispatch"), 200, "توزيع ثانٍ");
        for (var i = 0; i < 20 && (await State(m)).Status != "sent"; i++) await Task.Delay(250);
        Check.Equal("sent", (await State(m)).Status, "أُرسلت بعد زوال العطل");
        Check.That(smtp.Received.Contains("p8-m1@test.jsdo.gov.sa"), "استلمها الخادم");

        await Flags(("EMAIL_INTEGRATION_ENABLED", "false"));
        var attempts = smtp.Attempts;
        var off = await Message("disabled");
        await Owner.ExecuteAsync("UPDATE notify.outbox_messages SET next_attempt_at = now() + interval '1 day' WHERE status = 'pending' AND channel = 'email' AND id <> @off", new { off });
        await Check.Status(await Admin.PostAsync("/api/v1/admin/outbox/dispatch"), 200, "توزيع بالتعطيل");
        Check.Equal("logged", (await State(off)).Status, "تُسجل دون إرسال");
        Check.Equal(attempts, smtp.Attempts, "لم يُتصل بالخادم");
    }

    [Test("P8-12", "تعطيل كل تكامل لا يعطل النظام الأساسي: الاجتماعات والدعوات والمستندات والبحث التقليدي والتقارير والدخول تعمل، والتوقيعات السابقة لا تُحذف", 12)]
    public async Task DisableAll()
    {
        var signedBefore = await Owner.ScalarAsync<long>("SELECT count(*) FROM sig.signers WHERE status = 'signed'");
        var esign = (await Admin.GetJsonAsync("/api/v1/features"))["E_SIGNATURE_ENABLED"]!["value"]!.GetValue<string>();
        var signoffsBefore = await Owner.ScalarAsync<long>("SELECT count(*) FROM minutes.minutes_signoffs WHERE status = 'valid'");
        await Flags(("EMAIL_INTEGRATION_ENABLED", "false"), ("CORRESPONDENCE_INTEGRATION_ENABLED", "false"), ("E_SIGNATURE_ENABLED", "false"), ("AI_ENABLED", "false"),
            ("DIRECTORY_SYNC_ENABLED", "false"), ("DOCUMENT_REPOSITORY_ENABLED", "false"), ("VIDEO_MEETINGS_ENABLED", "false"));
        try
        {
            var sec = await As("sec");
            var m = (await Check.Status(await sec.PostAsync("/api/v1/meetings", new { bodyId = _bodyA, titleAr = "اجتماع دون أي تكامل", meetingType = "regular", startsAt = DateTime.UtcNow.AddDays(5), location = "القاعة" }), 201, "إنشاء اجتماع")).G("id");
            await Check.Status(await sec.PostAsync($"/api/v1/meetings/{m}/schedule"), 200, "جدولة");
            await Check.Status(await sec.PostAsync($"/api/v1/meetings/{m}/agenda-items", new { titleAr = "بند" }), 201, "بند");
            await Check.Status(await sec.PostAsync($"/api/v1/meetings/{m}/invitations/send"), 200, "الدعوات (إشعارات داخلية)");
            var doc = (await Check.Status(await sec.PostContentAsync("/api/v1/documents", File("p8-no-integrations.pdf", "مرفق", "meeting", m)), 201, "رفع مستند")).G("id");
            await Check.Status(await sec.GetAsync($"/api/v1/documents/{doc}/content"), 200, "تنزيل");
            Check.That(Arr((await sec.GetJsonAsync("/api/v1/search?q=" + Uri.EscapeDataString("دون أي تكامل")))["items"]).Any(), "البحث التقليدي مع تعطيل الذكاء الاصطناعي");
            await Check.Status(await (await As("exec")).GetAsync($"/api/v1/reports/executive?{Period}"), 200, "اللوحة");
            var chair = await NewSession("chair");
            await Check.Status(await chair.GetAsync("/api/v1/me"), 200, "الدخول المحلي مع تعطيل الدليل");
            Check.Equal(signedBefore, await Owner.ScalarAsync<long>("SELECT count(*) FROM sig.signers WHERE status = 'signed'"), "التوقيعات الإلكترونية السابقة باقية");
            Check.Equal(signoffsBefore, await Owner.ScalarAsync<long>("SELECT count(*) FROM minutes.minutes_signoffs WHERE status = 'valid'"), "الإقرارات السابقة باقية");
            await Check.Problem(await sec.PostAsync($"/api/v1/meetings/{m}/online-meeting"), 404, "feature-disabled", "الرابط المرئي فقط هو المتوقف");
        }
        finally
        {
            // يتوقع اختبار إعادة التشغيل أن الذكاء الاصطناعي مخزن مفعلًا
            await Flags(("AI_ENABLED", "true"), ("E_SIGNATURE_ENABLED", esign));
        }
    }

    [Test("P8-13", "الأسرار لا تظهر في سجلات الموصلات ولا التدقيق ولا الاستجابات ولا سجل الخادم", 13)]
    public async Task NoSecrets()
    {
        var secrets = new[] { fake.ClientSecret, fake.CorrespondenceKey };
        foreach (var s in secrets)
        {
            Check.Equal(0L, await Owner.ScalarAsync<long>("SELECT count(*) FROM integ.connector_calls WHERE coalesce(error, '') LIKE '%' || @s || '%'", new { s }), "سجل الموصلات");
            Check.Equal(0L, await Owner.ScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE coalesce(before::text, '') || coalesce(after::text, '') LIKE '%' || @s || '%'", new { s }), "التدقيق");
            Check.That(!(await Admin.GetJsonAsync("/api/v1/admin/integrations")).ToJsonString().Contains(s), "شاشة التكاملات");
            Check.That(!(await Admin.GetJsonAsync("/api/v1/admin/secrets")).ToJsonString().Contains(s), "شاشة الأسرار");
            Check.That(!ctx.App!.Log.Any(l => l.Contains(s)), "سجل الخادم");
        }
        Check.Equal(0L, await Owner.ScalarAsync<long>("SELECT count(*) FROM integ.connector_calls WHERE error ~ 'eyJ[A-Za-z0-9_-]+\\.'"), "لا رموز وصول في السجل");
    }
}
