using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Text.Json;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Http;

namespace Ccms.Modules.Bodies;

internal sealed class BodyPolicyQuery(Db db) : IBodyPolicyQuery
{
    internal const string Select = """
        SELECT p.id, p.body_id, p.version, p.effective_from, p.quorum_rule::text AS quorum_rule, p.voting_rule::text AS voting_rule,
               p.signoff_rule::text AS signoff_rule, p.minutes_review_days, p.extension_approver, p.allow_proxy, p.allow_remote_attendance,
               p.allow_circulation, p.created_at, u.display_name_ar AS created_by_name, p.minutes_workflow::text AS minutes_workflow,
               p.escalation_rule::text AS escalation_rule
        FROM bodies.body_policies p LEFT JOIN iam.users u ON u.id = p.created_by
        """;

    internal static BodyPolicy Map(DbDataReader r)
    {
        var signoff = PolicyJson.Signoff(r.Get<string>("signoff_rule"));
        return new BodyPolicy(r.Get<Guid>("id"), r.Get<Guid>("body_id"), r.Get<int>("version"), r.Get<DateOnly>("effective_from"),
            PolicyJson.Quorum(r.Get<string>("quorum_rule")), PolicyJson.Voting(r.Get<string>("voting_rule")), signoff.Required,
            r.Get<int>("minutes_review_days"), r.Get<string>("extension_approver"), r.Get<bool>("allow_proxy"), signoff.MaxProxies,
            r.Get<bool>("allow_remote_attendance"), r.Get<bool>("allow_circulation"), r.Get<DateTime>("created_at"), r.Str("created_by_name"),
            MinutesWorkflow.Parse(r.Str("minutes_workflow")), EscalationRule.Parse(r.Str("escalation_rule")));
    }

    public Task<BodyPolicy?> GetEffectiveAsync(Guid bodyId, DateOnly on, CancellationToken ct = default) =>
        db.QuerySingleAsync<BodyPolicy?>(Select + "\nWHERE p.body_id = @bodyId AND p.effective_from <= @on ORDER BY p.effective_from DESC, p.version DESC LIMIT 1",
            new { bodyId, on }, Map, ct);

    public Task<BodyPolicy?> GetByIdAsync(Guid policyId, CancellationToken ct = default) =>
        db.QuerySingleAsync<BodyPolicy?>(Select + "\nWHERE p.id = @policyId", new { policyId }, Map, ct);
}

public sealed record PolicyRequest(
    [property: Required] DateOnly? EffectiveFrom,
    [property: Required, OneOf("majority", "fraction", "fixed")] string QuorumType,
    [property: Range(1, 99)] int? QuorumNumerator,
    [property: Range(1, 100)] int? QuorumDenominator,
    [property: Range(1, 500)] int? QuorumFixedCount,
    bool ChairRequired,
    bool CountRemote,
    bool AllowOverride,
    [property: Required, OneOf("majority_of_votes_cast", "majority_of_present", "absolute_majority_of_members", "two_thirds_of_present", "unanimity")] string VotingThreshold,
    [property: OneOf("excluded", "count_as_against")] string? Abstentions,
    [property: Required, OneOf("chair_casting_vote", "motion_fails", "revote")] string TieBreaker,
    bool SecretAllowed,
    bool SignoffRequired,
    [property: Range(1, 60)] int MinutesReviewDays,
    [property: Required, OneOf("secretary", "chair")] string ExtensionApprover,
    bool AllowProxy,
    [property: Range(1, 5)] int? MaxProxiesPerMember,
    bool AllowRemoteAttendance,
    bool AllowCirculation,
    [property: Required(ErrorMessage = "سبب تعديل السياسة مطلوب"), MinLength(5), MaxLength(1000)] string Reason,
    EscalationRequest? Escalation = null,
    bool? MinutesSecretaryReview = null,
    bool? MinutesChairReview = null,
    bool? MinutesMemberReview = null,
    [property: OneOf("none", "present_members", "present_members_and_chair", "all_members")] string? MinutesSignatures = null,
    [property: OneOf("attestation", "wet_signature")] string? MinutesSignatureFallback = null,
    [property: OneOf("chair", "recorded_external")] string? MinutesFinalApproval = null,
    [property: OneOf("full", "final_only")] string? MinutesAmendmentReapproval = null,
    [property: MaxLength(40)] string? MinutesTemplateCode = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (QuorumType == "fraction")
        {
            if (QuorumNumerator is null || QuorumDenominator is null) yield return new("حدد بسط النسبة ومقامها", [nameof(QuorumNumerator)]);
            else if (QuorumNumerator > QuorumDenominator) yield return new("البسط أكبر من المقام", [nameof(QuorumNumerator)]);
        }
        if (QuorumType == "fixed" && QuorumFixedCount is null) yield return new("حدد العدد الثابت للنصاب", [nameof(QuorumFixedCount)]);
        if (Escalation is not null)
        {
            var results = new List<ValidationResult>();
            Validator.TryValidateObject(Escalation, new ValidationContext(Escalation), results, true);
            foreach (var l in Escalation.Levels ?? []) Validator.TryValidateObject(l, new ValidationContext(l), results, true);
            foreach (var r in results) yield return r;
        }
    }

    public QuorumRule ToQuorum() => new()
    {
        Type = QuorumType, Numerator = QuorumNumerator ?? 2, Denominator = QuorumDenominator ?? 3, FixedCount = QuorumFixedCount,
        ChairRequired = ChairRequired, CountRemote = CountRemote, CountProxies = AllowProxy, AllowOverride = AllowOverride,
    };

    public MinutesWorkflow ToMinutesWorkflow() => new()
    {
        SecretaryReview = MinutesSecretaryReview ?? false,
        ChairReview = MinutesChairReview ?? true,
        MemberReview = MinutesMemberReview ?? true,
        MemberReviewDays = MinutesReviewDays,
        Signatures = MinutesSignatures ?? (SignoffRequired ? "present_members" : "none"),
        SignatureFallback = MinutesSignatureFallback ?? "attestation",
        FinalApproval = MinutesFinalApproval ?? "chair",
        AmendmentReapproval = MinutesAmendmentReapproval ?? "full",
        TemplateCode = string.IsNullOrWhiteSpace(MinutesTemplateCode) ? "standard" : MinutesTemplateCode,
    };

    public VotingRule ToVoting() => new() { Threshold = VotingThreshold, Abstentions = Abstentions ?? "excluded", TieBreaker = TieBreaker, SecretAllowed = SecretAllowed };
}

/// <summary>تهيئة التنبيه والتصعيد في السياسة؛ إن لم تُرسل يُحتفظ بقاعدة الإصدار السابق.</summary>
public sealed record EscalationRequest(
    bool WorkingDays,
    [property: Range(1, 60)] int RepeatOverdueEveryDays,
    [property: Range(1, 30)] int ReviewOverdueDays,
    [property: Required, MinLength(1), MaxLength(8)] List<EscalationLevelRequest> Levels) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (l, i) in Levels.Select((l, i) => (l, i)))
        {
            if (!EscalationRule.Triggers.Contains(l.Trigger)) yield return new($"المحفز غير معروف: {l.Trigger}", [$"escalation.levels[{i}].trigger"]);
            if (l.Notify.Length == 0 || l.Notify.Any(n => !EscalationRule.Recipients.Contains(n)))
                yield return new("حدد متلقين صالحين للمستوى", [$"escalation.levels[{i}].notify"]);
            if (l.Trigger == "due_soon" ? l.OffsetDays >= 0 : l.OffsetDays < 0)
                yield return new("التنبيه قبل الاستحقاق بإزاحة سالبة، والتصعيد بعد التأخر بإزاحة صفر أو أكثر", [$"escalation.levels[{i}].offsetDays"]);
        }
        if (Levels.Select(l => l.Level).Distinct().Count() != Levels.Count) yield return new("أرقام المستويات مكررة", ["escalation.levels"]);
        var overdue = Levels.Where(l => l.Trigger != "due_soon").OrderBy(l => l.Level).ToList();
        for (var i = 1; i < overdue.Count; i++)
            if (overdue[i].OffsetDays <= overdue[i - 1].OffsetDays) yield return new("كل مستوى تصعيد أعلى يبدأ بعد سابقه", ["escalation.levels"]);
    }

    public EscalationRule ToRule() => new()
    {
        WorkingDays = WorkingDays, RepeatOverdueEveryDays = RepeatOverdueEveryDays, ReviewOverdueDays = ReviewOverdueDays,
        Levels = Levels.OrderBy(l => l.Level).Select(l => new EscalationLevel(l.Level, l.Trigger, l.OffsetDays, l.Notify)).ToList(),
    };
}

public sealed record EscalationLevelRequest([property: Range(0, 10)] int Level, [property: Required] string Trigger, [property: Range(-30, 365)] int OffsetDays, [property: Required] string[] Notify);

internal static class PolicyEndpoints
{
    public static readonly QuorumRule DefaultQuorum = new();
    public static readonly VotingRule DefaultVoting = new();

    public static async Task InsertDefaultAsync(ISql tx, Guid bodyId, DateOnly from, Guid by, CancellationToken ct) =>
        await InsertAsync(tx, bodyId, 1, from, DefaultQuorum, DefaultVoting, new MinutesWorkflow(), 3, "secretary", false, 1, true, true, by, ct, EscalationRule.Default);

    public static Task InsertAsync(ISql tx, Guid bodyId, int version, DateOnly from, QuorumRule q, VotingRule v, MinutesWorkflow w, int reviewDays,
        string extensionApprover, bool allowProxy, int maxProxies, bool allowRemote, bool allowCirculation, Guid by, CancellationToken ct, EscalationRule escalation) =>
        tx.ExecuteAsync("""
            INSERT INTO bodies.body_policies (id, body_id, version, effective_from, quorum_rule, voting_rule, signoff_rule, escalation_rule,
              minutes_review_days, extension_approver, allow_proxy, allow_remote_attendance, allow_circulation, created_by, minutes_workflow)
            VALUES (@id, @bodyId, @version, @from, @q::jsonb, @v::jsonb, @s::jsonb, @e::jsonb, @reviewDays, @extensionApprover, @allowProxy, @allowRemote, @allowCirculation, @by, @w::jsonb)
            """, new
        {
            id = Guid.CreateVersion7(), bodyId, version, from,
            q = JsonSerializer.Serialize(q, PolicyJson.Options), v = JsonSerializer.Serialize(v, PolicyJson.Options),
            w = (w with { MemberReviewDays = reviewDays }).ToJson(),
            s = JsonSerializer.Serialize(new { required = w.Signatures != "none", signers = "present_voting_members_except_chair", methods = new[] { "attestation", "wet_signature_evidence" }, maxProxiesPerMember = maxProxies }, PolicyJson.Options),
            e = escalation.ToJson(), reviewDays, extensionApprover, allowProxy, allowRemote, allowCirculation, by,
        }, ct);


    public static async Task<IResult> Get(Guid id, Db db, IAccessService access, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyView, Scope.Body(id), ct);
        var versions = await db.QueryAsync(BodyPolicyQuery.Select + "\nWHERE p.body_id = @id ORDER BY p.version DESC", new { id }, BodyPolicyQuery.Map, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        return Results.Ok(new { current = versions.FirstOrDefault(v => v.EffectiveFrom <= today) ?? versions.LastOrDefault(), versions });
    }

    /// <summary>تعديل السياسة يُنشئ إصدارًا جديدًا ساريًا من تاريخه؛ الاجتماعات المجدولة تحتفظ بإصدارها المثبت (BRL-05).</summary>
    public static async Task<IResult> Update(Guid id, PolicyRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.BodyPolicy, Scope.Body(id), ct);
        var version = await db.InTransactionAsync(async tx =>
        {
            var status = await tx.ScalarAsync<string>("SELECT status FROM bodies.bodies WHERE id = @id FOR UPDATE", new { id }, ct) ?? throw new NotFoundException();
            if (status is "dissolved" or "archived") throw new ConflictException("invalid-transition", "لا تُعدل سياسة جهة منتهية");
            var next = await tx.ScalarAsync<int>("SELECT COALESCE(max(version), 0) + 1 FROM bodies.body_policies WHERE body_id = @id", new { id }, ct);
            var escalation = req.Escalation?.ToRule() ?? EscalationRule.Parse(await tx.ScalarAsync<string>(
                "SELECT escalation_rule::text FROM bodies.body_policies WHERE body_id = @id ORDER BY version DESC LIMIT 1", new { id }, ct));
            await InsertAsync(tx, id, next, req.EffectiveFrom!.Value, req.ToQuorum(), req.ToVoting(), req.ToMinutesWorkflow(), req.MinutesReviewDays,
                req.ExtensionApprover, req.AllowProxy, req.MaxProxiesPerMember ?? 1, req.AllowRemoteAttendance, req.AllowCirculation, me.UserId, ct, escalation);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.policy_changed", "body", id.ToString(), After: new { version = next, req.EffectiveFrom, quorum = req.ToQuorum(), voting = req.ToVoting(), minutes = req.ToMinutesWorkflow(), escalation = req.Escalation, req.Reason }), ct);
            return next;
        }, ct);
        return Results.Ok(new { version });
    }
}
