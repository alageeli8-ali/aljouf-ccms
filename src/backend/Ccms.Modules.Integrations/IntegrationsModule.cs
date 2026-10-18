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
    public EmailOptions Email { get; set; } = new();
    public ESignOptions ESign { get; set; } = new();
    public CorrespondenceOptions Correspondence { get; set; } = new();
    public GraphOptions Graph { get; set; } = new();
    public DirectoryOptions Directory { get; set; } = new();
    public VideoMeetingsOptions VideoMeetings { get; set; } = new();
    public DocumentRepositoryOptions DocumentRepository { get; set; } = new();
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
    /// <summary>عقد الواجهة المعتمد. القيمة الوحيدة المنفذة: ccms-letters-v1 (عقد مقترح موثق في 23 §4.3 يُطابق مع مورد نظام المراسلات). فارغ = لا إرسال آلي.</summary>
    public string Contract { get; set; } = "";
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
        services.AddScoped<IConnector, ESignConnector>();
        services.AddScoped<IConnector, EmailConnector>();
        services.AddScoped<IConnector, CorrespondenceConnector>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddScoped<ICorrespondenceGateway, CorrespondenceGateway>();
        // موصلات Microsoft Graph (23 §4)
        services.AddSingleton<GraphTokenCache>();
        services.AddScoped<GraphClient>();
        services.AddScoped<IConnector, DirectoryConnector>();
        services.AddScoped<IConnector, VideoMeetingsConnector>();
        services.AddScoped<IConnector, DocumentRepositoryConnector>();
        services.AddScoped<IDirectoryGateway, DirectoryGateway>();
        services.AddScoped<IVideoMeetingGateway, VideoMeetingGateway>();
        services.AddScoped<IDocumentRepositoryGateway, DocumentRepositoryGateway>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        api.MapGet("/correspondence/status", CorrespondenceStatus).WithTags("Correspondence (optional)");
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
            "mock" when env.IsProduction() => new ConnectorHealth(ConnectorState.Failing, "المزوّد التجريبي mock ممنوع في بيئة الإنتاج"),
            "mock" => new ConnectorHealth(ConnectorState.Healthy, "مزوّد تجريبي للاختبار فقط — ليس خدمة توقيع إنتاجية"),
            "national_provider" when options.Value.ESign.NationalProviderBaseUrl.Length == 0 =>
                new ConnectorHealth(ConnectorState.NotConfigured, "لم يُتعاقد مع مزوّد توقيع معتمد بعد (Q-09)"),
            "national_provider" => await Probe.HttpAsync(http, options.Value.ESign.NationalProviderBaseUrl, null, ct),
            _ => new ConnectorHealth(ConnectorState.NotConfigured, "لم يُختر مزوّد توقيع (E_SIGNATURE_PROVIDER = none)"),
        };
    }

    public async Task<ConnectorDescriptor> DescribeAsync(CancellationToken ct = default)
    {
        var provider = await features.GetValueAsync(FeatureKeys.ESignatureProvider, ct);
        return new(Code, NameAr, NameEn, FeatureKeys.ESignatureEnabled, "provider_specific",
        [
            new("E_SIGNATURE_PROVIDER", "اختيار المزوّد من الإعدادات (none/mock/national_provider)", "Provider selection in settings", provider is "mock" or "national_provider"),
            new("Integrations:ESign:NationalProviderBaseUrl", "عنوان واجهة مزوّد التوقيع المعتمد (Q-09)", "Approved e-sign provider API URL (Q-09)", options.Value.ESign.NationalProviderBaseUrl.Length > 0),
            new("Signatures:MockCallbackSecret", "سر توقيع الاستدعاءات العكسية للمزوّد", "Provider callback signing secret", true, true),
        ],
        "تعطيل المزوّد يوقف الطلبات الجديدة فقط؛ التوقيعات المكتملة وأدلتها محفوظة ولا تُحذف ويُتحقق منها دون المزوّد.",
        "Disabling the provider only stops new requests; completed signatures and their evidence are kept and verified without the provider.");
    }
}

internal sealed class EmailConnector(IOptions<IntegrationOptions> options, ISecretStore secrets) : IConnector
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

    public Task<ConnectorDescriptor> DescribeAsync(CancellationToken ct = default) => DescribeCoreAsync(ct);

    private async Task<ConnectorDescriptor> DescribeCoreAsync(CancellationToken ct)
    {
        var o = options.Value.Email;
        var pw = await secrets.InfoAsync(SecretNames.SmtpPassword, ct);
        return new(Code, NameAr, NameEn, FeatureKeys.EmailIntegrationEnabled, o.Username.Length > 0 ? "smtp_auth" : "smtp_relay",
        [
            new("Integrations:Email:SmtpHost", "خادم SMTP المؤسسي ومنفذه", "Corporate SMTP host and port", o.SmtpHost.Length > 0),
            new("Integrations:Email:From", "عنوان المرسل المعتمد", "Approved sender address", o.From.Length > 0),
            new("Integrations:Email:Username", "حساب الإرسال (اختياري عند الترحيل الداخلي)", "Sending account (optional for internal relay)", o.Username.Length > 0),
            new(SecretNames.SmtpPassword, "كلمة مرور حساب الإرسال (مخزن الأسرار)", "Sending account password (secret store)", pw.Source != "none" || o.Password.Length > 0, true),
        ],
        "الإشعارات داخل النظام تعمل دائمًا؛ البريد قناة إضافية عبر صندوق صادر بإعادة محاولة متباعدة. تعطيله يسجل الرسائل دون إرسال ولا يوقف أي عملية.",
        "In-app notifications always work; email is an extra channel through an outbox with backoff. Disabling it logs messages unsent and stops nothing.");
    }
}

internal sealed class CorrespondenceConnector(IOptions<IntegrationOptions> options, IHttpClientFactory http, ISecretStore secrets) : IConnector
{
    public string Code => "correspondence";
    public string NameAr => "نظام المراسلات (قمم)";
    public string NameEn => "Correspondence system (Qemam)";

    public async Task<ConnectorHealth> CheckAsync(CancellationToken ct = default)
    {
        var o = options.Value.Correspondence;
        if (o.BaseUrl.Length == 0) return new ConnectorHealth(ConnectorState.NotConfigured, "واجهة نظام المراسلات غير موثقة بعد (Q-07)");
        if (o.Contract != CorrespondenceGateway.ContractV1) return await Probe.HttpAsync(http, o.BaseUrl, null, ct);
        var key = await secrets.GetAsync(SecretNames.CorrespondenceApiKey, ct);
        if (string.IsNullOrEmpty(key)) return new ConnectorHealth(ConnectorState.NotConfigured, "مفتاح الواجهة CORRESPONDENCE_API_KEY غير مضبوط");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, o.BaseUrl.TrimEnd('/') + "/health");
            req.Headers.Add("X-Api-Key", key);
            using var resp = await http.CreateClient("integrations").SendAsync(req, ct);
            return resp.IsSuccessStatusCode ? new ConnectorHealth(ConnectorState.Healthy, $"{CorrespondenceGateway.ContractV1}: HTTP {(int)resp.StatusCode}", (int)sw.ElapsedMilliseconds)
                : new ConnectorHealth(ConnectorState.Failing, $"HTTP {(int)resp.StatusCode}", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new ConnectorHealth(ConnectorState.Failing, "تعذر الاتصال: " + e.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    public async Task<ConnectorDescriptor> DescribeAsync(CancellationToken ct = default)
    {
        var o = options.Value.Correspondence;
        var key = await secrets.InfoAsync(SecretNames.CorrespondenceApiKey, ct);
        return new(Code, NameAr, NameEn, FeatureKeys.CorrespondenceIntegrationEnabled, "api_key",
        [
            new("Integrations:Correspondence:BaseUrl", "عنوان واجهة نظام المراسلات", "Correspondence system API URL", o.BaseUrl.Length > 0),
            new("Integrations:Correspondence:Contract", "عقد الواجهة المعتمد (ccms-letters-v1)", "Approved API contract (ccms-letters-v1)", o.Contract == CorrespondenceGateway.ContractV1),
            new(SecretNames.CorrespondenceApiKey, "مفتاح الواجهة (مخزن الأسرار)", "API key (secret store)", key.Source != "none", true),
        ],
        "واجهة نظام المراسلات لدى المكتب غير موثقة (Q-07). نُفذ محوّل لعقد مقترح ccms-letters-v1 بمفتاح عدم تكرار؛ لا يُعد جاهزًا إلا بعد اعتماد العقد مع المورد وفحص ناجح. حتى ذلك: الإرسال الرسمي يدوي ويُسجل رقم الصادر.",
        "The office's correspondence API is undocumented (Q-07). An adapter for a proposed ccms-letters-v1 contract with an idempotency key is implemented; it is not ready until the vendor confirms the contract and a test passes. Until then dispatch is manual with the outgoing number recorded.");
    }
}

/// <summary>إرسال البريد عبر SMTP. كل محاولة تُسجل في سجل الموصلات (دون المستلم أو المحتوى). إعادة المحاولة يتولاها صندوق الصادر بتباعد متزايد
/// لأن SMTP لا يضمن عدم التكرار، فالموصل نفسه محاولة واحدة لكل دورة.</summary>
internal sealed class SmtpEmailSender(IOptions<IntegrationOptions> options, ISecretStore secrets, IConnectorRuntime runtime) : IEmailSender
{
    public string ConnectorCode => "email";

    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default) =>
        runtime.ExecuteAsync("email", "send", FeatureKeys.EmailIntegrationEnabled, new ConnectorCallPolicy(Idempotent: false, TimeoutSeconds: 30), async t =>
        {
            var o = options.Value.Email;
            var password = await secrets.GetAsync(SecretNames.SmtpPassword, t) ?? o.Password;
            using var client = new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = o.UseSsl };
            if (o.Username.Length > 0) client.Credentials = new System.Net.NetworkCredential(o.Username, password);
            using var msg = new MailMessage(o.From, to, subject, htmlBody) { IsBodyHtml = true, BodyEncoding = System.Text.Encoding.UTF8, SubjectEncoding = System.Text.Encoding.UTF8 };
            try { await client.SendMailAsync(msg, t); }
            catch (SmtpException e) when (e.StatusCode is SmtpStatusCode.MailboxBusy or SmtpStatusCode.ServiceNotAvailable or SmtpStatusCode.LocalErrorInProcessing
                                          or SmtpStatusCode.InsufficientStorage or SmtpStatusCode.TransactionFailed or SmtpStatusCode.GeneralFailure)
            {
                throw new ConnectorCallException("unavailable", $"SMTP {(int)e.StatusCode}", (int)e.StatusCode, transient: true);
            }
            catch (SmtpException e)
            {
                throw new ConnectorCallException(e.StatusCode is SmtpStatusCode.ClientNotPermitted or SmtpStatusCode.MustIssueStartTlsFirst ? "denied" : "rejected",
                    $"SMTP {(int)e.StatusCode}", (int)e.StatusCode);
            }
            return true;
        }, ct);
}

/// <summary>بوابة نظام المراسلات: جاهزة فقط بتفعيل الخاصية، وموصل سليم بفحص فعلي، وعقد واجهة مضبوط صراحة (ccms-letters-v1).
/// دون العقد لا يوجد محوّل إرسال، ولا يُدّعى إرسال أي خطاب.</summary>
internal sealed class CorrespondenceGateway(IFeatureService features, IConnectorStatusStore status, IOptions<IntegrationOptions> options, IHttpClientFactory http,
    ISecretStore secrets, IConnectorRuntime runtime) : ICorrespondenceGateway
{
    public const string ContractV1 = "ccms-letters-v1";

    public async Task<(bool Ready, string Reason)> ReadinessAsync(CancellationToken ct = default)
    {
        if (!await features.IsEnabledAsync(FeatureKeys.CorrespondenceIntegrationEnabled, ct)) return (false, "integration_disabled");
        var health = await status.GetAsync("correspondence", ct);
        if (health is null || health.State == ConnectorState.NotConfigured) return (false, "connector_not_configured");
        if (health.State != ConnectorState.Healthy) return (false, "connector_unavailable");
        if (options.Value.Correspondence.Contract != ContractV1) return (false, "send_api_not_documented");
        return (true, "ok");
    }

    /// <summary>POST {BaseUrl}/letters بمفتاح عدم تكرار = معرّف الخطاب، فإعادة المحاولة لا تُنشئ صادرًا مكررًا (العقد يُلزم المزوّد بإرجاع الصادر نفسه).</summary>
    public Task<string> SubmitAsync(CorrespondenceLetter letter, CancellationToken ct = default) =>
        runtime.ExecuteAsync("correspondence", "submit_letter", FeatureKeys.CorrespondenceIntegrationEnabled, new ConnectorCallPolicy(Idempotent: true, TimeoutSeconds: 30), async t =>
        {
            var o = options.Value.Correspondence;
            if (o.Contract != ContractV1 || o.BaseUrl.Length == 0) throw new ConnectorCallException("not_configured", "عقد واجهة نظام المراسلات غير مضبوط");
            var key = await secrets.GetAsync(SecretNames.CorrespondenceApiKey, t) ?? throw new ConnectorCallException("not_configured", "مفتاح الواجهة غير مضبوط");
            using var req = new HttpRequestMessage(HttpMethod.Post, o.BaseUrl.TrimEnd('/') + "/letters")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new
                {
                    externalId = letter.LetterId, referenceNo = letter.ReferenceNo, recipientName = letter.RecipientName, subject = letter.Subject, body = letter.BodyText,
                    pdfBase64 = Convert.ToBase64String(letter.Pdf),
                }),
            };
            req.Headers.Add("X-Api-Key", key);
            req.Headers.Add("Idempotency-Key", letter.LetterId.ToString());
            using var resp = await http.CreateClient("integrations").SendAsync(req, t);
            var code = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
                throw code switch
                {
                    401 or 403 => new ConnectorCallException("denied", $"رفض نظام المراسلات المفتاح ({code})", code),
                    408 or 429 or >= 500 => new ConnectorCallException("unavailable", $"نظام المراسلات غير متاح ({code})", code, transient: true),
                    _ => new ConnectorCallException("rejected", $"رفض نظام المراسلات الخطاب ({code})", code),
                };
            var json = System.Text.Json.Nodes.JsonNode.Parse(await resp.Content.ReadAsStringAsync(t));
            var outgoing = json?["outgoingNo"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(outgoing) || outgoing.Length > 60) throw new ConnectorCallException("rejected", "الاستجابة بلا رقم صادر صالح");
            return outgoing;
        }, ct);
}
