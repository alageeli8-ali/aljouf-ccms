using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ccms.Modules.Minutes;

/// <summary>المحاضر: المسودة، والإصدار ببصمة، وطلب الإقرار، والإقرار الداخلي. دورة الاعتماد الكاملة في المرحلة 3 (13).</summary>
public sealed class MinutesModule : IModule
{
    public string Code => "MIN";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IRecordBodyResolver, MinutesBodyResolver>();
        services.AddScoped<IFeatureImpactProvider, SignatureImpact>();
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/minutes").WithTags("Minutes");
        g.MapGet("/", List);
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}/draft", SaveDraft).Validate<DraftRequest>();
        g.MapPost("/{id:guid}/request-signoffs", RequestSignoffs);
        g.MapPost("/{id:guid}/signoffs", SignOff).Validate<SignOffRequest>();
        api.MapPost("/meetings/{meetingId:guid}/minutes", Create).WithTags("Minutes");
    }

    public sealed record Section(
        [property: Required, MaxLength(100)] string Key,
        [property: Required, MaxLength(300)] string Title,
        [property: MaxLength(50000)] string? Body);

    public sealed record DraftRequest([property: Required, MaxLength(50)] List<Section> Sections) : IValidatableObject
    {
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            foreach (var (s, i) in Sections.Select((s, i) => (s, i)))
                foreach (var r in ValidationHelper.Validate(s))
                    yield return new ValidationResult(r.ErrorMessage, r.MemberNames.Select(m => $"sections[{i}].{m}"));
        }
    }

    public sealed record SignOffRequest(
        [property: Required, RegularExpression("^[0-9a-f]{64}$", ErrorMessage = "بصمة غير صالحة")] string ContentHash,
        [property: MaxLength(2000)] string? Reservation);

    private const string Select = """
        SELECT mi.id, mi.meeting_id, mi.status, mi.current_version_no, mi.draft_content::text AS draft_content, mi.updated_at, mi.approved_at,
               m.reference_no, m.title_ar AS meeting_title, m.starts_at, m.body_id, b.name_ar AS body_name,
               (SELECT v.content_hash FROM minutes.minutes_versions v WHERE v.minutes_id = mi.id ORDER BY v.version_no DESC LIMIT 1) AS current_hash
        FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id JOIN bodies.bodies b ON b.id = m.body_id
        """;

    private static object Map(System.Data.Common.DbDataReader r) => new
    {
        id = r.Get<Guid>("id"), meetingId = r.Get<Guid>("meeting_id"), status = r.Get<string>("status"), versionNo = r.Get<int>("current_version_no"),
        content = JsonSerializer.Deserialize<JsonElement>(r.Get<string>("draft_content")), updatedAt = r.Get<DateTime>("updated_at"),
        approvedAt = r.Time("approved_at"), meetingReference = r.Str("reference_no"), meetingTitle = r.Get<string>("meeting_title"),
        meetingStartsAt = r.Get<DateTime>("starts_at"), bodyId = r.Get<Guid>("body_id"), bodyName = r.Get<string>("body_name"),
        contentHash = r.Str("current_hash"),
    };

    private static async Task<IResult> List(Guid? bodyId, string? status, int? page, int? pageSize, Db db, IAccessService access, CancellationToken ct)
    {
        var filter = await access.GetBodyFilterAsync(Permissions.MinutesView, ct);
        var p = PageQuery.From(page, pageSize);
        var args = new { all = filter.All, ids = filter.BodyIds, bodyId, status, limit = p.PageSize, offset = p.Offset };
        const string where = """
            WHERE (@all::boolean OR m.body_id = ANY(@ids::uuid[])) AND (@bodyId::uuid IS NULL OR m.body_id = @bodyId)
              AND (@status::text IS NULL OR mi.status = @status)
            """;
        var total = await db.ScalarAsync<long>("SELECT count(*) FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id " + "\n" + where, args, ct);
        var items = await db.QueryAsync(Select + "\n" + where + " ORDER BY m.starts_at DESC LIMIT @limit OFFSET @offset", args, Map, ct);
        return Results.Ok(new PagedResult<object>(items, p.Page, p.PageSize, total));
    }

    private static readonly object DefaultSections = new
    {
        sections = new[]
        {
            new { key = "opening", title = "الافتتاح", body = "" },
            new { key = "attendance", title = "الحضور", body = "" },
            new { key = "agenda", title = "البنود والمداولات", body = "" },
            new { key = "decisions", title = "القرارات والتوصيات", body = "" },
            new { key = "closing", title = "الختام", body = "" },
        },
    };

    private static async Task<IResult> Create(Guid meetingId, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesEdit, Scope.Record("meeting", meetingId), ct);
        var id = Guid.CreateVersion7();
        await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM meetings.meetings WHERE id = @meetingId FOR UPDATE", new { meetingId }, ct) ?? throw new NotFoundException();
            if (status is "draft" or "cancelled" or "postponed") throw new ConflictException("invalid-transition", "لا يُنشأ محضر لاجتماع غير مجدول أو ملغى");
            if (await tx.ScalarAsync<int>("SELECT count(*) FROM minutes.minutes WHERE meeting_id = @meetingId", new { meetingId }, ct) > 0)
                throw new ConflictException("duplicate", "للاجتماع محضر مسبقًا");
            await tx.ExecuteAsync("""
                INSERT INTO minutes.minutes (id, meeting_id, draft_content, classification, created_by)
                SELECT @id, m.id, @content::jsonb, m.classification, @by FROM meetings.meetings m WHERE m.id = @meetingId
                """, new { id, meetingId, content = Json.Serialize(DefaultSections), by = me.UserId }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.created", "minutes", id.ToString(), After: new { meetingId }), ct);
        }, ct);
        return Results.Created($"/api/v1/minutes/{id}", await db.QuerySingleAsync(Select + " WHERE mi.id = @id", new { id }, Map, ct));
    }

    private static async Task<IResult> Get(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesView, Scope.Record("minutes", id), ct);
        var minutes = await db.QuerySingleAsync(Select + " WHERE mi.id = @id", new { id }, Map, ct) ?? throw new NotFoundException();
        var signoffs = await db.QueryAsync("""
            SELECT s.id, s.user_id, u.display_name_ar, s.method, s.status, s.reservation_text, s.requested_at, s.signed_at, v.version_no
            FROM minutes.minutes_signoffs s JOIN minutes.minutes_versions v ON v.id = s.minutes_version_id JOIN iam.users u ON u.id = s.user_id
            WHERE v.minutes_id = @id ORDER BY v.version_no DESC, u.display_name_ar
            """, new { id }, r => new
        {
            id = r.Get<Guid>("id"), userId = r.Get<Guid>("user_id"), name = r.Get<string>("display_name_ar"), method = r.Get<string>("method"),
            status = r.Get<string>("status"), reservation = r.Str("reservation_text"), requestedAt = r.Get<DateTime>("requested_at"),
            signedAt = r.Time("signed_at"), versionNo = r.Get<int>("version_no"),
        }, ct);
        return Results.Ok(new { minutes, signoffs, permissions = (await access.GetPermissionsAsync(Scope.Record("minutes", id), ct)).Order() });
    }

    private static async Task<IResult> SaveDraft(Guid id, DraftRequest req, Db db, IAccessService access, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesEdit, Scope.Record("minutes", id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM minutes.minutes WHERE id = @id FOR UPDATE", new { id }, ct) ?? throw new NotFoundException();
            if (status is "approved" or "amended") throw new ConflictException("invalid-transition", "المحضر المعتمد لا يُعدل (BRL-07)");
            // التعديل بعد طلب الإقرار يعيد المسودة ويُبطل الإقرارات (BRL-06)
            var invalidated = 0;
            if (status != "draft")
                invalidated = await tx.ExecuteAsync("""
                    UPDATE minutes.minutes_signoffs s SET status = 'invalidated'
                    FROM minutes.minutes_versions v WHERE v.id = s.minutes_version_id AND v.minutes_id = @id AND s.status <> 'invalidated'
                    """, new { id }, ct);
            await tx.ExecuteAsync("UPDATE minutes.minutes SET draft_content = @content::jsonb, status = 'draft' WHERE id = @id",
                new { id, content = Json.Serialize(new { sections = req.Sections }) }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.draft_saved", "minutes", id.ToString(), After: new { sections = req.Sections.Count, previousStatus = status, invalidatedSignoffs = invalidated }), ct);
        }, ct);
        return Results.Ok(await db.QuerySingleAsync(Select + " WHERE mi.id = @id", new { id }, Map, ct));
    }

    private static async Task<IResult> RequestSignoffs(Guid id, Db db, IAccessService access, IFeatureService features, INotifier notifier,
        IAuditWriter audit, ICurrentUser me, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesEdit, Scope.Record("minutes", id), ct);
        var electronic = await features.IsEnabledAsync(FeatureKeys.ESignatureEnabled, ct)
            && await features.GetValueAsync(FeatureKeys.ESignatureMemberRequired, ct) == "true";
        var method = electronic ? "electronic_signature" : "attestation";
        var result = await db.InTransactionAsync(async tx =>
        {
            var m = await tx.QuerySingleAsync("""
                SELECT mi.status, mi.current_version_no, mt.body_id, mt.reference_no FROM minutes.minutes mi JOIN meetings.meetings mt ON mt.id = mi.meeting_id
                WHERE mi.id = @id FOR UPDATE OF mi
                """, new { id }, r => (Status: r.GetString(0), Version: r.GetInt32(1), BodyId: r.GetGuid(2), Ref: r.Str("reference_no")), ct);
            if (m == default) throw new NotFoundException();
            if (m.Status is not ("draft" or "member_review")) throw new ConflictException("invalid-transition", "لا يُطلب الإقرار من حالة المحضر الحالية");
            var versionNo = m.Version + 1;
            var versionId = Guid.CreateVersion7();
            // البصمة تُحسب في القاعدة على التمثيل القانوني لـ jsonb (08 §2.3)
            var hash = await tx.ScalarAsync<string>("""
                INSERT INTO minutes.minutes_versions (id, minutes_id, version_no, content, content_hash, created_by)
                SELECT @versionId, mi.id, @versionNo, mi.draft_content,
                       encode(sha256(convert_to(jsonb_build_object('minutesId', mi.id, 'versionNo', @versionNo::int, 'content', mi.draft_content)::text, 'UTF8')), 'hex'),
                       @by
                FROM minutes.minutes mi WHERE mi.id = @id
                RETURNING content_hash
                """, new { versionId, id, versionNo, by = me.UserId }, ct);
            await tx.ExecuteAsync("UPDATE minutes.minutes SET status = 'pending_signoffs', current_version_no = @versionNo WHERE id = @id", new { id, versionNo }, ct);
            // الموقّعون: الأعضاء المصوتون النشطون عدا الرئيس (ASM-05). سجل الحضور يُضاف في المرحلة 2.
            var signers = await tx.QueryAsync("""
                SELECT user_id FROM bodies.body_members WHERE body_id = @bodyId AND status = 'active' AND is_voting AND role <> 'chair'
                """, new { bodyId = m.BodyId }, r => r.GetGuid(0), ct);
            foreach (var uid in signers)
            {
                await tx.ExecuteAsync("""
                    INSERT INTO minutes.minutes_signoffs (id, minutes_version_id, user_id, method, content_hash) VALUES (@sid, @versionId, @uid, @method, @hash)
                    """, new { sid = Guid.CreateVersion7(), versionId, uid, method, hash }, ct);
                await notifier.NotifyAsync(tx, new NotificationRequest(uid, "signoff_requested", $"محضر بانتظار إقرارك ({m.Ref})",
                    $"Minutes awaiting your sign-off ({m.Ref})", $"/minutes/{id}", $"signoff:{versionId}:{uid}", "warning"), ct);
            }
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.signoffs_requested", "minutes", id.ToString(), After: new { versionNo, hash, method, signers = signers.Count }), ct);
            return new { id, status = "pending_signoffs", versionNo, contentHash = hash, method, signers = signers.Count };
        }, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> SignOff(Guid id, SignOffRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.MinutesSign, Scope.Record("minutes", id), ct);
        await db.InTransactionAsync(async tx =>
        {
            var row = await tx.QuerySingleAsync("""
                SELECT s.id, s.method, s.content_hash, mi.status FROM minutes.minutes_signoffs s
                JOIN minutes.minutes_versions v ON v.id = s.minutes_version_id JOIN minutes.minutes mi ON mi.id = v.minutes_id
                WHERE mi.id = @id AND s.user_id = @uid AND s.status = 'requested' AND v.version_no = mi.current_version_no
                FOR UPDATE OF s
                """, new { id, uid = me.UserId }, r => (Id: r.GetGuid(0), Method: r.GetString(1), Hash: r.GetString(2), Status: r.GetString(3)), ct);
            if (row == default || row.Status != "pending_signoffs") throw new ConflictException("invalid-transition", "لا يوجد طلب إقرار قائم لك على هذا المحضر");
            if (row.Hash != req.ContentHash) throw new ConflictException("hash-mismatch", "البصمة لا تطابق الإصدار الحالي؛ أعد تحميل المحضر (BRL-06)");
            if (row.Method != "attestation") throw new ConflictException("electronic-signature-required", "هذا الإقرار يتطلب التوقيع الإلكتروني عبر المزوّد");
            await tx.ExecuteAsync("UPDATE minutes.minutes_signoffs SET status = 'valid', signed_at = now(), reservation_text = @Reservation, recorded_by = @uid WHERE id = @sid",
                new { sid = row.Id, req.Reservation, uid = me.UserId }, ct);
            var remaining = await tx.ScalarAsync<long>("""
                SELECT count(*) FROM minutes.minutes_signoffs s JOIN minutes.minutes_versions v ON v.id = s.minutes_version_id
                JOIN minutes.minutes mi ON mi.id = v.minutes_id WHERE mi.id = @id AND v.version_no = mi.current_version_no AND s.status = 'requested'
                """, new { id }, ct);
            if (remaining == 0) await tx.ExecuteAsync("UPDATE minutes.minutes SET status = 'pending_approval' WHERE id = @id", new { id }, ct);
            await audit.WriteAsync(tx, new AuditEntry("MIN", "minutes.signed_off", "minutes", id.ToString(), After: new { method = "attestation", req.ContentHash, reservation = req.Reservation is not null, remaining }), ct);
        }, ct);
        return Results.NoContent();
    }
}

internal static class ValidationHelper
{
    public static IEnumerable<ValidationResult> Validate(object o)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(o, new ValidationContext(o), results, true);
        return results;
    }
}

internal sealed class MinutesBodyResolver(Db db) : IRecordBodyResolver
{
    public string RecordType => "minutes";
    public Task<Guid?> ResolveBodyAsync(Guid recordId, CancellationToken ct = default) =>
        db.ScalarAsync<Guid?>("SELECT m.body_id FROM minutes.minutes mi JOIN meetings.meetings m ON m.id = mi.meeting_id WHERE mi.id = @recordId", new { recordId }, ct);
}

/// <summary>أثر تغيير إعدادات التوقيع على المحاضر التي تنتظر توقيعات (المرحلة 1، البند 5).</summary>
internal sealed class SignatureImpact(Db db) : IFeatureImpactProvider
{
    public IReadOnlyCollection<string> FeatureKeys =>
        [Contracts.FeatureKeys.ESignatureEnabled, Contracts.FeatureKeys.ESignatureProvider, Contracts.FeatureKeys.ESignatureMethod, Contracts.FeatureKeys.ESignatureMemberRequired];

    public async Task<IReadOnlyList<FeatureImpactItem>> AssessAsync(string key, string oldValue, string newValue, CancellationToken ct = default)
    {
        // تفعيل التوقيع لا يؤثر في الطلبات القائمة؛ التعطيل أو تغيير المزوّد/الطريقة يؤثر
        if (key == Contracts.FeatureKeys.ESignatureEnabled && newValue == "true") return [];
        if (key == Contracts.FeatureKeys.ESignatureMemberRequired && newValue == "true") return [];
        var pending = await db.QuerySingleAsync("""
            SELECT count(DISTINCT mi.id), count(s.id)
            FROM minutes.minutes mi JOIN minutes.minutes_versions v ON v.minutes_id = mi.id AND v.version_no = mi.current_version_no
            JOIN minutes.minutes_signoffs s ON s.minutes_version_id = v.id
            WHERE mi.status = 'pending_signoffs' AND s.status = 'requested'
            """, null, r => (Minutes: r.GetInt64(0), Signoffs: r.GetInt64(1)), ct);
        return
        [
            new FeatureImpactItem("minutes_pending_signoffs", "محاضر تنتظر توقيعات أو إقرارات", "Minutes awaiting signatures", (int)pending.Minutes),
            new FeatureImpactItem("pending_signoff_requests", "طلبات توقيع لم تكتمل", "Incomplete signature requests", (int)pending.Signoffs),
        ];
    }
}
