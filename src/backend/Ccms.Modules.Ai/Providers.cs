using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ccms.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ccms.Modules.Ai;

internal sealed record AiPromptRequest(string Function, string PromptVersion, string System, string User, JsonObject Payload, int MaxOutputTokens, TimeSpan Timeout);
internal sealed record AiCompletion(string Json, int? InputTokens, int? OutputTokens, bool UsageEstimated, string Model);
internal sealed record AiTranscript(string Text, string Model, int? InputTokens, bool UsageEstimated);
internal sealed record AiReadiness(bool Ready, string Reason, bool TestOnly);

/// <summary>فشل على مستوى المزوّد: unavailable | timeout | rate_limited (مؤقت — 503)، أو invalid_credentials | error | unsupported_audio (502).</summary>
internal sealed class AiProviderException(string kind, string detail) : Exception(detail)
{
    public string Kind { get; } = kind;
    public bool Transient => Kind is "unavailable" or "timeout" or "rate_limited";
}

/// <summary>واجهة موحدة لمزوّد الذكاء الاصطناعي (22 §5). منطق النظام لا يعرف المزوّد: يمرر التعليمات والبيانات المسموح بها ويتلقى JSON يُتحقق منه في الخادم.</summary>
internal interface IAiProvider
{
    string Code { get; }
    string NameAr { get; }
    string NameEn { get; }
    bool TestOnly { get; }
    Task<AiReadiness> ReadinessAsync(AiSettings s, bool transcription, CancellationToken ct);
    Task<ConnectorHealth> CheckAsync(AiSettings s, CancellationToken ct);
    Task<AiCompletion> CompleteAsync(AiSettings s, AiPromptRequest request, CancellationToken ct);
    Task<AiTranscript> TranscribeAsync(AiSettings s, byte[] audio, string fileName, string contentType, CancellationToken ct);
}

internal sealed class AiProviderRegistry(IEnumerable<IAiProvider> providers)
{
    public IAiProvider? Find(string code) => providers.FirstOrDefault(p => p.Code == code);

    public async Task<(IAiProvider? Provider, AiReadiness Readiness)> ResolveAsync(AiSettings s, bool transcription, CancellationToken ct)
    {
        if (s.Provider == "none") return (null, new AiReadiness(false, "not_selected", false));
        var p = Find(s.Provider);
        return p is null ? (null, new AiReadiness(false, "provider_unknown", false)) : (p, await p.ReadinessAsync(s, transcription, ct));
    }
}

/// <summary>خادم نموذج بواجهة متوافقة مع OpenAI (vLLM أو خادم داخلي أو مزوّد معتمد): /chat/completions و/audio/transcriptions و/models.
/// المفتاح من مخزن الأسرار فقط، والمضيف من القائمة المسموحة في بيئة الخادم فقط.</summary>
internal sealed class OpenAiCompatibleProvider(IHttpClientFactory http, ISecretStore secrets, IOptions<AiOptions> options, IHostEnvironment env) : IAiProvider
{
    public string Code => "openai_compatible";
    public string NameAr => "خادم نموذج بواجهة متوافقة";
    public string NameEn => "OpenAI-compatible model server";
    public bool TestOnly => false;

    public static string? EndpointProblem(string baseUrl, AiOptions o, bool production)
    {
        if (baseUrl.Length == 0) return "base_url_missing";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return "base_url_invalid";
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && o.AllowHttp && !production)) return "insecure_scheme";
        if (!o.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return "host_not_allowed";
        return null;
    }

    public async Task<AiReadiness> ReadinessAsync(AiSettings s, bool transcription, CancellationToken ct)
    {
        if (EndpointProblem(s.BaseUrl, options.Value, env.IsProduction()) is { } p) return new(false, p, false);
        if ((transcription ? s.TranscriptionModel : s.Model).Length == 0) return new(false, transcription ? "transcription_not_available" : "model_missing", false);
        if (string.IsNullOrEmpty(await secrets.GetAsync(SecretNames.AiApiKey, ct))) return new(false, "missing_api_key", false);
        return new(true, "ready", false);
    }

    private async Task<HttpRequestMessage> Request(HttpMethod method, AiSettings s, string path, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, s.BaseUrl.TrimEnd('/') + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await secrets.GetAsync(SecretNames.AiApiKey, ct));
        return req;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage req, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var resp = await client.SendAsync(req, cts.Token);
            if (resp.IsSuccessStatusCode) return resp;
            var code = (int)resp.StatusCode;
            resp.Dispose();
            throw resp.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AiProviderException("invalid_credentials", $"رفض المزوّد بيانات الاعتماد (HTTP {code})"),
                HttpStatusCode.TooManyRequests => new AiProviderException("rate_limited", "المزوّد يحد الطلبات حاليًا (HTTP 429)"),
                _ when code >= 500 => new AiProviderException("unavailable", $"المزوّد غير متاح (HTTP {code})"),
                _ => new AiProviderException("error", $"رفض المزوّد الطلب (HTTP {code})"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiProviderException("timeout", $"تجاوز المزوّد المهلة ({timeout.TotalSeconds:0} ثانية)");
        }
        catch (HttpRequestException e)
        {
            throw new AiProviderException("unavailable", "تعذر الاتصال بالمزوّد: " + (e.HttpRequestError == HttpRequestError.Unknown ? e.Message : e.HttpRequestError.ToString()));
        }
    }

    public async Task<ConnectorHealth> CheckAsync(AiSettings s, CancellationToken ct)
    {
        var ready = await ReadinessAsync(s, false, ct);
        if (!ready.Ready) return new ConnectorHealth(ready.Reason is "host_not_allowed" or "insecure_scheme" ? ConnectorState.Failing : ConnectorState.NotConfigured, Describe(ready.Reason));
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = await Request(HttpMethod.Get, s, "/models", ct);
            using var resp = await SendAsync(http.CreateClient("ai"), req, TimeSpan.FromSeconds(Math.Min(10, s.TimeoutSeconds)), ct);
            return new ConnectorHealth(ConnectorState.Healthy, $"{new Uri(s.BaseUrl).Host} — {s.Model}", (int)sw.ElapsedMilliseconds);
        }
        catch (AiProviderException e)
        {
            return new ConnectorHealth(ConnectorState.Failing, e.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    public async Task<AiCompletion> CompleteAsync(AiSettings s, AiPromptRequest r, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = s.Model,
            ["temperature"] = 0.1,
            ["max_tokens"] = r.MaxOutputTokens,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = r.System },
                new JsonObject { ["role"] = "user", ["content"] = r.User }),
        };
        using var req = await Request(HttpMethod.Post, s, "/chat/completions", ct);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await SendAsync(http.CreateClient("ai"), req, r.Timeout, ct);
        JsonNode? json;
        try { json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)); }
        catch (JsonException) { throw new AiProviderException("error", "رد المزوّد ليس JSON"); }
        var content = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            ?? throw new AiProviderException("error", "رد المزوّد لا يتضمن نصًا");
        var usage = json?["usage"];
        return new AiCompletion(content, usage?["prompt_tokens"]?.GetValue<int>(), usage?["completion_tokens"]?.GetValue<int>(), usage is null,
            json?["model"]?.GetValue<string>() ?? s.Model);
    }

    public async Task<AiTranscript> TranscribeAsync(AiSettings s, byte[] audio, string fileName, string contentType, CancellationToken ct)
    {
        using var req = await Request(HttpMethod.Post, s, "/audio/transcriptions", ct);
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(s.TranscriptionModel), "model");
        form.Add(new StringContent("ar"), "language");
        form.Add(new StringContent("json"), "response_format");
        req.Content = form;
        using var resp = await SendAsync(http.CreateClient("ai"), req, TimeSpan.FromSeconds(Math.Max(s.TimeoutSeconds, 120)), ct);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        var text = json?["text"]?.GetValue<string>() ?? throw new AiProviderException("error", "رد خدمة التفريغ لا يتضمن نصًا");
        return new AiTranscript(text, s.TranscriptionModel, null, true);
    }

    public static string Describe(string reason) => reason switch
    {
        "ready" => "جاهز",
        "not_selected" => "لم يُختر مزوّد (AI_PROVIDER = none)",
        "provider_unknown" => "مزوّد غير معروف",
        "base_url_missing" => "لم يُحدد عنوان خادم النموذج (AI_BASE_URL)",
        "base_url_invalid" => "عنوان خادم النموذج غير صالح",
        "insecure_scheme" => "عنوان المزوّد يجب أن يكون https",
        "host_not_allowed" => "مضيف المزوّد غير مدرج في القائمة المسموحة ببيئة الخادم (Ai:AllowedHosts)",
        "model_missing" => "لم يُحدد النموذج (AI_MODEL)",
        "missing_api_key" => "مفتاح API غير موجود في مخزن الأسرار (AI_API_KEY)",
        "transcription_not_available" => "خدمة التفريغ الصوتي غير متاحة (AI_TRANSCRIPTION_MODEL فارغ)",
        "mock_blocked_in_production" => "المزوّد التجريبي ممنوع في بيئة الإنتاج",
        _ => reason,
    };
}
