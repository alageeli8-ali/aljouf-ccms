using System.Data.Common;
using System.Threading.RateLimiting;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Ccms.Host;
using Ccms.Modules.Ai;
using Ccms.Modules.Audit;
using Ccms.Modules.Bodies;
using Ccms.Modules.Configuration;
using Ccms.Modules.Documents;
using Ccms.Modules.Iam;
using Ccms.Modules.Integrations;
using Ccms.Modules.Meetings;
using Ccms.Modules.Minutes;
using Ccms.Modules.Notifications;
using Ccms.Modules.Org;
using Ccms.Modules.Reporting;
using Ccms.Modules.Resolutions;
using Ccms.Modules.Signatures;
using Ccms.Modules.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

// فحص صحة خفيف لحاوية الإنتاج (HEALTHCHECK) — لا يبني التطبيق ولا يتصل بقاعدة البيانات؛ صورة aspnet لا تحوي wget/curl.
if (args is ["healthcheck", ..])
{
    Environment.Exit(await HealthProbe.RunAsync(args.Length > 1 ? args[1] : "/health/live"));
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

// ---------------- الوحدات (03 §4) ----------------
IModule[] modules =
[
    new IamModule(), new OrgModule(), new ConfigurationModule(), new AuditModule(), new NotificationsModule(),
    new BodiesModule(), new MeetingsModule(), new MinutesModule(), new ResolutionsModule(), new TasksModule(),
    new DocumentsModule(), new ReportingModule(), new IntegrationsModule(), new SignaturesModule(), new AiModule(),
];
foreach (var m in modules) m.AddServices(builder.Services, builder.Configuration);

// ---------------- قاعدة البيانات ----------------
builder.Services.AddSingleton<DbDataSource>(_ => DataSources.Create(builder.Configuration.GetConnectionString("App")
    ?? throw new InvalidOperationException("ConnectionStrings:App مطلوب")));
builder.Services.AddSingleton(sp => new Db(sp.GetRequiredService<DbDataSource>()));

// ---------------- المنصة ----------------
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton<OpenApiDocumentBuilder>();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-Token";
    o.Cookie.Name = "ccms.csrf";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.HttpOnly = true;
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // حد عام لكل مستخدم (أو عنوان لغير المصادق) يحد من الإغراق وكشط البيانات، وحد أضيق للتصدير الثقيل
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        !http.Request.Path.StartsWithSegments("/api") ? RateLimitPartition.GetNoLimiter("static")
        : RateLimitPartition.GetFixedWindowLimiter(http.User.FindFirst("ccms:uid")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimits:ApiPerMinute", 600), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("export", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst("ccms:uid")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimits:ExportPerMinute", 30), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimits:LoginPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
});
// الترويسات الممررة تُقبل من الوكلاء الموثوقين فقط (Network:TrustedProxies / TrustedNetworks)؛ وإلا يستطيع أي عميل تزوير عنوانه
// فيتجاوز حد محاولات الدخول ويُفسد عنوان المنفذ في سجل التدقيق (24 §2). الافتراضي: الوكيل على الجهاز نفسه فقط.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    var proxies = builder.Configuration.GetSection("Network:TrustedProxies").Get<string[]>() ?? [];
    var networks = builder.Configuration.GetSection("Network:TrustedNetworks").Get<string[]>() ?? [];
    if (proxies.Length + networks.Length > 0) { o.KnownProxies.Clear(); o.KnownIPNetworks.Clear(); }
    foreach (var ip in proxies)
        o.KnownProxies.Add(System.Net.IPAddress.Parse(ip));
    foreach (var net in networks)
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(net));
});
builder.Services.AddHealthChecks();
// مفاتيح حماية البيانات (الكعكات ورموز CSRF) محفوظة خارج العملية لتبقى الجلسات صالحة بعد إعادة التشغيل وبين النسخ
var dp = builder.Services.AddDataProtection().SetApplicationName("ccms");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

var app = builder.Build();

// ---------------- أوامر التشغيل الإدارية ----------------
if (args.Length > 0 && !args[0].StartsWith('-'))
{
    Environment.ExitCode = await Cli.RunAsync(app, args);
    return;
}

Startup.EnforceProductionSafety(app);
if (app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", false))
    await Startup.MigrateAsync(app);

// ---------------- خط المعالجة ----------------
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapGet("/health/ready", async (Db db, CancellationToken ct) =>
    await db.ScalarAsync<int>("SELECT 1", null, ct) == 1 ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503)).AllowAnonymous().ExcludeFromDescription();

var openApiEnabled = app.Configuration.GetValue("OpenApi:Enabled", !app.Environment.IsProduction());
if (openApiEnabled)
    app.MapGet("/openapi/v1.json", (OpenApiDocumentBuilder doc) => Results.Text(doc.Build().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), "application/json"))
        .AllowAnonymous().ExcludeFromDescription();

SsoEndpoints.Map(app);
foreach (var m in modules) m.MapPublicEndpoints(app);

var api = app.MapGroup("/api/v1").RequireAuthorization();
api.AddEndpointFilter(async (ctx, next) =>
{
    // حماية CSRF لكل طلب يغيّر الحالة، حتى للمستخدم غير المصادق (مثل الدخول)
    var http = ctx.HttpContext;
    if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
        && !await http.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(http))
        return Results.Problem(statusCode: 400, title: "رمز الحماية من تزوير الطلبات مفقود أو غير صالح", type: "/problems/csrf",
            extensions: new Dictionary<string, object?> { ["code"] = "csrf", ["traceId"] = http.TraceIdentifier });
    return await next(ctx);
});
api.MapGet("/auth/csrf", (HttpContext http, IAntiforgery af) =>
{
    var tokens = af.GetAndStoreTokens(http);
    return Results.Ok(new { token = tokens.RequestToken, headerName = "X-CSRF-Token" });
}).AllowAnonymous().WithTags("Auth");
api.MapGet("/system/info", (IConfiguration cfg, IWebHostEnvironment env) => Results.Ok(new
{
    name = "Aljouf Councils & Committees Management System",
    version = cfg["App:Version"] ?? "0.1.0",
    environment = env.EnvironmentName,
    modules = modules.Select(m => new { m.Code, m.IsOptional }),
})).AllowAnonymous().WithTags("System");

foreach (var m in modules) m.MapEndpoints(api);

// الواجهة الأمامية (SPA): كل مسار غير /api يعود إلى index.html
app.MapFallback(async ctx =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/auth"))
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    var index = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (File.Exists(index))
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync(index);
    }
    else ctx.Response.StatusCode = 404;
}).AllowAnonymous();

app.Run();


internal static class DataSources
{
    public static DbDataSource Create(string connectionString)
    {
#if USE_NPGSQL
        return Npgsql.NpgsqlDataSource.Create(connectionString);
#else
        return new Ccms.Data.PgWire.PgWireDataSource(connectionString);
#endif
    }

#if USE_NPGSQL
    public const bool UsesOfflineShim = false;
#else
    public const bool UsesOfflineShim = true;
#endif
}

internal static class Startup
{
    /// <summary>يرفض الإقلاع في الإنتاج بإعدادات ضعيفة (NFR-SEC-12).</summary>
    public static void EnforceProductionSafety(WebApplication app)
    {
        // بيئة التجربة (Trial) تُطبق ضوابط الإنتاج كلها عدا ثلاثة استثناءات لازمة للتجربة ببيانات وهمية (25 §11):
        // الدخول المحلي لكل الحسابات (لا دخول موحد)، والمزوّدان التجريبيان للتوقيع والذكاء الاصطناعي، والبيانات التجريبية بكلمة مرور مولدة.
        var trial = app.Environment.IsEnvironment(TrialEnvironment.Name);
        if (!app.Environment.IsProduction() && !trial) return;
        var problems = new List<string>();
        var auth = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (DataSources.UsesOfflineShim) problems.Add("الإنتاج يتطلب مشغّل Npgsql: ابنِ بـ -p:UseNpgsql=true (ADR-015)");
        if (!trial && auth.LocalLogin.Enabled && !auth.LocalLogin.BreakGlassOnly) problems.Add("الدخول المحلي في الإنتاج لحساب الطوارئ فقط (Authentication:LocalLogin:BreakGlassOnly=true)");
        if (auth.Entra.AuthorityOverride.Length > 0) problems.Add("Authentication:Entra:AuthorityOverride مخصص للاختبار ولا يُقبل في الإنتاج");
        if (app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", false)) problems.Add("الترحيلات في الإنتاج تُشغّل بأمر migrate بحساب المالك لا عند الإقلاع");
        var cs = app.Configuration.GetConnectionString("App") ?? "";
        if (cs.Contains("Username=ccms_owner", StringComparison.OrdinalIgnoreCase)) problems.Add("حساب التطبيق في الإنتاج يجب أن يكون ccms_app لا ccms_owner");
        if (string.IsNullOrWhiteSpace(app.Configuration["DataProtection:KeysPath"])) problems.Add("DataProtection:KeysPath مطلوب في الإنتاج (مفاتيح الكعكات ورموز الحماية خارج العملية ومحمية بصلاحيات الملفات)");
        if ((app.Configuration["AllowedHosts"] ?? "*") == "*") problems.Add("AllowedHosts يجب أن يحدد اسم النطاق الرسمي لا «*» (منع هجمات ترويسة Host)");
        if (app.Configuration["Secrets:MasterKey"] is { Length: > 0 } mk && !IsKey32(mk)) problems.Add("Secrets:MasterKey يجب أن يكون 32 بايت بترميز Base64");
        if (!trial && !string.IsNullOrEmpty(app.Configuration["Signatures:MockCallbackSecret"])) problems.Add("Signatures:MockCallbackSecret خاص بالمزوّد التجريبي ولا يُضبط في الإنتاج");
        if (trial && app.Configuration["Signatures:MockCallbackSecret"] is { Length: > 0 and < 32 }) problems.Add("Signatures:MockCallbackSecret في بيئة التجربة يجب أن يكون عشوائيًا بطول 32 حرفًا على الأقل");
        var storage = app.Configuration["Storage:Path"] ?? "";
        if (!Path.IsPathRooted(storage)) problems.Add("Storage:Path يجب أن يكون مسارًا مطلقًا خارج مجلد التطبيق في الإنتاج");
        if (problems.Count > 0)
            throw new InvalidOperationException("إعدادات إنتاج غير آمنة:\n - " + string.Join("\n - ", problems));
        if (trial) app.Logger.LogWarning("بيئة تجربة: ضوابط الإنتاج مطبقة، والدخول المحلي والمزوّدان التجريبيان مسموحة — بيانات وهمية فقط");
    }

    private static bool IsKey32(string b64)
    {
        try { return Convert.FromBase64String(b64).Length == 32; } catch (FormatException) { return false; }
    }

    public static async Task MigrateAsync(WebApplication app)
    {
        var cs = app.Configuration.GetConnectionString("Migrations") ?? app.Configuration.GetConnectionString("App")!;
        await using var owner = DataSources.Create(cs);
        var runner = new MigrationRunner(owner, app.Services.GetRequiredService<ILogger<MigrationRunner>>());
        var dir = app.Configuration["Database:MigrationsPath"] is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "migrations");
        var result = await runner.ApplyAsync(dir);
        app.Logger.LogInformation("الترحيلات: طُبق {Applied}، سابقًا {Existing}", result.Applied.Count, result.AlreadyApplied.Count);
    }
}

/// <summary>يطلب نقطة الصحة محليًا بترويسة Host من AllowedHosts (لأن تصفية المضيف ترفض 127.0.0.1 في الإنتاج).</summary>
internal static class HealthProbe
{
    public static async Task<int> RunAsync(string path)
    {
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://127.0.0.1:8080";
        var port = new Uri(urls.Split(';')[0].Replace("0.0.0.0", "127.0.0.1").Replace("+", "127.0.0.1").Replace("*", "127.0.0.1")).Port;
        var host = (Environment.GetEnvironmentVariable("AllowedHosts") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(h => h != "*" && !h.StartsWith("*.", StringComparison.Ordinal));
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
            if (host is not null) req.Headers.Host = host;
            using var res = await http.SendAsync(req);
            Console.WriteLine($"{(int)res.StatusCode} {path}");
            return res.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"unhealthy: {e.GetType().Name}");
            return 1;
        }
    }
}

/// <summary>اسم بيئة التجربة المؤسسية على استضافة مجانية ببيانات وهمية (25 §11).</summary>
internal static class TrialEnvironment
{
    public const string Name = "Trial";
}
