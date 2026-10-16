using System.Text.Json;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.Contracts;

namespace Ccms.Modules.Signatures;

/// <summary>قيم إعدادات التوقيع كما هي لحظة القراءة؛ تُثبَّت لقطة منها في كل طلب فلا يتغير سلوكه بتغيير الإعدادات لاحقًا.</summary>
internal sealed record SigSettings(bool Enabled, string Provider, string Method, bool MemberRequired, bool ChairRequired, bool SecretaryRequired, bool Sequential,
    bool PdfOutput, bool ReauthRequired)
{
    public static async Task<SigSettings> LoadAsync(IFeatureService f, CancellationToken ct) => new(
        await f.IsEnabledAsync(FeatureKeys.ESignatureEnabled, ct),
        await f.GetValueAsync(FeatureKeys.ESignatureProvider, ct),
        await f.GetValueAsync(FeatureKeys.ESignatureMethod, ct),
        await f.IsEnabledAsync(FeatureKeys.ESignatureMemberRequired, ct),
        await f.IsEnabledAsync(FeatureKeys.ESignatureChairRequired, ct),
        await f.IsEnabledAsync(FeatureKeys.ESignatureSecretaryRequired, ct),
        await f.IsEnabledAsync(FeatureKeys.ESignatureSequential, ct),
        await f.IsEnabledAsync(FeatureKeys.ESignaturePdfOutput, ct),
        await f.IsEnabledAsync(FeatureKeys.ESignatureReauthRequired, ct));

    public JsonObject ToJson() => new()
    {
        ["enabled"] = Enabled, ["provider"] = Provider, ["method"] = Method, ["memberRequired"] = MemberRequired, ["chairRequired"] = ChairRequired,
        ["secretaryRequired"] = SecretaryRequired, ["sequential"] = Sequential, ["pdfOutput"] = PdfOutput, ["reauthRequired"] = ReauthRequired,
    };
}

internal sealed record RouteStep(int Order, string Role, bool AllowDelegation);

internal sealed record ResolvedRoute(Guid? Id, string NameAr, IReadOnlyList<RouteStep> Steps, bool Sequential, string SignatureType, int ExpiryDays, string Source)
{
    public JsonObject ToJson() => new()
    {
        ["id"] = Id?.ToString(), ["nameAr"] = NameAr, ["sequential"] = Sequential, ["signatureType"] = SignatureType, ["expiryDays"] = ExpiryDays, ["source"] = Source,
        ["steps"] = new JsonArray(Steps.Select(s => (JsonNode)new JsonObject { ["order"] = s.Order, ["role"] = s.Role, ["allowDelegation"] = s.AllowDelegation }).ToArray()),
    };

    public static List<RouteStep> ParseSteps(string json) =>
        JsonSerializer.Deserialize<List<RouteStep>>(json, Json.Web)!.OrderBy(s => s.Order).ToList();
}

internal sealed record SigRequest(Guid Id, string Reference, Guid MinutesId, Guid VersionId, int VersionNo, Guid BodyId, string ContentHash, Guid DocumentId,
    string DocumentSha256, long DocumentSize, string Type, string Provider, string? ProviderRef, bool Sequential, string Status, string? StatusReason,
    Guid? CurrentDocumentId, string? CurrentDocumentSha256, Guid? SignedDocumentId, string? SignedDocumentSha256, string? VerificationJson, DateTime ExpiresAt,
    int Attempts, DateTime? NextAttemptAt, string? LastError, bool PendingProviderCancel, JsonObject Settings, JsonObject Route, Guid CreatedBy, DateTime CreatedAt,
    DateTime? CompletedAt)
{
    public bool Live => Status is "queued" or "in_progress" or "on_hold";
    public bool ReauthRequired => Settings["reauthRequired"]?.GetValue<bool>() ?? true;
    public bool PdfOutput => Settings["pdfOutput"]?.GetValue<bool>() ?? true;
}

internal sealed record SigSigner(Guid Id, Guid UserId, string Name, string Role, int Step, int Position, bool AllowDelegation, string Status, Guid? ActedBy,
    string? ActedByName, Guid? DelegationId, DateTime? SignedAt, string? DeclineReason, string? SignedDocumentSha256, string? EvidenceJson, string? ProviderSignerRef);

internal static class Json
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
        { "token", "secret", "password", "authorization", "apiKey", "clientSecret", "x-mock-signature", "signatureHeader", "pfx", "privateKey" };

    /// <summary>يحذف أي حقل سري قبل الحفظ في السجلات.</summary>
    public static JsonNode? Sanitize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var k in o.Select(p => p.Key).ToList())
                    if (SecretKeys.Contains(k)) o.Remove(k); else Sanitize(o[k]);
                break;
            case JsonArray a:
                foreach (var x in a) Sanitize(x);
                break;
        }
        return node;
    }
}

internal static class SigData
{
    private const string RequestColumns = """
        r.id, r.reference_no, r.minutes_id, r.minutes_version_id, r.version_no, r.body_id, r.content_hash, r.document_id, r.document_sha256, r.document_size,
        r.signature_type, r.provider, r.provider_ref, r.sequential, r.status, r.status_reason, r.current_document_id, r.current_document_sha256,
        r.signed_document_id, r.signed_document_sha256, r.verification::text, r.expires_at, r.dispatch_attempts, r.next_attempt_at, r.last_error,
        r.pending_provider_cancel, r.settings_snapshot::text, r.route_snapshot::text, r.created_by, r.created_at, r.completed_at
        """;

    private static SigRequest MapRequest(System.Data.Common.DbDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetGuid(3), r.GetInt32(4),
        r.GetGuid(5), r.GetString(6), r.GetGuid(7), r.GetString(8), r.GetInt64(9), r.GetString(10), r.GetString(11), r.Str("provider_ref"), r.GetBoolean(13),
        r.GetString(14), r.Str("status_reason"), r.Uuid("current_document_id"), r.Str("current_document_sha256"), r.Uuid("signed_document_id"),
        r.Str("signed_document_sha256"), r.Str("verification"), r.GetDateTime(21), r.GetInt32(22), r.Time("next_attempt_at"), r.Str("last_error"), r.GetBoolean(25),
        JsonNode.Parse(r.GetString(26))!.AsObject(), JsonNode.Parse(r.GetString(27))!.AsObject(), r.GetGuid(28), r.GetDateTime(29), r.Time("completed_at"));

    public static Task<SigRequest?> RequestAsync(ISql sql, Guid id, bool forUpdate, CancellationToken ct) =>
        sql.QuerySingleAsync<SigRequest?>($"SELECT {RequestColumns} FROM sig.requests r WHERE r.id = @id {(forUpdate ? "FOR UPDATE" : "")}", new { id }, MapRequest, ct);

    public static Task<List<SigRequest>> RequestsWhereAsync(ISql sql, string where, object args, CancellationToken ct) =>
        sql.QueryAsync($"SELECT {RequestColumns} FROM sig.requests r WHERE {where} ORDER BY r.created_at DESC", args, MapRequest, ct);

    public static Task<List<SigSigner>> SignersAsync(ISql sql, Guid requestId, CancellationToken ct) =>
        sql.QueryAsync("""
            SELECT s.id, s.user_id, u.display_name_ar, s.signer_role, s.step_order, s.position, s.allow_delegation, s.status, s.acted_by_user_id, a.display_name_ar,
                   s.delegation_id, s.signed_at, s.decline_reason, s.signed_document_sha256, s.evidence::text, s.provider_signer_ref
            FROM sig.signers s JOIN iam.users u ON u.id = s.user_id LEFT JOIN iam.users a ON a.id = s.acted_by_user_id
            WHERE s.request_id = @requestId ORDER BY s.position
            """, new { requestId }, r => new SigSigner(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetBoolean(6),
            r.GetString(7), r.Uuid("acted_by_user_id"), r.IsDBNull(9) ? null : r.GetString(9), r.Uuid("delegation_id"), r.Time("signed_at"), r.Str("decline_reason"),
            r.Str("signed_document_sha256"), r.Str("evidence"), r.Str("provider_signer_ref")), ct);

    public static Task EventAsync(ISql sql, Guid? requestId, Guid? signerId, string kind, string source, Guid? actor, object? data, CancellationToken ct)
    {
        var node = data is null ? new JsonObject() : data as JsonNode ?? JsonSerializer.SerializeToNode(data, Json.Web);
        return sql.ExecuteAsync("""
            INSERT INTO sig.events (id, request_id, signer_id, kind, source, actor_user_id, data) VALUES (@id, @requestId, @signerId, @kind, @source, @actor, @data::jsonb)
            """, new { id = Guid.CreateVersion7(), requestId, signerId, kind, source, actor, data = Json.Sanitize(node)!.ToJsonString() }, ct);
    }

    public static Task SetStatusAsync(ISql sql, Guid id, string status, string? reason, Guid? closedBy, CancellationToken ct) =>
        sql.ExecuteAsync("""
            UPDATE sig.requests SET status = @status, status_reason = COALESCE(@reason, status_reason), updated_at = now(),
              completed_at = CASE WHEN @status = 'completed' THEN now() ELSE completed_at END,
              closed_by = CASE WHEN @status IN ('voided','cancelled','declined','expired','failed') THEN COALESCE(@closedBy, closed_by) ELSE closed_by END,
              closed_at = CASE WHEN @status IN ('voided','cancelled','declined','expired','failed') THEN now() ELSE closed_at END
            WHERE id = @id
            """, new { id, status, reason, closedBy }, ct);
}

internal static class Labels
{
    public static string Type(string t) => t switch
    {
        SignatureTypes.InAppApproval => "موافقة إلكترونية موثقة داخل النظام",
        SignatureTypes.ProviderESign => "توقيع إلكتروني عبر مزوّد خارجي",
        SignatureTypes.PdfDigitalSignature => "توقيع رقمي تشفيري على PDF",
        _ => t,
    };

    public static string TypeNotice(string t, bool productionGrade) => t switch
    {
        SignatureTypes.InAppApproval => "موافقة موثقة بهوية الدخول وإعادة التحقق والوقت وبصمة النسخة. ليست توقيعًا رقميًا بشهادة تشفير.",
        _ when !productionGrade => "يجري عبر مزوّد تجريبي للاختبار فقط: ليس خدمة توقيع إنتاجية ولا يحقق حجية نظامية.",
        _ => "لا يُعد مستوفيًا للمتطلبات النظامية قبل نجاح التكامل الفعلي مع المزوّد المعتمد ومراجعة المكتب لمتطلباته.",
    };
}
