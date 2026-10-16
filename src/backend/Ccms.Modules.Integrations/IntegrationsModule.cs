using System.Diagnostics;
using System.Net.Mail;
using System.Net.Sockets;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ccms.Modules.Integrations;

public sealed class IntegrationOptions
{
    public const string Section = "Integrations";
    public AiOptions Ai { get; set; } = new();
    public EmailOptions Email { get; set; } = new();
    public ESignOptions ESign { get; set; } = new();
    public CorrespondenceOptions Correspondence { get; set; } = new();
}

public sealed class AiOptions
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>لا يُرسل أي نص إلا إلى مضيف في هذه القائمة (10 §6)؛ تُقفل من بيئة الخادم في الإنتاج.</summary>
    public string[] AllowedHosts { get; set; } = [];
}

public sealed class EmailOptions
{
    public string Provider { get; set; } = "none"; // none | smtp
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string From { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class ESignOptions
{
    public string NationalProviderBaseUrl { get; set; } = "";
}

public sealed class CorrespondenceOptions
{
    public string BaseUrl { get; set; } = "";
}

/// <summary>وحدة التكاملات الاختيارية (ADR-005): الموصلات ونقاط النهاية المحكومة بالمفاتيح.
/// حالة كل موصل تُفحص فعليًا ولا تُفترض؛ لا يوجد تكامل «جاهز» دون إعداداته وفحص ناجح.</summary>
public sealed class IntegrationsModule : IModule
{
    public string Code => "INT";
    public bool IsOptional => true;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IntegrationOptions>(configuration.GetSection(IntegrationOptions.Section));
        services.AddHttpClient("integrations", c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IConnector, AiConnector>();
        services.AddScoped<IConnector, ESignConnector>();
        services.AddSingleton<IConnector, EmailConnector>();
        services.AddSingleton<IConnector, CorrespondenceConnector>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddScoped<ICorrespondenceGateway, CorrespondenceGateway>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var ai = api.MapGroup("/ai").WithTags("AI (optional)");
        ai.MapGet("/status", AiStatus);
        ai.MapPost("/minutes/{id:guid}/summary", AiSummary);

        api.MapGet("/esign/status", ESignStatus).WithTags("E-signature (optional)");
        api.MapGet("/correspondence/status", CorrespondenceStatus).WithTags("Correspondence (optional)");
    }

    private static readonly string[] AiCapabilities =
    [
        FeatureKeys.AiMinutesSummary, FeatureKeys.AiDecisionExtraction, FeatureKeys.AiTaskExtraction,
        FeatureKeys.AiSmartSearch, FeatureKeys.AiAudioTranscription,
    ];

    private static async Task<IResult> AiStatus(IFeatureService features, IConnectorStatusStore status, ICurrentUser me, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        await features.DemandEnabledAsync(FeatureKeys.AiEnabled, ct);
        var caps = new Dictionary<string, bool>();
        foreach (var k in AiCapabilities) caps[k] = await features.IsEnabledAsync(k, ct);
        return Results.Ok(new { enabled = true, capabilities = caps, connection = (await status.GetAsync("ai", ct))?.StateCode ?? "not_configured" });
    }

    /// <summary>مثال للتحقق الطبقي على الخادم: الخاصية (404 إن كانت معطلة) ← الصلاحية ← المزوّد (503 إن لم يكن متاحًا).
    /// تنفيذ التلخيص نفسه في المرحلة 7ب بعد توفر خادم النموذج.</summary>
    private static async Task<IResult> AiSummary(Guid id, IFeatureService features, IAccessService access, IConnectorStatusStore status, CancellationToken ct)
    {
        await features.DemandEnabledAsync(FeatureKeys.AiMinutesSummary, ct);
        await access.DemandAsync(Permissions.MinutesView, Scope.Record("minutes", id), ct);
        await status.DemandAvailableAsync("ai", ct);
        return Results.Problem(statusCode: 501, title: "قدرة التلخيص تُنفذ في المرحلة 7ب بعد توفر خادم النموذج", type: "/problems/capability-not-implemented");
    }

    private static async Task<IResult> ESignStatus(IFeatureService features, IConnectorStatusStore status, ICurrentUser me, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        await features.DemandEnabledAsync(FeatureKeys.ESignatureEnabled, ct);
        return Results.Ok(new
        {
            provider = await features.GetValueAsync(FeatureKeys.ESignatureProvider, ct),
            method = await features.GetValueAsync(FeatureKeys.ESignatureMethod, ct),
            memberRequired = await features.IsEnabledAsync(FeatureKeys.ESignatureMemberRequired, ct),
            chairRequired = await features.IsEnabledAsync(FeatureKeys.ESignatureChairRequired, ct),
            secretaryRequired = await features.IsEnabledAsync(FeatureKeys.ESignatureSecretaryRequired, ct),
            sequential = await features.IsEnabledAsync(FeatureKeys.ESignatureSequential, ct),
            pdfOutput = await features.IsEnabledAsync(FeatureKeys.ESignaturePdfOutput, ct),
            reauthRequired = await features.IsEnabledAsync(FeatureKeys.ESignatureReauthRequired, ct),
            connection = (await status.GetAsync("esign", ct))?.StateCode ?? "not_configured",
        });
    }

    private static async Task<IResult> CorrespondenceStatus(IFeatureService features, IConnectorStatusStore status, ICurrentUser me, CancellationToken ct)
    {
        if (!me.IsAuthenticated) throw new UnauthenticatedException();
        await features.DemandEnabledAsync(FeatureKeys.CorrespondenceIntegrationEnabled, ct);
        return Results.Ok(new { connection = (await status.GetAsync("correspondence", ct))?.StateCode ?? "not_configured" });
    }
}

internal static class Probe
{
    public static async Task<ConnectorHealth> HttpAsync(IHttpClientFactory http, string url, string? bearer, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(bearer)) req.Headers.Authorization = new("Bearer", bearer);
            using var resp = await http.CreateClient("integrations").SendAsync(req, ct);
            return resp.IsSuccessStatusCode
                ? new ConnectorHealth(ConnectorState.Healthy, $"HTTP {(int)resp.StatusCode}", (int)sw.ElapsedMilliseconds)
                : new ConnectorHealth(ConnectorState.Failing, $"HTTP {(int)resp.StatusCode}", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new ConnectorHealth(ConnectorState.Failing, e.Message, (int)sw.ElapsedMilliseconds);
        }
    }
}

internal sealed class AiConnector(IOptions<IntegrationOptions> options, IHttpClientFactory http) : IConnector
{
    public string Code => "ai";
    public string NameAr => "خادم النموذج اللغوي";
    public string NameEn => "Language model server";

    public Task<ConnectorHealth> CheckAsync(CancellationToken ct = default)
    {
        var o = options.Value.Ai;
        if (o.BaseUrl.Length == 0 || o.Model.Length == 0)
            return Task.FromResult(new ConnectorHealth(ConnectorState.NotConfigured, "لم يُحدد خادم النموذج ولا النموذج (Q-16)"));
        if (!Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var uri) || !o.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            return Task.FromResult(new ConnectorHealth(ConnectorState.Failing, "مضيف النموذج غير مدرج في القائمة المسموح بها (Integrations:Ai:AllowedHosts)"));
        return Probe.HttpAsync(http, o.BaseUrl.TrimEnd('/') + "/models", o.ApiKey, ct);
    }
}

internal sealed class ESignConnector(IOptions<IntegrationOptions> options, IHttpClientFactory http, IFeatureService features, IHostEnvironment env) : IConnector
{
    public string Code => "esign";
    public string NameAr => "مزوّد التوقيع الإلكتروني";
    public string NameEn => "E-signature provider";

    public async Task<ConnectorHealth> CheckAsync(CancellationToken ct = default)
    {
        var provider = await features.GetValueAsync(FeatureKeys.ESignatureProvider, ct);
        return provider switch
        {
            "sandbox" when env.IsProduction() => new ConnectorHealth(ConnectorState.Failing, "المحاكي sandbox ممنوع في بيئة الإنتاج"),
            "sandbox" => new ConnectorHealth(ConnectorState.Healthy, "محاكي داخلي للاختبار فقط — ليس توقيعًا ذا حجية"),
            "national_provider" when options.Value.ESign.NationalProviderBaseUrl.Length == 0 =>
                new ConnectorHealth(ConnectorState.NotConfigured, "لم يُتعاقد مع مزوّد توقيع معتمد بعد (Q-09)"),
            "national_provider" => await Probe.HttpAsync(http, options.Value.ESign.NationalProviderBaseUrl, null, ct),
            _ => new ConnectorHealth(ConnectorState.NotConfigured, "لم يُختر مزوّد توقيع (E_SIGNATURE_PROVIDER = none)"),
        };
    }
}

internal sealed class EmailConnector(IOptions<IntegrationOptions> options) : IConnector
{
    public string Code => "email";
    public string NameAr => "خادم البريد";
    public string NameEn => "Mail server";

    public async Task<ConnectorHealth> CheckAsync(CancellationToken ct = default)
    {
        var o = options.Value.Email;
        if (o.Provider != "smtp" || o.SmtpHost.Length == 0 || o.From.Length == 0)
            return new ConnectorHealth(ConnectorState.NotConfigured, "لم يُضبط خادم البريد (Integrations:Email) — Q-04");
        var sw = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(o.SmtpHost, o.SmtpPort, timeout.Token);
            return new ConnectorHealth(ConnectorState.Healthy, $"{o.SmtpHost}:{o.SmtpPort}", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            return new ConnectorHealth(ConnectorState.Failing, $"تعذر الاتصال بـ {o.SmtpHost}:{o.SmtpPort}", (int)sw.ElapsedMilliseconds);
        }
    }
}

internal sealed class CorrespondenceConnector(IOptions<IntegrationOptions> options, IHttpClientFactory http) : IConnector
{
    public string Code => "correspondence";
    public string NameAr => "نظام المراسلات (قمم)";
    public string NameEn => "Correspondence system (Qemam)";

    public Task<ConnectorHealth> CheckAsync(CancellationToken ct = default) =>
        options.Value.Correspondence.BaseUrl.Length == 0
            ? Task.FromResult(new ConnectorHealth(ConnectorState.NotConfigured, "واجهة نظام المراسلات غير موثقة بعد (Q-07)"))
            : Probe.HttpAsync(http, options.Value.Correspondence.BaseUrl, null, ct);
}

internal sealed class SmtpEmailSender(IOptions<IntegrationOptions> options) : IEmailSender
{
    public string ConnectorCode => "email";

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var o = options.Value.Email;
        using var client = new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = o.UseSsl };
        if (o.Username.Length > 0) client.Credentials = new System.Net.NetworkCredential(o.Username, o.Password);
        using var msg = new MailMessage(o.From, to, subject, htmlBody) { IsBodyHtml = true, BodyEncoding = System.Text.Encoding.UTF8, SubjectEncoding = System.Text.Encoding.UTF8 };
        await client.SendMailAsync(msg, ct);
    }
}

/// <summary>بوابة نظام المراسلات: جاهزة فقط بتفعيل الخاصية، وموصل سليم، ومحوّل إرسال مطابق لواجهة النظام المعتمدة.
/// واجهة الإرسال في نظام المراسلات لم تُوثق بعد (Q-07)، فلا يوجد محوّل إرسال، ولا يُدّعى إرسال أي خطاب.</summary>
internal sealed class CorrespondenceGateway(IFeatureService features, IConnectorStatusStore status) : ICorrespondenceGateway
{
    public async Task<(bool Ready, string Reason)> ReadinessAsync(CancellationToken ct = default)
    {
        if (!await features.IsEnabledAsync(FeatureKeys.CorrespondenceIntegrationEnabled, ct)) return (false, "integration_disabled");
        var health = await status.GetAsync("correspondence", ct);
        if (health is null || health.State == ConnectorState.NotConfigured) return (false, "connector_not_configured");
        if (health.State != ConnectorState.Healthy) return (false, "connector_unavailable");
        return (false, "send_api_not_documented");
    }

    public Task<string> SubmitAsync(CorrespondenceLetter letter, CancellationToken ct = default) =>
        throw new NotSupportedException("محوّل الإرسال إلى نظام المراسلات غير منفذ حتى توثيق واجهته واعتمادها");
}
