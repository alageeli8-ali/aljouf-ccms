using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ccms.BuildingBlocks.Data;
using Ccms.Contracts;

namespace Ccms.Modules.Signatures;

/// <summary>بوابة التوقيع كما تراها وحدة المحاضر: الجاهزية، وإنشاء طلب على إصدار ثابت، وإبطاله، وأدلته. لا تعتمد على وحدة المحاضر.</summary>
internal sealed class ESignatureGateway(Db db, IFeatureService features, ProviderRegistry providers, IDocumentStore store, INumberingService numbering,
    INotifier notifier, IAuditWriter audit) : ISignatureGateway
{
    public async Task<(bool Ready, string Reason)> ReadinessAsync(CancellationToken ct = default)
    {
        var s = await SigSettings.LoadAsync(features, ct);
        return Readiness(s, s.Method, providers);
    }

    public static (bool Ready, string Reason) Readiness(SigSettings s, string type, ProviderRegistry providers)
    {
        if (!s.Enabled) return (false, "esign_disabled");
        if (type == SignatureTypes.InAppApproval) return (true, "esign");
        if (s.Provider == "none") return (false, "esign_provider_not_configured");
        var p = providers.Find(s.Provider);
        if (p is null) return (false, "esign_provider_unknown");
        if (!p.Supports(type)) return (false, "esign_provider_unsupported");
        var r = p.Readiness();
        if (!r.Configured || !r.AllowedInEnvironment) return (false, r.Reason);
        return (true, "esign");
    }

    /// <summary>مسار التوقيع: الأخص فالأعم (الجهة ونوع الاجتماع، ثم الجهة، ثم النوع، ثم العام)، وإلا المسار الافتراضي من الإعدادات المركزية.</summary>
    public static async Task<ResolvedRoute> ResolveRouteAsync(ISql sql, SigSettings s, Guid bodyId, string meetingType, bool policyIncludesChair, CancellationToken ct)
    {
        var row = await sql.QuerySingleAsync("""
            SELECT id, name_ar, steps::text, sequential, signature_type, expiry_days FROM sig.routes
            WHERE active AND (body_id = @bodyId OR body_id IS NULL) AND (meeting_type = @meetingType OR meeting_type IS NULL)
            ORDER BY (body_id IS NULL), (meeting_type IS NULL) LIMIT 1
            """, new { bodyId, meetingType }, r => (Id: r.GetGuid(0), Name: r.GetString(1), Steps: r.GetString(2), Sequential: r.IsDBNull(3) ? (bool?)null : r.GetBoolean(3),
            Type: r.Str("signature_type"), Expiry: r.GetInt32(5)), ct);
        if (row != default)
            return new ResolvedRoute(row.Id, row.Name, ResolvedRoute.ParseSteps(row.Steps), row.Sequential ?? s.Sequential, row.Type ?? s.Method, row.Expiry, "route");
        var steps = new List<RouteStep>();
        if (s.SecretaryRequired) steps.Add(new RouteStep(steps.Count + 1, "secretary", false));
        if (s.MemberRequired) steps.Add(new RouteStep(steps.Count + 1, "members", false));
        if (s.ChairRequired) steps.Add(new RouteStep(steps.Count + 1, "chair", true));
        if (steps.Count == 0)
        {
            // لا مفاتيح أدوار مفعلة: الموقّعون كما تحددهم سياسة الجهة (الأعضاء، ثم الرئيس إن شملته)
            steps.Add(new RouteStep(1, "members", false));
            if (policyIncludesChair) steps.Add(new RouteStep(2, "chair", true));
        }
        return new ResolvedRoute(null, "المسار الافتراضي من إعدادات التوقيع", steps, s.Sequential, s.Method, 14, "settings");
    }

    public async Task<CreatedSignatureRequest?> CreateForMinutesAsync(ISql tx, MinutesSignatureSubject subject, Guid by, CancellationToken ct = default)
    {
        var s = await SigSettings.LoadAsync(features, ct);
        if (!s.Enabled) return null;
        var route = await ResolveRouteAsync(tx, s, subject.BodyId, subject.MeetingType, subject.Candidates.PolicyIncludesChair, ct);
        var (ready, _) = Readiness(s, route.SignatureType, providers);
        if (!ready) return null;

        // الموقّعون بترتيب المسار؛ لا يتكرر المستخدم في أكثر من مرحلة
        var placed = new HashSet<Guid>();
        var plan = new List<(Guid User, string Role, int Step, bool AllowDelegation)>();
        void Add(IEnumerable<Guid> users, string role, int step, bool deleg)
        {
            foreach (var u in users) if (placed.Add(u)) plan.Add((u, role, step, deleg));
        }
        foreach (var st in route.Steps)
        {
            var users = st.Role switch
            {
                "secretary" => subject.Candidates.Secretaries,
                "members" => subject.Candidates.Members,
                "chair" => subject.Candidates.Chairs,
                _ => [],
            };
            Add(await OrderByNameAsync(tx, users, ct), st.Role == "members" ? "member" : st.Role, st.Order, st.AllowDelegation);
        }
        if (subject.Restart)
        {
            // إعادة الطلب: كل من لم يوقّع يجب أن يُشمل حتى لو تغيّر المسار
            var last = route.Steps.Count == 0 ? 0 : route.Steps.Max(x => x.Order);
            Add(await OrderByNameAsync(tx, subject.Candidates.Secretaries, ct), "secretary", last + 1, false);
            Add(await OrderByNameAsync(tx, subject.Candidates.Members, ct), "member", last + 1, false);
            Add(await OrderByNameAsync(tx, subject.Candidates.Chairs, ct), "chair", last + 2, true);
        }
        if (plan.Count == 0) return null;

        var id = Guid.CreateVersion7();
        var year = DateTime.UtcNow.Year;
        var reference = $"SIG-{year}-{await numbering.NextAsync(tx, "esign", year, ct):0000}";
        var doc = await store.SaveAsync(tx, subject.FileName, "application/pdf", subject.Pdf, "minutes", subject.MinutesId, "signature_document",
            subject.Classification, by, ct);
        var provider = route.SignatureType == SignatureTypes.InAppApproval ? "internal" : s.Provider;
        var status = route.SignatureType == SignatureTypes.ProviderESign ? "queued" : "in_progress";
        await tx.ExecuteAsync("""
            INSERT INTO sig.requests (id, reference_no, minutes_id, minutes_version_id, version_no, body_id, content_hash, document_id, document_sha256, document_size,
              signature_type, provider, sequential, route_id, route_snapshot, settings_snapshot, status, current_document_id, current_document_sha256, expires_at,
              next_attempt_at, created_by)
            VALUES (@id, @reference, @minutes, @version, @versionNo, @body, @hash, @doc, @sha, @size, @type, @provider, @sequential, @routeId, @route::jsonb,
              @settings::jsonb, @status, @doc, @sha, now() + make_interval(days => @expiry::int), CASE WHEN @status = 'queued' THEN now() END, @by)
            """, new
        {
            id, reference, minutes = subject.MinutesId, version = subject.VersionId, versionNo = subject.VersionNo, body = subject.BodyId, hash = subject.ContentHash,
            doc = doc.Id, sha = doc.Sha256, size = doc.Size, type = route.SignatureType, provider, sequential = route.Sequential, routeId = route.Id,
            route = route.ToJson().ToJsonString(), settings = s.ToJson().ToJsonString(), status, expiry = route.ExpiryDays, by,
        }, ct);
        var signers = new List<CreatedSignatureSigner>();
        var position = 0;
        foreach (var p in plan.OrderBy(p => p.Step))
        {
            var sid = Guid.CreateVersion7();
            await tx.ExecuteAsync("""
                INSERT INTO sig.signers (id, request_id, user_id, signer_role, step_order, position, allow_delegation) VALUES (@sid, @id, @user, @role, @step, @pos, @deleg)
                """, new { sid, id, user = p.User, role = p.Role, step = p.Step, pos = ++position, deleg = p.AllowDelegation }, ct);
            signers.Add(new CreatedSignatureSigner(sid, p.User, p.Role));
        }
        await SigData.EventAsync(tx, id, null, "created", "system", by, new
        {
            type = route.SignatureType, provider, route = route.ToJson(), settings = s.ToJson(), documentSha256 = doc.Sha256, documentSize = doc.Size,
            contentHash = subject.ContentHash, versionNo = subject.VersionNo, signers = plan.Count, restart = subject.Restart,
        }, ct);
        await audit.WriteAsync(tx, new AuditEntry("SIG", "esign.request_created", "minutes", subject.MinutesId.ToString(),
            After: new { requestId = id, reference, type = route.SignatureType, provider, versionNo = subject.VersionNo, documentSha256 = doc.Sha256, signers = plan.Count }), ct);
        await SignerFlow.ActivateAsync(tx, notifier, id, subject.MinutesId, reference, ct);
        return new CreatedSignatureRequest(id, route.SignatureType, signers);
    }

    private static async Task<IEnumerable<Guid>> OrderByNameAsync(ISql sql, IReadOnlyList<Guid> users, CancellationToken ct) =>
        users.Count == 0 ? [] : await sql.QueryAsync("SELECT id FROM iam.users WHERE id = ANY(@ids::uuid[]) ORDER BY display_name_ar, id",
            new { ids = "{" + string.Join(",", users) + "}" }, r => r.GetGuid(0), ct);

    public async Task<int> VoidForVersionAsync(ISql tx, Guid minutesVersionId, string reason, Guid by, CancellationToken ct = default)
    {
        var live = await SigData.RequestsWhereAsync(tx, "r.minutes_version_id = @minutesVersionId AND r.status IN ('queued','in_progress','on_hold')", new { minutesVersionId }, ct);
        foreach (var r in live)
        {
            await tx.ExecuteAsync("""
                UPDATE sig.requests SET status = 'voided', status_reason = @reason, closed_by = @by, closed_at = now(), updated_at = now(),
                  pending_provider_cancel = (provider <> 'internal' AND provider_ref IS NOT NULL)
                WHERE id = @id
                """, new { id = r.Id, reason, by }, ct);
            await tx.ExecuteAsync("UPDATE sig.signers SET status = 'cancelled', updated_at = now() WHERE request_id = @id AND status IN ('waiting','pending')", new { id = r.Id }, ct);
            await SigData.EventAsync(tx, r.Id, null, "voided", "system", by, new { reason, signedBefore = await SignedCountAsync(tx, r.Id, ct) }, ct);
            await audit.WriteAsync(tx, new AuditEntry("SIG", "esign.request_voided", "minutes", r.MinutesId.ToString(), After: new { requestId = r.Id, r.Reference, reason }), ct);
        }
        return live.Count;
    }

    private static Task<long> SignedCountAsync(ISql sql, Guid id, CancellationToken ct) =>
        sql.ScalarAsync<long>("SELECT count(*) FROM sig.signers WHERE request_id = @id AND status = 'signed'", new { id }, ct);

    public async Task<IReadOnlyList<SignatureEvidenceLine>> EvidenceForVersionAsync(Guid minutesVersionId, CancellationToken ct = default)
    {
        var lines = new List<SignatureEvidenceLine>();
        foreach (var r in (await SigData.RequestsWhereAsync(db, "r.minutes_version_id = @minutesVersionId", new { minutesVersionId }, ct)).OrderBy(r => r.CreatedAt))
        {
            var overall = r.VerificationJson is null ? null : JsonNode.Parse(r.VerificationJson)?["overall"]?.GetValue<string>();
            foreach (var s in await SigData.SignersAsync(db, r.Id, ct))
            {
                if (s.Status is not ("signed" or "declined")) continue;
                var ev = s.EvidenceJson is null ? null : JsonNode.Parse(s.EvidenceJson);
                lines.Add(new SignatureEvidenceLine(r.Reference, r.Type, s.Name, s.Role, s.Status, s.SignedAt,
                    s.ActedBy is { } a && a != s.UserId ? s.ActedByName : null, r.DocumentSha256, s.SignedDocumentSha256 ?? r.SignedDocumentSha256,
                    ev?["certificate"]?["subject"]?.GetValue<string>(), ev?["certificate"]?["serial"]?.GetValue<string>(), overall));
            }
        }
        return lines;
    }

    public static string Sha256(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
}

/// <summary>تفعيل أدوار الموقّعين: المراحل بالترتيب، وداخل المرحلة بالتوازي أو بالتتابع حسب المسار.</summary>
internal static class SignerFlow
{
    public static async Task<List<Guid>> ActivateAsync(ISql tx, INotifier notifier, Guid requestId, Guid minutesId, string reference, CancellationToken ct)
    {
        var rows = await tx.QueryAsync("SELECT id, user_id, step_order, position, status FROM sig.signers WHERE request_id = @requestId ORDER BY position",
            new { requestId }, r => (Id: r.GetGuid(0), User: r.GetGuid(1), Step: r.GetInt32(2), Pos: r.GetInt32(3), Status: r.GetString(4)), ct);
        var sequential = await tx.ScalarAsync<bool>("SELECT sequential FROM sig.requests WHERE id = @requestId", new { requestId }, ct);
        var open = rows.Where(r => r.Status is "waiting" or "pending").ToList();
        if (open.Count == 0) return [];
        var step = open.Min(r => r.Step);
        var inStep = open.Where(r => r.Step == step).OrderBy(r => r.Pos).ToList();
        var toActivate = (sequential ? inStep.Take(1) : inStep).Where(r => r.Status == "waiting").ToList();
        foreach (var r in toActivate)
        {
            await tx.ExecuteAsync("UPDATE sig.signers SET status = 'pending', updated_at = now() WHERE id = @id", new { id = r.Id }, ct);
            await notifier.NotifyAsync(tx, new NotificationRequest(r.User, "signature_requested", $"طلب توقيع بانتظارك ({reference})", $"Signature requested ({reference})",
                $"/minutes/{minutesId}?tab=signatures", $"esign:{requestId}:{r.Id}", "warning"), ct);
        }
        return toActivate.Select(r => r.Id).ToList();
    }
}
