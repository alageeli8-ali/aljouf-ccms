using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Contracts;

/// <summary>منع التكرار عند إعادة المحاولة أو تحديث الصفحة: طلب الإنشاء يحمل ترويسة Idempotency-Key،
/// فإن تكرر المفتاح نفسه من المستخدم نفسه أُعيدت النتيجة الأولى دون تنفيذ ثانٍ (المرحلة 3، هـ).</summary>
public sealed class IdempotencyFilter : IEndpointFilter
{
    public const string Header = "Idempotency-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var key = http.Request.Headers[Header].ToString();
        if (string.IsNullOrEmpty(key)) return await next(ctx);
        if (key.Length is < 8 or > 100) throw AppValidationException.For("idempotencyKey", "مفتاح منع التكرار يجب أن يكون بين 8 و100 محرف");
        var me = http.RequestServices.GetRequiredService<ICurrentUser>();
        if (!me.IsAuthenticated) return await next(ctx);
        var db = http.RequestServices.GetRequiredService<Db>();
        var route = $"{http.Request.Method} {http.Request.Path}";
        var hash = RequestHash(route, ctx.Arguments);

        var inserted = await db.ExecuteAsync("""
            INSERT INTO config.idempotency_keys (user_id, key, route, request_hash) VALUES (@user, @key, @route, @hash)
            ON CONFLICT (user_id, key) DO NOTHING
            """, new { user = me.UserId, key, route, hash });
        if (inserted == 0)
        {
            var row = await db.QuerySingleAsync("""
                SELECT route, request_hash, status, status_code, location, response::text AS response FROM config.idempotency_keys WHERE user_id = @user AND key = @key
                """, new { user = me.UserId, key }, r => (Route: r.GetString(0), Hash: r.GetString(1), Status: r.GetString(2),
                Code: r.IsDBNull(3) ? 0 : r.GetInt32(3), Location: r.Str("location"), Response: r.Str("response")));
            if (row.Route != route || row.Hash != hash)
                throw new ConflictException("idempotency-key-reused", "استُخدم مفتاح منع التكرار نفسه لطلب مختلف");
            if (row.Status != "completed")
                throw new ConflictException("request-in-progress", "الطلب نفسه قيد التنفيذ؛ انتظر نتيجته");
            http.Response.Headers["Idempotent-Replayed"] = "true";
            if (row.Location is not null) http.Response.Headers.Location = row.Location;
            if (row.Response is null) return Results.StatusCode(row.Code);
            return Results.Json(JsonSerializer.Deserialize<JsonElement>(row.Response), Json.Options, statusCode: row.Code);
        }

        object? result;
        try
        {
            result = await next(ctx);
        }
        catch
        {
            await db.ExecuteAsync("DELETE FROM config.idempotency_keys WHERE user_id = @user AND key = @key", new { user = me.UserId, key });
            throw;
        }
        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        var value = (result as IValueHttpResult)?.Value;
        var location = result?.GetType().GetProperty("Location")?.GetValue(result) as string;
        if (status >= 400)
        {
            await db.ExecuteAsync("DELETE FROM config.idempotency_keys WHERE user_id = @user AND key = @key", new { user = me.UserId, key });
            return result;
        }
        await db.ExecuteAsync("""
            UPDATE config.idempotency_keys SET status = 'completed', status_code = @status, location = @location, response = @response::jsonb
            WHERE user_id = @user AND key = @key
            """, new { user = me.UserId, key, status, location, response = value is null ? null : Json.Serialize(value) });
        return result;
    }

    private static string RequestHash(string route, IList<object?> args)
    {
        var parts = args.Where(a => a is not null && (a is Guid or string or int or bool
            || a.GetType().Namespace?.StartsWith("Ccms", StringComparison.Ordinal) == true && a.GetType().Name.EndsWith("Request", StringComparison.Ordinal)))
            .Select(a => Json.Serialize(a));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(route + "\n" + string.Join("\n", parts))));
    }
}

public static class IdempotencyExtensions
{
    public static RouteHandlerBuilder Idempotent(this RouteHandlerBuilder b) => b.AddEndpointFilter<IdempotencyFilter>();
}
