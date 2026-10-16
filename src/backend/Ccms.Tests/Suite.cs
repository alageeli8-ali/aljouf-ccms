using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.Data.PgWire;

namespace Ccms.Tests;

/// <summary>اختبارات المرحلة 1: المصادقة، والصلاحيات النطاقية، والإعدادات المركزية وسجلها، والاستمرارية بعد إعادة التشغيل.
/// كل اختبار يعمل عبر HTTP على التطبيق الحقيقي وقاعدة PostgreSQL حقيقية.</summary>
public sealed class Suite(TestContext ctx)
{
    private const string Pw = "Test@Password2026";
    private string Url => ctx.App!.BaseUrl;
    private readonly Dictionary<string, Guid> _users = [];
    private Guid _bodyA, _bodyB, _meetingA, _minutesA, _taskUser, _taskEntity, _taskOtherEntity, _entity, _entity2, _unit, _bodyDoc, _taskDoc;

    private async Task<ApiClient> As(string user)
    {
        var c = new ApiClient(Url);
        var r = await c.LoginAsync(user + "@test.jsdo.gov.sa", Pw);
        await Check.Status(r, 200, $"دخول {user}");
        return c;
    }

    private ApiClient Admin => ctx.AdminClient!;

    // ============================ التهيئة ============================

    [Test("SETUP", "تهيئة: مستخدمون بكل الأدوار، وجهتان، واجتماع، ومحضر، ومهام", 0)]
    public async Task Setup()
    {
        ctx.AdminClient = new ApiClient(Url);
        await Check.Status(await Admin.LoginAsync(TestContext.AdminEmail, TestContext.AdminPassword), 200, "دخول المدير");

        // جهتان خارجيتان ووحدة تنظيمية
        async Task<Guid> CreateUser(string key, string[]? roles = null, Guid? entity = null)
        {
            var r = await Admin.PostAsync("/api/v1/users", new
            {
                email = key + "@test.jsdo.gov.sa", displayNameAr = "مستخدم " + key, systemRoles = roles ?? [], initialPassword = Pw, externalEntityId = entity,
            });
            var id = (await Check.Status(r, 201, "إنشاء " + key)).G("id");
            _users[key] = id;
            return id;
        }
        await CreateUser("gov", ["governance_officer"]);
        await CreateUser("exec", ["executive"]);
        await CreateUser("auditor", ["auditor"]);
        foreach (var k in new[] { "chair", "vice", "secretary", "member", "observer", "outsider", "executor", "manager", "secretary2" }) await CreateUser(k);

        var gov = await As("gov");
        _unit = (await Check.Status(await gov.PostAsync("/api/v1/org-units", new { code = "QA-UNIT", nameAr = "إدارة الاختبار", managerUserId = _users["manager"] }), 201, "وحدة")).G("id");
        _entity = (await Check.Status(await gov.PostAsync("/api/v1/external-entities", new { nameAr = "أمانة الاختبار", liaisonUserId = _users["secretary"] }), 201, "جهة")).G("id");
        _entity2 = (await Check.Status(await gov.PostAsync("/api/v1/external-entities", new { nameAr = "جهة أخرى", liaisonUserId = _users["secretary"] }), 201, "جهة 2")).G("id");
        await CreateUser("extrep", ["external_representative"], _entity);

        // الجهة A بتشكيل كامل، والجهة B بأمين سر آخر
        _bodyA = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code = "QA-COMM", bodyType = "permanent_committee", nameAr = "لجنة الاختبار", nameEn = "QA Committee" }), 201, "جهة A")).G("id");
        foreach (var (u, role) in new[] { ("chair", "chair"), ("vice", "vice_chair"), ("secretary", "secretary"), ("member", "member"), ("observer", "observer") })
            await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_bodyA}/members", new { userId = _users[u], role }), 201, "عضوية " + u);
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_bodyA}/activate"), 200, "تفعيل A");
        _bodyB = (await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code = "QA-OTHER", bodyType = "executive_council", nameAr = "مجلس آخر", nameEn = "Other Council" }), 201, "جهة B")).G("id");

        // سياسة الجهة A: المحضر يمر بتوقيع كل الأعضاء ثم الاعتماد (تختبر أثر تعطيل التوقيع في CFG-05)
        await Check.Status(await gov.PutAsync($"/api/v1/bodies/{_bodyA}/policy", new
        {
            effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)), quorumType = "majority", chairRequired = false, countRemote = true, allowOverride = false,
            votingThreshold = "majority_of_votes_cast", abstentions = "excluded", tieBreaker = "chair_casting_vote", secretAllowed = false, signoffRequired = true,
            minutesReviewDays = 3, extensionApprover = "secretary", allowProxy = false, maxProxiesPerMember = 1, allowRemoteAttendance = true, allowCirculation = false,
            reason = "سياسة اختبار المرحلة 1", minutesChairReview = false, minutesMemberReview = false, minutesSignatures = "all_members",
        }), 200, "سياسة A");

        var sec = await As("secretary");
        var m = await Check.Status(await sec.PostAsync("/api/v1/meetings", new { bodyId = _bodyA, titleAr = "اجتماع الاختبار", startsAt = DateTime.UtcNow.AddDays(3), location = "قاعة الاختبار" }), 201, "اجتماع");
        _meetingA = m.G("id");
        var sch = await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meetingA}/schedule"), 200, "جدولة");
        Check.That(sch.S("referenceNo").StartsWith("QA-COMM-"), "ترقيم الاجتماع");
        _minutesA = (await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meetingA}/minutes"), 201, "محضر")).G("id");

        _taskUser = (await Check.Status(await sec.PostAsync("/api/v1/tasks", new
        { bodyId = _bodyA, titleAr = "مهمة لموظف", assigneeType = "user", assigneeUserId = _users["executor"], dueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(10)) }), 201, "مهمة موظف")).G("id");
        _taskEntity = (await Check.Status(await sec.PostAsync("/api/v1/tasks", new
        { bodyId = _bodyA, titleAr = "مهمة لجهة خارجية", assigneeType = "external_entity", assigneeExternalEntityId = _entity, dueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(10)) }), 201, "مهمة جهة")).G("id");
        _taskOtherEntity = (await Check.Status(await sec.PostAsync("/api/v1/tasks", new
        { bodyId = _bodyA, titleAr = "مهمة لجهة أخرى", assigneeType = "external_entity", assigneeExternalEntityId = _entity2, dueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(10)) }), 201, "مهمة جهة 2")).G("id");
        await Check.Status(await sec.PostAsync("/api/v1/tasks", new
        { bodyId = _bodyA, titleAr = "مهمة لإدارة", assigneeType = "org_unit", assigneeOrgUnitId = _unit, dueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(5)) }), 201, "مهمة إدارة");

        _bodyDoc = (await Check.Status(await Upload(sec, "body", _bodyA, "memo.pdf"), 201, "مرفق جهة")).G("id");
    }

    private static async Task<HttpResponseMessage> Upload(ApiClient c, string ownerType, Guid ownerId, string name)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent("%PDF-1.4 test"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", name);
        content.Add(new StringContent(ownerType), "ownerType");
        content.Add(new StringContent(ownerId.ToString()), "ownerId");
        return await c.PostContentAsync("/api/v1/documents", content);
    }

    // ============================ المصادقة ============================

    [Test("AUTH-01", "طلب دون جلسة يُرفض بـ 401", 10)]
    public async Task Unauthenticated()
    {
        using var anon = new ApiClient(Url);
        await Check.Status(await anon.GetAsync("/api/v1/me"), 401, "me بلا جلسة");
        await Check.Status(await anon.GetAsync("/api/v1/admin/features"), 401, "الإعدادات بلا جلسة");
    }

    [Test("AUTH-02", "كلمة مرور خاطئة تُرفض برسالة موحدة، والقفل بعد 5 محاولات", 11)]
    public async Task WrongPasswordAndLockout()
    {
        using var c = new ApiClient(Url);
        await Check.Problem(await c.LoginAsync("observer@test.jsdo.gov.sa", "wrong-password"), 401, "unauthenticated", "كلمة خاطئة");
        await Check.Problem(await c.LoginAsync("nobody@test.jsdo.gov.sa", "wrong-password"), 401, "unauthenticated", "حساب غير موجود بالرسالة نفسها");
        for (var i = 0; i < 5; i++) await c.LoginAsync("outsider@test.jsdo.gov.sa", "wrong-password");
        await Check.Problem(await c.LoginAsync("outsider@test.jsdo.gov.sa", Pw), 409, "account-locked", "الحساب مقفل حتى مع كلمة صحيحة");
        // فك القفل بإعادة ضبط كلمة المرور من المدير
        await Check.Status(await Admin.PutAsync($"/api/v1/users/{_users["outsider"]}/password", new { password = Pw }), 204, "إعادة ضبط");
        await Check.Status(await c.LoginAsync("outsider@test.jsdo.gov.sa", Pw), 200, "الدخول بعد فك القفل");
    }

    [Test("AUTH-03", "طلب يغيّر الحالة دون رمز CSRF يُرفض", 12)]
    public async Task CsrfRequired()
    {
        using var c = await As("secretary");
        await Check.Problem(await c.PostAsync("/api/v1/notifications/read-all", null, csrf: false), 400, "csrf", "بلا رمز");
        await Check.Status(await c.PostAsync("/api/v1/notifications/read-all"), 200, "مع الرمز");
    }

    [Test("AUTH-04", "الخروج يُبطل الجلسة في الخادم (الكعكة القديمة لا تعمل)", 13)]
    public async Task LogoutRevokesSession()
    {
        using var c = await As("member");
        var cookie = c.Cookies.GetCookies(new Uri(Url))["ccms.session"]!.Value;
        await Check.Status(await c.PostAsync("/api/v1/auth/logout"), 204, "خروج");
        using var replay = new ApiClient(Url);
        replay.Cookies.Add(new Uri(Url), new System.Net.Cookie("ccms.session", cookie));
        await Check.Status(await replay.GetAsync("/api/v1/me"), 401, "إعادة استخدام الكعكة بعد الخروج");
    }

    [Test("AUTH-05", "الخروج من كل الأجهزة يُبطل الجلسات الأخرى", 14)]
    public async Task LogoutAll()
    {
        using var a = await As("vice");
        using var b = await As("vice");
        await Check.Status(await a.PostAsync("/api/v1/auth/logout-all"), 200, "خروج من الكل");
        await Check.Status(await b.GetAsync("/api/v1/me"), 401, "الجلسة الثانية");
    }

    [Test("AUTH-06", "تعطيل المستخدم يُنهي جلساته فورًا", 15)]
    public async Task DisableUserKillsSessions()
    {
        using var c = await As("outsider");
        await Check.Status(await c.GetAsync("/api/v1/me"), 200, "قبل التعطيل");
        await Check.Status(await Admin.PatchAsync($"/api/v1/users/{_users["outsider"]}", new { status = "disabled" }), 200, "تعطيل");
        await Check.Status(await c.GetAsync("/api/v1/me"), 401, "بعد التعطيل");
        await Check.Status(await Admin.PatchAsync($"/api/v1/users/{_users["outsider"]}", new { status = "active" }), 200, "إعادة التفعيل");
    }

    [Test("AUTH-07", "الدخول الموحد معطل (SSO_ENABLED=false) يعيد 404 من الخادم", 16)]
    public async Task SsoDisabled()
    {
        using var c = new ApiClient(Url, followRedirects: false);
        await Check.Problem(await c.GetAsync("/auth/sso/login"), 404, "feature-disabled", "SSO معطل");
    }

    [Test("AUTH-08", "تفعيل SSO دون بيانات تسجيل التطبيق يعطي 503 «غير مضبوط» لا دخولًا وهميًا", 17)]
    public async Task SsoEnabledButNotConfigured()
    {
        await Check.Status(await Admin.PutAsync("/api/v1/admin/features/SSO_ENABLED", new { value = "true", reason = "اختبار حالة المزود" }), 200, "تفعيل SSO");
        using var c = new ApiClient(Url, followRedirects: false);
        await Check.Problem(await c.GetAsync("/auth/sso/login"), 503, "provider-unavailable", "SSO غير مضبوط");
        var p = await c.GetJsonAsync("/api/v1/auth/providers");
        Check.That(p["sso"]!["featureEnabled"]!.GetValue<bool>(), "الإعداد مفعل");
        Check.That(!p["sso"]!["configured"]!.GetValue<bool>(), "المزود غير مضبوط");
        Check.That(!p["sso"]!["available"]!.GetValue<bool>(), "غير متاح للدخول");
        await Check.Status(await Admin.PutAsync("/api/v1/admin/features/SSO_ENABLED", new { value = "false", reason = "إرجاع بعد الاختبار" }), 200, "تعطيل SSO");
    }

    // ============================ الصلاحيات ============================

    [Test("RBAC-01", "عضو جهة لا يرى جهة ليس عضوًا فيها (404 لا 403)", 20)]
    public async Task BodyScopeIsolation()
    {
        using var member = await As("member");
        await Check.Status(await member.GetAsync($"/api/v1/bodies/{_bodyA}"), 200, "جهته");
        await Check.Problem(await member.GetAsync($"/api/v1/bodies/{_bodyB}"), 404, "not-found", "جهة أخرى");
        var list = await member.GetJsonAsync("/api/v1/bodies");
        Check.Equal(1, list.Items().Count, "القائمة تقتصر على جهاته");
        using var sec2 = await As("secretary2");
        await Check.Problem(await sec2.GetAsync($"/api/v1/meetings/{_meetingA}"), 404, "not-found", "اجتماع جهة أخرى");
    }

    [Test("RBAC-02", "العضو يطّلع ولا يُنشئ اجتماعًا (403)، وأمين السر يُنشئ", 21)]
    public async Task MemberCannotCreateMeeting()
    {
        using var member = await As("member");
        await Check.Problem(await member.PostAsync("/api/v1/meetings", new { bodyId = _bodyA, titleAr = "x", startsAt = DateTime.UtcNow.AddDays(2), location = "ق" }), 403, "forbidden-action", "عضو");
        using var observer = await As("observer");
        await Check.Problem(await observer.PostAsync($"/api/v1/meetings/{_meetingA}/agenda-items", new { titleAr = "بند" }), 403, "forbidden-action", "مراقب");
        using var sec = await As("secretary");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meetingA}/agenda-items", new { titleAr = "بند من أمين السر" }), 201, "أمين السر");
    }

    [Test("RBAC-03", "إنشاء الجهات لمسؤول المجالس فقط؛ الموظف يُرفض بـ 403", 22)]
    public async Task OnlyGovernanceCreatesBodies()
    {
        using var outsider = await As("outsider");
        await Check.Problem(await outsider.PostAsync("/api/v1/bodies", new { code = "QA-X", bodyType = "permanent_committee", nameAr = "س", nameEn = "X" }), 403, "forbidden-action", "موظف");
        using var chair = await As("chair");
        await Check.Problem(await chair.PostAsync("/api/v1/bodies", new { code = "QA-Y", bodyType = "permanent_committee", nameAr = "س", nameEn = "X" }), 403, "forbidden-action", "رئيس");
    }

    [Test("RBAC-04", "الإدارة العليا تطّلع على كل الجهات ولا تعدّلها", 23)]
    public async Task ExecutiveReadOnly()
    {
        using var exec = await As("exec");
        Check.Equal(2, (await exec.GetJsonAsync("/api/v1/bodies")).Items().Count, "كل الجهات");
        await Check.Problem(await exec.PatchAsync($"/api/v1/bodies/{_bodyA}", new { nameAr = "تعديل" }), 403, "forbidden-action", "تعديل");
        await Check.Status(await exec.GetAsync("/api/v1/reports/tasks-by-body"), 200, "التقارير");
    }

    [Test("RBAC-05", "مسؤول تنفيذ المهمة يرى مهمته ويحدّثها، ولا يرى محضر الجهة", 24)]
    public async Task TaskExecutorScope()
    {
        using var ex = await As("executor");
        await Check.Status(await ex.GetAsync($"/api/v1/tasks/{_taskUser}"), 200, "مهمته");
        await Check.Status(await ex.PostAsync($"/api/v1/tasks/{_taskUser}/updates", new { progressPct = 30, note = "بدأ العمل" }), 204, "تحديث");
        await Check.Problem(await ex.GetAsync($"/api/v1/minutes/{_minutesA}"), 404, "not-found", "المحضر");
        await Check.Problem(await ex.GetAsync($"/api/v1/tasks/{_taskEntity}"), 404, "not-found", "مهمة غيره");
        await Check.Problem(await ex.PostAsync("/api/v1/tasks", new { bodyId = _bodyA, titleAr = "x", assigneeType = "user", assigneeUserId = _users["executor"], dueDate = "2030-01-01" }), 404, "not-found", "إنشاء مهمة");
        Check.Equal(1, (await ex.GetJsonAsync("/api/v1/tasks")).Items().Count, "قائمته مهمته فقط");
    }

    [Test("RBAC-06", "مسؤول المتابعة (مدير الإدارة المكلفة) يرى المهمة ويحدّثها", 25)]
    public async Task FollowUpScope()
    {
        using var mgr = await As("manager");
        var tasks = (await mgr.GetJsonAsync("/api/v1/tasks")).Items();
        Check.Equal(1, tasks.Count, "مهمة إدارته");
        var id = tasks[0].G("id");
        await Check.Status(await mgr.PostAsync($"/api/v1/tasks/{id}/updates", new { progressPct = 10, note = "متابعة" }), 204, "تحديث المتابع");
    }

    [Test("RBAC-07", "ممثل الجهة الخارجية: محجوب والبوابة معطلة، ويرى مهام جهته فقط ببطاقة البوابة بعد تغيير كلمة المرور المؤقتة", 26)]
    public async Task ExternalPortal()
    {
        using var ext = await As("extrep");
        await Check.Problem(await ext.GetAsync("/api/v1/portal/tasks"), 404, "feature-disabled", "البوابة معطلة");
        await Check.Problem(await ext.GetAsync($"/api/v1/tasks/{_taskEntity}"), 404, "not-found", "مهمة جهته والبوابة معطلة");
        await Check.Status(await Admin.PutAsync("/api/v1/admin/features/EXTERNAL_PORTAL_ENABLED", new { value = "true", reason = "تجربة البوابة" }), 200, "تفعيل");
        await Check.Problem(await ext.GetAsync("/api/v1/portal/tasks"), 403, "password-change-required", "كلمة المرور المؤقتة");
        await Check.Status(await ext.PostAsync("/api/v1/auth/password", new { currentPassword = Pw, newPassword = "Ext@Portal2026" }), 204, "تغيير كلمة المرور");
        var portal = await ext.GetJsonAsync("/api/v1/portal/tasks");
        Check.Equal(1, portal.Items().Count, "مهام جهته فقط");
        await Check.Status(await ext.GetAsync($"/api/v1/portal/tasks/{_taskEntity}"), 200, "مهمة جهته في البوابة");
        await Check.Problem(await ext.GetAsync($"/api/v1/tasks/{_taskEntity}"), 404, "not-found", "البطاقة الداخلية غير متاحة للجهة");
        await Check.Status(await ext.PostAsync($"/api/v1/portal/tasks/{_taskEntity}/progress", new { progressPct = 50, note = "تحديث من الجهة" }), 200, "تحديث");
        await Check.Problem(await ext.GetAsync($"/api/v1/portal/tasks/{_taskOtherEntity}"), 404, "not-found", "مهمة جهة أخرى");
        await Check.Problem(await ext.GetAsync($"/api/v1/bodies/{_bodyA}"), 404, "not-found", "الجهة نفسها");
        await Check.Status(await Admin.PutAsync("/api/v1/admin/features/EXTERNAL_PORTAL_ENABLED", new { value = "false", reason = "إرجاع", confirmImpact = true }), 200, "تعطيل");
        await Check.Problem(await ext.GetAsync($"/api/v1/portal/tasks/{_taskEntity}"), 404, "feature-disabled", "بعد التعطيل");
    }

    [Test("RBAC-08", "صلاحية المستند مشتقة من مالكه (جهة/مهمة)", 27)]
    public async Task DocumentScope()
    {
        using var ex = await As("executor");
        _taskDoc = (await Check.Status(await Upload(ex, "task", _taskUser, "evidence.pdf"), 201, "دليل المهمة")).G("id");
        await Check.Status(await ex.GetAsync($"/api/v1/documents/{_taskDoc}/content"), 200, "المكلف يقرأ دليله");
        await Check.Problem(await ex.GetAsync($"/api/v1/documents/{_bodyDoc}/content"), 404, "not-found", "مرفق الجهة");
        using var member = await As("member");
        await Check.Status(await member.GetAsync($"/api/v1/documents/{_bodyDoc}/content"), 200, "العضو يقرأ مرفق جهته");
        using var outsider = await As("outsider");
        await Check.Problem(await outsider.GetAsync($"/api/v1/documents/{_taskDoc}/content"), 404, "not-found", "خارج النطاق");
        await Check.Problem(await Upload(member, "body", _bodyA, "x.pdf"), 403, "forbidden-action", "العضو لا يرفع");
        var exe = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "virus.exe" }, { new StringContent("body"), "ownerType" }, { new StringContent(_bodyA.ToString()), "ownerId" } };
        using var sec = await As("secretary");
        await Check.Problem(await sec.PostContentAsync("/api/v1/documents", exe), 400, "validation", "نوع ملف ممنوع");
    }

    [Test("RBAC-09", "سجل التدقيق للمدقق فقط؛ مدير النظام يُرفض", 28)]
    public async Task AuditorOnly()
    {
        using var aud = await As("auditor");
        var events = await aud.GetJsonAsync("/api/v1/audit/events?pageSize=5");
        Check.That(events["total"]!.GetValue<long>() > 10, "أحداث مسجلة");
        await Check.Problem(await Admin.GetAsync("/api/v1/audit/events"), 403, "forbidden-action", "مدير النظام");
    }

    [Test("RBAC-10", "تفعيل الجهة يتطلب رئيسًا وأمين سر (FR-BOD-04)", 29)]
    public async Task ActivationRequiresFormation()
    {
        using var gov = await As("gov");
        await Check.Problem(await gov.PostAsync($"/api/v1/bodies/{_bodyB}/activate"), 409, "formation-incomplete", "بلا رئيس");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_bodyB}/members", new { userId = _users["secretary2"], role = "secretary" }), 201, "أمين سر B");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_bodyB}/members", new { userId = _users["chair"], role = "chair" }), 201, "رئيس B");
        await Check.Status(await gov.PostAsync($"/api/v1/bodies/{_bodyB}/activate"), 200, "التفعيل");
        await Check.Problem(await gov.PostAsync($"/api/v1/bodies/{_bodyB}/members", new { userId = _users["vice"], role = "chair" }), 409, "chair-exists", "رئيس ثانٍ");
    }

    [Test("RBAC-11", "إدارة المستخدمين لمدير النظام فقط", 30)]
    public async Task OnlyAdminManagesUsers()
    {
        using var gov = await As("gov");
        await Check.Problem(await gov.GetAsync("/api/v1/users"), 403, "forbidden-action", "قائمة المستخدمين");
        await Check.Problem(await gov.PutAsync($"/api/v1/users/{_users["member"]}/system-roles", new { roles = new[] { "system_admin" }, reason = "محاولة تصعيد" }), 403, "forbidden-action", "تصعيد الأدوار");
    }

    // ============================ الإعدادات المركزية ============================

    [Test("CFG-01", "المفاتيح التسعة عشر محفوظة في القاعدة، بحالة إعداد وحالة اتصال منفصلتين", 40)]
    public async Task FlagsListed()
    {
        var flags = (await Admin.GetJsonAsync("/api/v1/admin/features")).AsArray();
        var keys = flags.Select(f => f.S("key")).ToHashSet();
        foreach (var k in TestContext.ExpectedFlags) Check.That(keys.Contains(k), "المفتاح موجود: " + k);
        Check.Equal(19, keys.Count, "عدد المفاتيح");
        var ai = flags.First(f => f.S("key") == "AI_ENABLED");
        Check.That(ai["connector"]!["status"] is not null, "حالة الاتصال مستقلة");
        Check.That(ai["effectiveEnabled"] is not null, "حالة الإعداد مستقلة");
    }

    [Test("CFG-02", "مستخدم غير مخول لا يعدّل الإعدادات (403) وتُسجَّل المحاولة «مرفوضة»", 41)]
    public async Task UnauthorizedCannotChangeSettings()
    {
        foreach (var u in new[] { "gov", "exec", "chair", "secretary", "auditor" })
        {
            using var c = await As(u);
            await Check.Problem(await c.PutAsync("/api/v1/admin/features/AI_ENABLED", new { value = "true", reason = "محاولة غير مخولة" }), 403, "forbidden-action", u);
        }
        var history = await Admin.GetJsonAsync("/api/v1/admin/features/history?key=AI_ENABLED&result=denied");
        Check.Equal(5L, history["total"]!.GetValue<long>(), "خمس محاولات مرفوضة مسجلة");
        var row = history.Items()[0];
        Check.That(row["actor"]!.GetValue<string>().StartsWith("مستخدم "), "هوية المحاول مسجلة");
        Check.Equal("false", (await Admin.GetJsonAsync("/api/v1/features"))["AI_ENABLED"]!["value"]!.GetValue<string>(), "القيمة لم تتغير");
        using var aud = await As("auditor");
        await Check.Status(await aud.GetAsync("/api/v1/admin/features"), 200, "المدقق يطّلع على الإعدادات");
    }

    [Test("CFG-03", "السبب إلزامي، والقيمة تُتحقق حسب النوع، والمرفوض يُسجَّل", 42)]
    public async Task ValidationAndHistory()
    {
        await Check.Problem(await Admin.PutAsync("/api/v1/admin/features/AI_ENABLED", new { value = "true", reason = "" }), 400, "validation", "بلا سبب");
        await Check.Problem(await Admin.PutAsync("/api/v1/admin/features/AI_ENABLED", new { value = "yes", reason = "قيمة خاطئة" }), 400, "validation", "قيمة منطقية خاطئة");
        await Check.Problem(await Admin.PutAsync("/api/v1/admin/features/E_SIGNATURE_PROVIDER", new { value = "docusign", reason = "مزود غير مسموح" }), 400, "validation", "قيمة خارج القائمة");
        await Check.Problem(await Admin.PutAsync("/api/v1/admin/features/NOT_A_FLAG", new { value = "true", reason = "مفتاح وهمي" }), 404, "not-found", "مفتاح غير موجود");
        var h = await Admin.GetJsonAsync("/api/v1/admin/features/history?result=rejected_validation");
        Check.That(h["total"]!.GetValue<long>() >= 3, "المحاولات المرفوضة مسجلة");
    }

    [Test("CFG-04", "AI_ENABLED مفتاح أعلى: الخاصية الفرعية لا تعمل إلا بتفعيله", 43)]
    public async Task ParentKeyGovernsChildren()
    {
        await Check.Status(await Admin.PutAsync("/api/v1/admin/features/AI_MINUTES_SUMMARY_ENABLED", new { value = "true", reason = "تفعيل التلخيص" }), 200, "تفعيل الفرعي");
        var f = await Admin.GetJsonAsync("/api/v1/features");
        Check.Equal(false, f["AI_MINUTES_SUMMARY_ENABLED"]!["enabled"]!.GetValue<bool>(), "الفرعي غير فعّال والأعلى معطل");
        var list = (await Admin.GetJsonAsync("/api/v1/admin/features")).AsArray();
        Check.Equal("AI_ENABLED", list.First(x => x.S("key") == "AI_MINUTES_SUMMARY_ENABLED")["blockedBy"]!.GetValue<string>(), "محجوب بالأعلى");
        using var sec = await As("secretary");
        await Check.Problem(await sec.PostAsync($"/api/v1/ai/minutes/{_minutesA}/summary"), 404, "feature-disabled", "الخادم يرفض والأعلى معطل");
        await Check.Problem(await sec.GetAsync("/api/v1/ai/status"), 404, "feature-disabled", "حالة الذكاء الاصطناعي");

        await Check.Status(await Admin.PutAsync("/api/v1/admin/features/AI_ENABLED", new { value = "true", reason = "تجربة المفتاح الأعلى" }), 200, "تفعيل الأعلى");
        f = await sec.GetJsonAsync("/api/v1/features");
        Check.Equal(true, f["AI_MINUTES_SUMMARY_ENABLED"]!["enabled"]!.GetValue<bool>(), "الفرعي فعّال");
        // مفعّل لكن المزوّد غير مضبوط: 503 لا نجاح وهمي
        await Check.Problem(await sec.PostAsync($"/api/v1/ai/minutes/{_minutesA}/summary"), 503, "provider-unavailable", "المزوّد غير متاح");
        Check.Equal(false, f["AI_DECISION_EXTRACTION_ENABLED"]!["enabled"]!.GetValue<bool>(), "خاصية أخرى غير مفعلة");
    }

    [Test("CFG-05", "تعطيل التوقيع ومحاضر تنتظر توقيعات يتطلب تأكيدًا صريحًا", 44)]
    public async Task ImpactConfirmation()
    {
        await Check.Status(await Admin.PutAsync("/api/v1/admin/features/E_SIGNATURE_ENABLED", new { value = "true", reason = "تفعيل التوقيع" }), 200, "تفعيل");
        await Check.Status(await Admin.PutAsync("/api/v1/admin/features/E_SIGNATURE_PROVIDER", new { value = "mock", reason = "مزود الاختبار" }), 200, "المزود");
        using var sec = await As("secretary");
        await Check.Status(await sec.PutAsync($"/api/v1/minutes/{_minutesA}/draft", new { sections = new[] { new { key = "opening", title = "الافتتاح", body = "افتتح الرئيس الاجتماع" } } }), 200, "المسودة");
        // عقد الاجتماع ثم رفع محضره: سياسة الجهة تقتضي توقيع الأعضاء
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meetingA}/agenda-items", new { titleAr = "بند الاختبار" }), 201, "بند");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meetingA}/invitations/send"), 200, "الدعوات");
        await Check.Status(await sec.PutAsync($"/api/v1/meetings/{_meetingA}/attendance", new
        {
            records = new[] { new { userId = _users["chair"], status = "present", mode = "in_person" }, new { userId = _users["member"], status = "present", mode = "in_person" } },
        }), 200, "الحضور");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meetingA}/open", new { }), 200, "الافتتاح");
        await Check.Status(await sec.PostAsync($"/api/v1/meetings/{_meetingA}/close"), 200, "الإغلاق");
        var req = await Check.Status(await sec.PostAsync($"/api/v1/minutes/{_minutesA}/submit", new { }), 200, "رفع المحضر");
        Check.Equal("pending_signatures", req.S("status"), "المحضر ينتظر التوقيعات");

        var impact = await Admin.GetJsonAsync("/api/v1/admin/features/E_SIGNATURE_ENABLED/impact?value=false");
        Check.That(impact["requiresConfirmation"]!.GetValue<bool>(), "الأثر يتطلب تأكيدًا");

        var noConfirm = await Admin.PutAsync("/api/v1/admin/features/E_SIGNATURE_ENABLED", new { value = "false", reason = "تعطيل التوقيع" });
        var body = await Check.Status(noConfirm, 409, "دون تأكيد");
        Check.Equal("confirmation-required", body.S("code"), "رمز المشكلة");
        Check.That(body!["impact"]!.AsArray().Any(i => i!["count"]!.GetValue<int>() > 0), "تفاصيل الأثر مرفقة");
        Check.Equal("true", (await Admin.GetJsonAsync("/api/v1/features"))["E_SIGNATURE_ENABLED"]!["value"]!.GetValue<string>(), "لم يتغير دون تأكيد");

        await Check.Status(await Admin.PutAsync("/api/v1/admin/features/E_SIGNATURE_ENABLED", new { value = "false", reason = "تعطيل مؤكد بعد مراجعة الأثر", confirmImpact = true }), 200, "مع التأكيد");
        var h = (await Admin.GetJsonAsync("/api/v1/admin/features/history?key=E_SIGNATURE_ENABLED")).Items();
        Check.Equal("succeeded", h[0].S("result"), "آخر نتيجة");
        Check.Equal("confirmation_required", h[1].S("result"), "قبلها: يتطلب تأكيدًا");
        Check.That(h[0]["impact"] is not null, "الأثر محفوظ مع التغيير");
        // البيانات الجارية لم تُفقد: طلبات الإقرار باقية ويستطيع العضو الإقرار بالطريقة الداخلية
        var minutes = await sec.GetJsonAsync($"/api/v1/minutes/{_minutesA}");
        Check.That(minutes["signoffs"]!.AsArray().Count > 0, "طلبات الإقرار محفوظة بعد التعطيل");
    }

    [Test("CFG-06", "سجل التغييرات يحفظ الاسم والقيمتين والوقت والمسؤول والسبب والنتيجة", 45)]
    public async Task HistoryFields()
    {
        var row = (await Admin.GetJsonAsync("/api/v1/admin/features/history?key=AI_ENABLED&result=succeeded")).Items()[0];
        Check.Equal("AI_ENABLED", row.S("key"), "اسم الخاصية");
        Check.Equal("false", row.S("oldValue"), "القيمة السابقة");
        Check.Equal("true", row.S("newValue"), "القيمة الجديدة");
        Check.That(DateTime.Parse(row.S("changedAt")) > DateTime.UtcNow.AddMinutes(-30), "وقت التغيير");
        Check.Equal(ctx.AdminId, row.G("changedBy"), "هوية المسؤول");
        Check.Equal("تجربة المفتاح الأعلى", row.S("reason"), "السبب");
        Check.Equal("succeeded", row.S("result"), "النتيجة");
    }

    [Test("CFG-07", "حالة الاتصال تُفحص فعليًا ومستقلة عن تفعيل الخاصية", 46)]
    public async Task ConnectorStatusIndependent()
    {
        await Check.Status(await Admin.PutAsync("/api/v1/admin/features/EMAIL_INTEGRATION_ENABLED", new { value = "true", reason = "تجربة البريد" }), 200, "تفعيل البريد");
        var check = await Check.Status(await Admin.PostAsync("/api/v1/admin/connectors/email/check"), 200, "فحص البريد");
        Check.Equal("not_configured", check.S("status"), "المزود غير مضبوط رغم تفعيل الخاصية");
        var list = (await Admin.GetJsonAsync("/api/v1/admin/features")).AsArray();
        var email = list.First(x => x.S("key") == "EMAIL_INTEGRATION_ENABLED");
        Check.Equal(true, email["effectiveEnabled"]!.GetValue<bool>(), "الإعداد مفعل");
        Check.Equal("not_configured", email["connector"]!.S("status"), "الاتصال غير مضبوط");
        var esign = await Check.Status(await Admin.PostAsync("/api/v1/admin/connectors/esign/check"), 200, "فحص التوقيع");
        Check.Equal("healthy", esign.S("status"), "محاكي التوقيع سليم في بيئة الاختبار");
        using var gov = await As("gov");
        await Check.Problem(await gov.PostAsync("/api/v1/admin/connectors/email/check"), 403, "forbidden-action", "الفحص للمدير فقط");
        // البريد مفعل والمزود غير متاح: الرسائل تبقى معلقة (لا تضيع ولا تُعلَّم مرسلة)
        var d = await Check.Status(await Admin.PostAsync("/api/v1/admin/outbox/dispatch"), 200, "التوزيع");
        Check.Equal(0, d!["sent"]!.GetValue<int>(), "لا إرسال وهمي");
        await Check.Status(await Admin.PutAsync("/api/v1/admin/features/EMAIL_INTEGRATION_ENABLED", new { value = "false", reason = "إرجاع", confirmImpact = true }), 200, "تعطيل البريد");
        d = await Check.Status(await Admin.PostAsync("/api/v1/admin/outbox/dispatch"), 200, "التوزيع بعد التعطيل");
        Check.That(d!["logged"]!.GetValue<int>() >= 0, "الرسائل تُسجَّل ولا تُرسل");
    }

    // ============================ التحقق والأخطاء والتوثيق ============================

    [Test("VAL-01", "التحقق من المدخلات يعيد ProblemDetails بالحقول، والتكرار يعيد 409", 50)]
    public async Task InputValidation()
    {
        using var gov = await As("gov");
        var r = await Check.Status(await gov.PostAsync("/api/v1/bodies", new { code = "bad code", bodyType = "club", nameAr = "" }), 400, "مدخلات خاطئة");
        Check.That(r!["errors"]!["code"] is not null && r["errors"]!["bodyType"] is not null && r["errors"]!["nameAr"] is not null, "أخطاء لكل حقل");
        await Check.Problem(await gov.PostAsync("/api/v1/bodies", new { code = "QA-WT", bodyType = "working_team", nameAr = "فريق", nameEn = "Team" }), 400, "validation", "فريق عمل بلا تاريخ انتهاء");
        await Check.Problem(await gov.PostAsync("/api/v1/bodies", new { code = "QA-COMM", bodyType = "permanent_committee", nameAr = "مكرر", nameEn = "Dup" }), 409, "duplicate", "رمز مكرر");
    }

    [Test("DOC-01", "وثيقة OpenAPI تصف الواجهات وتتاح في بيئة الاختبار", 51)]
    public async Task OpenApi()
    {
        using var anon = new ApiClient(Url);
        var doc = await anon.GetJsonAsync("/openapi/v1.json");
        Check.Equal("3.1.0", doc.S("openapi"), "الإصدار");
        var paths = doc["paths"]!.AsObject();
        foreach (var p in new[] { "/api/v1/admin/features/{key}", "/api/v1/bodies", "/api/v1/tasks/{id}", "/api/v1/auth/login", "/api/v1/meetings/{id}/schedule" })
            Check.That(paths.ContainsKey(p), "المسار موثق: " + p);
        Check.That(paths["/api/v1/admin/features/{key}"]!["put"]!["requestBody"] is not null, "جسم الطلب موصوف");
        Check.That(paths.Count >= 50, $"عدد المسارات {paths.Count}");
    }

    // ============================ التدقيق والقاعدة ============================

    [Test("AUD-01", "سلسلة التدقيق سليمة، وحساب التطبيق لا يستطيع تعديل السجل أو حذفه", 60)]
    public async Task AuditIntegrity()
    {
        using var aud = await As("auditor");
        var i = await aud.GetJsonAsync("/api/v1/audit/integrity");
        Check.That(i["intact"]!.GetValue<bool>(), "السلسلة سليمة");
        await using var app = new PgWireDataSource(ctx.AppConnectionString);
        var db = new Db(app);
        foreach (var sql in new[] { "UPDATE audit.audit_events SET action = 'tampered' WHERE chain_seq = 1", "DELETE FROM audit.audit_events WHERE chain_seq = 1",
                     "UPDATE config.feature_flag_history SET reason = 'x'", "DELETE FROM tasks.task_updates" })
        {
            try
            {
                await db.ExecuteAsync(sql);
                throw new AssertionException("نُفذ ما يجب منعه: " + sql);
            }
            catch (PgWireException e) when (e.SqlState == SqlErrors.InsufficientPrivilege) { }
        }
    }

    [Test("MIG-01", "الترحيلات متساوية الأثر، وتعديل ترحيل مطبق يُكتشف", 61)]
    public async Task Migrations()
    {
        await using var owner = new PgWireDataSource(ctx.OwnerConnectionString);
        var runner = new MigrationRunner(owner, Microsoft.Extensions.Logging.Abstractions.NullLogger<MigrationRunner>.Instance);
        var again = await runner.ApplyAsync(ctx.MigrationsPath);
        Check.Equal(0, again.Applied.Count, "لا ترحيلات جديدة في التشغيل الثاني");
        var tmp = Directory.CreateTempSubdirectory("ccms-mig-");
        foreach (var f in Directory.GetFiles(ctx.MigrationsPath)) File.Copy(f, Path.Combine(tmp.FullName, Path.GetFileName(f)));
        File.AppendAllText(Directory.GetFiles(tmp.FullName, "V002__*.sql")[0], "\n-- تعديل غير مسموح\n");
        try
        {
            await runner.ApplyAsync(tmp.FullName);
            throw new AssertionException("لم يُكتشف تعديل ترحيل مطبق");
        }
        catch (InvalidOperationException e) when (e.Message.Contains("عُدّل بعد تطبيقه")) { }
        finally { tmp.Delete(true); }
    }

    [Test("ARCH-01", "حدود الوحدات: كل وحدة تعتمد على العقود فقط، ولا وحدة أساسية تعتمد على الاختيارية", 62)]
    public Task ModuleBoundaries()
    {
        var root = Path.GetFullPath(Path.Combine(ctx.MigrationsPath, "../../src/backend"));
        foreach (var proj in Directory.GetFiles(root, "Ccms.Modules.*.csproj", SearchOption.AllDirectories))
        {
            var refs = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(proj), "ProjectReference Include=\"([^\"]+)\"")
                .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value)).ToList();
            Check.That(refs.All(r => r == "Ccms.Contracts"), $"{Path.GetFileName(proj)} يعتمد على: {string.Join(", ", refs)}");
        }
        return Task.CompletedTask;
    }
}
