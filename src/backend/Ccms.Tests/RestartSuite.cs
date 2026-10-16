using Ccms.BuildingBlocks.Data;
using Ccms.Data.PgWire;

namespace Ccms.Tests;

/// <summary>بعد إيقاف التطبيق وتشغيله من جديد بإعدادات مختلفة (مزوّد هوية وهمي مضبوط، وقفل بيئي على AI_ENABLED).</summary>
public sealed class RestartSuite(TestContext ctx)
{
    private string Url => ctx.App!.BaseUrl;

    [Test("RST-01", "الإعدادات محفوظة بعد إعادة تشغيل التطبيق", 1)]
    public async Task SettingsPersistAfterRestart()
    {
        var f = await ctx.AdminClient!.GetJsonAsync("/api/v1/admin/features");
        string V(string k) => f.AsArray().First(x => x.S("key") == k).S("storedValue");
        Check.Equal("true", V("AI_ENABLED"), "AI_ENABLED المخزنة");
        Check.Equal("true", V("AI_MINUTES_SUMMARY_ENABLED"), "AI_MINUTES_SUMMARY_ENABLED");
        Check.Equal("false", V("E_SIGNATURE_ENABLED"), "E_SIGNATURE_ENABLED");
        Check.Equal("mock", V("E_SIGNATURE_PROVIDER"), "E_SIGNATURE_PROVIDER");
        Check.Equal("false", V("EXTERNAL_PORTAL_ENABLED"), "EXTERNAL_PORTAL_ENABLED");
        var h = await ctx.AdminClient!.GetJsonAsync("/api/v1/admin/features/history");
        Check.Equal(ctx.HistoryCountBeforeRestart, h["total"]!.GetValue<long>(), "سجل التغييرات كامل بعد إعادة التشغيل");
    }

    [Test("RST-02", "الجلسة القائمة تبقى صالحة بعد إعادة التشغيل (جلسات في القاعدة ومفاتيح محفوظة)", 2)]
    public async Task SessionSurvivesRestart()
    {
        await Check.Status(await ctx.AdminClient!.GetAsync("/api/v1/me"), 200, "جلسة المدير");
        await ctx.AdminClient!.RefreshCsrfAsync();
    }

    [Test("RST-03", "قفل البيئة يتقدم على القيمة المخزنة ولا يُغيَّر من الواجهة", 3)]
    public async Task EnvironmentLock()
    {
        var f = await ctx.AdminClient!.GetJsonAsync("/api/v1/features");
        Check.Equal(false, f["AI_ENABLED"]!["enabled"]!.GetValue<bool>(), "مقفل معطلًا رغم أن المخزن true");
        Check.Equal(false, f["AI_MINUTES_SUMMARY_ENABLED"]!["enabled"]!.GetValue<bool>(), "الفرعي يتبع القفل");
        await Check.Problem(await ctx.AdminClient!.PutAsync("/api/v1/admin/features/AI_ENABLED", new { value = "false", reason = "محاولة تغيير مفتاح مقفل" }), 400, "validation", "تغيير المقفل");
        using var c = new ApiClient(Url);
        await c.LoginAsync("secretary@test.jsdo.gov.sa", "Test@Password2026");
        await Check.Problem(await c.GetAsync("/api/v1/ai/status"), 404, "feature-disabled", "الخادم يحترم القفل");
    }

    [Test("RST-04", "موصل Entra: الدخول الموحد الكامل مع مزوّد هوية وهمي (PKCE + RS256 + nonce)", 4)]
    public async Task SsoFullFlow()
    {
        await Check.Status(await ctx.AdminClient!.PutAsync("/api/v1/admin/features/SSO_ENABLED", new { value = "true", reason = "تجربة الموصل مع المزوّد الوهمي" }), 200, "تفعيل SSO");
        var check = await Check.Status(await ctx.AdminClient!.PostAsync("/api/v1/admin/connectors/sso/check"), 200, "فحص الموصل");
        Check.Equal("healthy", check.S("status"), "المزوّد الوهمي متصل");

        ctx.Idp!.NextUser = ("oid-new-1", "new.sso@jsdo.gov.sa", "موظف عبر الدخول الموحد");
        using var c = new ApiClient(Url);
        var flow = await c.GetAsync("/auth/sso/login?returnUrl=/dashboard");
        Check.That(flow.RequestMessage!.RequestUri!.AbsolutePath == "/dashboard", $"انتهى المسار بالعودة إلى الواجهة: {flow.RequestMessage.RequestUri} {(int)flow.StatusCode} {await flow.Content.ReadAsStringAsync()}");
        var me = await c.GetJsonAsync("/api/v1/me");
        Check.Equal("new.sso@jsdo.gov.sa", me["user"]!.S("email"), "مستخدم أُنشئ من رمز الهوية");
        Check.Equal(0, (await c.GetJsonAsync("/api/v1/bodies")).Items().Count, "المستخدم الجديد بلا صلاحيات حتى تُسند له");

        // نطاق بريد غير مسموح يُرفض حتى لو كان الحساب موجودًا
        ctx.Idp.NextUser = ("oid-secretary", "secretary@test.jsdo.gov.sa", "أمين السر");
        using var denied = new ApiClient(Url);
        await Check.Problem(await denied.GetAsync("/auth/sso/login"), 401, "unauthenticated", "نطاق بريد غير مسموح");

        // ربط حساب موجود بالبريد، ثم تثبيت معرّف Entra له، مع الاحتفاظ بأدواره
        var created = await Check.Status(await ctx.AdminClient!.PostAsync("/api/v1/users", new
        { email = "linked@jsdo.gov.sa", displayNameAr = "حساب مرتبط", systemRoles = new[] { "auditor" } }), 201, "إنشاء حساب للربط");
        ctx.Idp.NextUser = ("oid-linked", "linked@jsdo.gov.sa", "حساب مرتبط");
        using var s = new ApiClient(Url);
        var flow2 = await s.GetAsync("/auth/sso/login");
        Check.That((int)flow2.StatusCode is 200 or 404, $"مسار ربط الحساب: {(int)flow2.StatusCode} {await flow2.Content.ReadAsStringAsync()}");
        var me2 = await s.GetJsonAsync("/api/v1/me");
        Check.Equal(created.S("id"), me2["user"]!.S("id"), "الحساب نفسه لا حساب جديد");
        Check.That(me2["systemRoles"]!.AsArray().Any(r => r!.GetValue<string>() == "auditor"), "الأدوار محفوظة بعد الربط");
    }

    [Test("RST-05", "موصل Entra يرفض رمزًا بتوقيع مزوّر أو nonce غير مطابق", 5)]
    public async Task SsoRejectsForgedTokens()
    {
        ctx.Idp!.SignWithRogueKey = true;
        using var a = new ApiClient(Url);
        await Check.Problem(await a.GetAsync("/auth/sso/login"), 401, "unauthenticated", "توقيع مزوّر");
        await Check.Status(await a.GetAsync("/api/v1/me"), 401, "لا جلسة");
        ctx.Idp.SignWithRogueKey = false;
        ctx.Idp.TamperNonce = true;
        using var b = new ApiClient(Url);
        await Check.Problem(await b.GetAsync("/auth/sso/login"), 401, "unauthenticated", "nonce غير مطابق");
        ctx.Idp.TamperNonce = false;
    }

    [Test("RST-06", "أي عبث مباشر بسجل التدقيق (حتى بحساب المالك) يُكتشف", 6)]
    public async Task TamperDetected()
    {
        await using var owner = new PgWireDataSource(ctx.OwnerConnectionString);
        await new Db(owner).ExecuteAsync("UPDATE audit.audit_events SET action = 'tampered' WHERE chain_seq = 3");
        using var aud = new ApiClient(Url);
        await aud.LoginAsync("auditor@test.jsdo.gov.sa", "Test@Password2026");
        var i = await aud.GetJsonAsync("/api/v1/audit/integrity");
        Check.Equal(false, i["intact"]!.GetValue<bool>(), "اكتُشف العبث");
        Check.Equal(3L, i["firstBrokenSeq"]!.GetValue<long>(), "موضع العبث");
    }
}
