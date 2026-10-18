using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ccms.Tests;

/// <summary>خادم وهمي بواجهة متوافقة مع OpenAI داخل الاختبارات فقط، لاختبار موصل openai_compatible طرفًا لطرف (المصادقة، والمهلة، والأخطاء، والاستهلاك).
/// يعيد مخرجًا يتضمن عمدًا مرجعًا مختلقًا (S99) ليُثبت أن الخادم يُسقطه مع المزوّد الحقيقي أيضًا.</summary>
public sealed class FakeOpenAiServer : IAsyncDisposable
{
    private WebApplication? _app;
    public string BaseUrl { get; private set; } = "";
    public string ExpectedKey { get; set; } = "sk-test-fake-0123456789abcdef";
    /// <summary>ok | error500 | malformed | slow</summary>
    public string Mode { get; set; } = "ok";
    public int SlowMs { get; set; } = 8000;
    public ConcurrentQueue<(string Path, string? Auth, string Body)> Requests { get; } = new();

    public async Task StartAsync()
    {
        var port = AppInstance.FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/v1";
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls($"http://127.0.0.1:{port}");
        b.Logging.ClearProviders();
        _app = b.Build();
        _app.MapGet("/v1/models", (HttpRequest r) =>
            Authorized(r) ? Results.Json(new { data = new[] { new { id = "fake-arabic-model" } } }) : Results.StatusCode(401));
        _app.MapPost("/v1/chat/completions", async (HttpRequest r) =>
        {
            var body = await new StreamReader(r.Body).ReadToEndAsync();
            Requests.Enqueue(("/chat/completions", r.Headers.Authorization.ToString(), body));
            if (!Authorized(r)) return Results.StatusCode(401);
            if (Mode == "error500") return Results.StatusCode(500);
            if (Mode == "slow") await Task.Delay(SlowMs);
            if (Mode == "malformed") return Results.Json(Envelope("هذا ليس JSON"));
            // يقرأ المصادر من وسم <data> ويعيد ملخصًا مسندًا إلى أول مصدر + عبارة بمرجع مختلق
            var user = JsonNode.Parse(body)!["messages"]![1]!["content"]!.GetValue<string>();
            var data = user[(user.IndexOf("<data>", StringComparison.Ordinal) + 6)..user.IndexOf("</data>", StringComparison.Ordinal)];
            var payload = JsonNode.Parse(data)!;
            var first = payload["sources"]?.AsArray().FirstOrDefault();
            var firstRef = first?["ref"]?.GetValue<string>() ?? "S1";
            var text = first?["text"]?.GetValue<string>() ?? "";
            var output = new JsonObject
            {
                ["points"] = new JsonArray(
                    new JsonObject { ["text"] = "ملخص من الخادم المتوافق: " + (text.Length > 80 ? text[..80] : text), ["refs"] = new JsonArray(firstRef) },
                    new JsonObject { ["text"] = "عبارة بمرجع غير موجود", ["refs"] = new JsonArray("S99") }),
                ["decisions"] = new JsonArray(), ["followUps"] = new JsonArray(),
            };
            return Results.Json(Envelope(output.ToJsonString()));
        });
        await _app.StartAsync();
    }

    private bool Authorized(HttpRequest r) => r.Headers.Authorization.ToString() == "Bearer " + ExpectedKey;

    private static object Envelope(string content) => new
    {
        id = "chatcmpl-test", model = "fake-arabic-model",
        choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 1234, completion_tokens = 56, total_tokens = 1290 },
    };

    public async Task StopAsync()
    {
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); _app = null; }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
