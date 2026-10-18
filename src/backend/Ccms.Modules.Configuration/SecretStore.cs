using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace Ccms.Modules.Configuration;

/// <summary>مخزن الأسرار (22 §4). الأولوية: قيمة من بيئة الخادم (Secrets:Values:NAME — متغير بيئة أو مخزن أسرار خارجي مركّب فيها) للقراءة فقط،
/// ثم قيمة مشفرة في القاعدة بـ AES-256-GCM بمفتاح رئيسي من البيئة (Secrets:MasterKey). القيمة الصريحة لا تُعاد في أي استجابة ولا تُكتب في أي سجل.</summary>
internal sealed class SecretStore(Db db, IConfiguration configuration) : ISecretStore
{
    private byte[]? MasterKey()
    {
        var raw = configuration["Secrets:MasterKey"];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var key = Convert.FromBase64String(raw);
            return key.Length == 32 ? key : null;
        }
        catch (FormatException) { return null; }
    }

    public bool CanStore => MasterKey() is not null;

    private string? FromEnvironment(string name) => configuration[$"Secrets:Values:{name}"] is { Length: > 0 } v ? v : null;

    private static string KeyId(byte[] key) => Convert.ToHexString(SHA256.HashData(key))[..12].ToLowerInvariant();
    public static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();

    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        if (FromEnvironment(name) is { } env) return env;
        var key = MasterKey();
        if (key is null) return null;
        var row = await db.QuerySingleAsync("SELECT ciphertext, key_id FROM config.secrets WHERE name = @name", new { name },
            r => (Cipher: r.GetString(0), KeyId: r.GetString(1)), ct);
        if (row == default || row.KeyId != KeyId(key)) return null; // مفتاح رئيسي مختلف: لا يُفك ولا يُخمن
        var blob = Convert.FromBase64String(row.Cipher);
        var nonce = blob[..12];
        var tag = blob[12..28];
        var cipher = blob[28..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(name));
        return Encoding.UTF8.GetString(plain);
    }

    public async Task<SecretInfo> InfoAsync(string name, CancellationToken ct = default)
    {
        if (FromEnvironment(name) is { } env) return new SecretInfo(name, "environment", CanStore, null, null, Fingerprint(env));
        var row = await db.QuerySingleAsync("""
            SELECT s.updated_at, u.display_name_ar, s.fingerprint, s.key_id FROM config.secrets s LEFT JOIN iam.users u ON u.id = s.updated_by WHERE s.name = @name
            """, new { name }, r => (At: r.GetDateTime(0), By: r.Str("display_name_ar"), Fp: r.GetString(2), KeyId: r.GetString(3)), ct);
        if (row == default) return new SecretInfo(name, "none", CanStore, null, null, null);
        var key = MasterKey();
        return key is not null && row.KeyId == KeyId(key)
            ? new SecretInfo(name, "database", true, row.At, row.By, row.Fp)
            : new SecretInfo(name, "unreadable", CanStore, row.At, row.By, row.Fp);
    }

    public async Task SetAsync(ISql tx, string name, string value, Guid by, CancellationToken ct)
    {
        var key = MasterKey() ?? throw new InvalidOperationException("no master key");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(name));
        var blob = Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
        await tx.ExecuteAsync("""
            INSERT INTO config.secrets (name, ciphertext, key_id, fingerprint, updated_at, updated_by) VALUES (@name, @blob, @kid, @fp, now(), @by)
            ON CONFLICT (name) DO UPDATE SET ciphertext = EXCLUDED.ciphertext, key_id = EXCLUDED.key_id, fingerprint = EXCLUDED.fingerprint,
              updated_at = now(), updated_by = EXCLUDED.updated_by
            """, new { name, blob, kid = KeyId(key), fp = Fingerprint(value), by }, ct);
    }

    public bool FromEnv(string name) => FromEnvironment(name) is not null;
}

internal static class Secrets
{
    public sealed record SetSecretRequest(
        [property: Required(ErrorMessage = "القيمة مطلوبة"), MinLength(8, ErrorMessage = "القيمة قصيرة جدًا"), MaxLength(4096)] string Value,
        [property: Required(ErrorMessage = "سبب التغيير مطلوب"), MinLength(5), MaxLength(1000)] string Reason);
    public sealed record ClearSecretRequest([property: Required(ErrorMessage = "سبب الحذف مطلوب"), MinLength(5), MaxLength(1000)] string Reason);

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/secrets", List);
        g.MapPut("/secrets/{name}", Set).Validate<SetSecretRequest>();
        g.MapPost("/secrets/{name}/clear", Clear).Validate<ClearSecretRequest>();
    }

    private static async Task<IResult> List(SecretStore store, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsView, Scope.System, ct);
        var items = new List<SecretInfo>();
        foreach (var n in SecretNames.All) items.Add(await store.InfoAsync(n, ct));
        return Results.Ok(new { canStore = store.CanStore, items });
    }

    private static async Task Log(ISql sql, string name, string action, string? fp, string? reason, ICurrentUser me, CancellationToken ct) =>
        await sql.ExecuteAsync("""
            INSERT INTO config.secret_history (id, name, action, fingerprint, reason, changed_by, actor_display) VALUES (@id, @name, @action, @fp, @reason, @by, @actor)
            """, new { id = Guid.CreateVersion7(), name, action, fp, reason, by = me.UserId, actor = me.DisplayName }, ct);

    private static async Task Authorize(string name, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, string? reason, CancellationToken ct)
    {
        try { await access.DemandAsync(Permissions.AdminSettingsManage, Scope.System, ct); }
        catch (ForbiddenException)
        {
            await Log(db, name, "denied", null, reason, me, ct);
            await audit.WriteAsync(new AuditEntry("ADM", "config.secret_change_denied", "secret", name, Outcome: "denied"), ct);
            throw;
        }
        if (!SecretNames.All.Contains(name)) throw new NotFoundException("سر غير معروف");
    }

    private static async Task<IResult> Set(string name, SetSecretRequest req, SecretStore store, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit,
        IConnectorStatusStore status, CancellationToken ct)
    {
        await Authorize(name, db, access, me, audit, req.Reason, ct);
        if (store.FromEnv(name)) throw new ConflictException("secret-from-environment", "هذا السر مضبوط من بيئة الخادم ويتقدم على المخزن؛ غيّره من هناك");
        if (!store.CanStore) throw new ConflictException("secret-store-unavailable", "مخزن الأسرار غير مهيأ: اضبط مفتاحًا رئيسيًا (Secrets:MasterKey) في بيئة الخادم، أو مرر السر عبر متغير بيئة");
        var value = req.Value.Trim();
        if (value.Any(char.IsControl) || value.Contains(' ')) throw AppValidationException.For("value", "القيمة تحتوي محارف غير مسموحة");
        var fp = SecretStore.Fingerprint(value);
        await db.InTransactionAsync(async tx =>
        {
            await store.SetAsync(tx, name, value, me.UserId, ct);
            await Log(tx, name, "set", fp, req.Reason, me, ct);
            await audit.WriteAsync(tx, new AuditEntry("ADM", "config.secret_set", "secret", name, After: new { fingerprint = fp, req.Reason }), ct);
        }, ct);
        return Results.Ok(await store.InfoAsync(name, ct));
    }

    private static async Task<IResult> Clear(string name, ClearSecretRequest req, SecretStore store, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit,
        CancellationToken ct)
    {
        await Authorize(name, db, access, me, audit, req.Reason, ct);
        await db.InTransactionAsync(async tx =>
        {
            var fp = await tx.ScalarAsync<string>("DELETE FROM config.secrets WHERE name = @name RETURNING fingerprint", new { name }, ct)
                ?? throw new NotFoundException("لا توجد قيمة مخزنة لهذا السر");
            await Log(tx, name, "cleared", fp, req.Reason, me, ct);
            await audit.WriteAsync(tx, new AuditEntry("ADM", "config.secret_cleared", "secret", name, Before: new { fingerprint = fp }, After: new { req.Reason }), ct);
        }, ct);
        return Results.Ok(await store.InfoAsync(name, ct));
    }
}
