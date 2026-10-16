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
               p.allow_circulation, p.created_at, u.display_name_ar AS created_by_name
        FROM bodies.body_policies p LEFT JOIN iam.users u ON u.id = p.created_by
        """;

    internal static BodyPolicy Map(DbDataReader r)
    {
        var signoff = PolicyJson.Signoff(r.Get<string>("signoff_rule"));
        return new BodyPolicy(r.Get<Guid>("id"), r.Get<Guid>("body_id"), r.Get<int>("version"), r.Get<DateOnly>("effective_from"),
            PolicyJson.Quorum(r.Get<string>("quorum_rule")), PolicyJson.Voting(r.Get<string>("voting_rule")), signoff.Required,
            r.Get<int>("minutes_review_days"), r.Get<string>("extension_approver"), r.Get<bool>("allow_proxy"), signoff.MaxProxies,
            r.Get<bool>("allow_remote_attendance"), r.Get<bool>("allow_circulation"), r.Get<DateTime>("created_at"), r.Str("created_by_name"));
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
    [property: Required(ErrorMessage = "سبب تعديل السياسة مطلوب"), MinLength(5), MaxLength(1000)] string Reason) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (QuorumType == "fraction")
        {
            if (QuorumNumerator is null || QuorumDenominator is null) yield return new("حدد بسط النسبة ومقامها", [nameof(QuorumNumerator)]);
            else if (QuorumNumerator > QuorumDenominator) yield return new("البسط أكبر من المقام", [nameof(QuorumNumerator)]);
        }
        if (QuorumType == "fixed" && QuorumFixedCount is null) yield return new("حدد العدد الثابت للنصاب", [nameof(QuorumFixedCount)]);
    }

    public QuorumRule ToQuorum() => new()
    {
        Type = QuorumType, Numerator = QuorumNumerator ?? 2, Denominator = QuorumDenominator ?? 3, FixedCount = QuorumFixedCount,
        ChairRequired = ChairRequired, CountRemote = CountRemote, CountProxies = AllowProxy, AllowOverride = AllowOverride,
    };

    public VotingRule ToVoting() => new() { Threshold = VotingThreshold, Abstentions = Abstentions ?? "excluded", TieBreaker = TieBreaker, SecretAllowed = SecretAllowed };
}

internal static class PolicyEndpoints
{
    public static readonly QuorumRule DefaultQuorum = new();
    public static readonly VotingRule DefaultVoting = new();

    public static async Task InsertDefaultAsync(ISql tx, Guid bodyId, DateOnly from, Guid by, CancellationToken ct) =>
        await InsertAsync(tx, bodyId, 1, from, DefaultQuorum, DefaultVoting, false, 3, "secretary", false, 1, true, true, by, ct);

    public static Task InsertAsync(ISql tx, Guid bodyId, int version, DateOnly from, QuorumRule q, VotingRule v, bool signoffRequired, int reviewDays,
        string extensionApprover, bool allowProxy, int maxProxies, bool allowRemote, bool allowCirculation, Guid by, CancellationToken ct) =>
        tx.ExecuteAsync("""
            INSERT INTO bodies.body_policies (id, body_id, version, effective_from, quorum_rule, voting_rule, signoff_rule, escalation_rule,
              minutes_review_days, extension_approver, allow_proxy, allow_remote_attendance, allow_circulation, created_by)
            VALUES (@id, @bodyId, @version, @from, @q::jsonb, @v::jsonb, @s::jsonb, @e::jsonb, @reviewDays, @extensionApprover, @allowProxy, @allowRemote, @allowCirculation, @by)
            """, new
        {
            id = Guid.CreateVersion7(), bodyId, version, from,
            q = JsonSerializer.Serialize(q, PolicyJson.Options), v = JsonSerializer.Serialize(v, PolicyJson.Options),
            s = JsonSerializer.Serialize(new { required = signoffRequired, signers = "present_voting_members_except_chair", methods = new[] { "attestation", "wet_signature_evidence" }, maxProxiesPerMember = maxProxies }, PolicyJson.Options),
            e = DefaultEscalationJson, reviewDays, extensionApprover, allowProxy, allowRemote, allowCirculation, by,
        }, ct);

    private const string DefaultEscalationJson = """
        {"workingDays":true,"repeatOverdueEveryDays":7,"levels":[
          {"level":0,"trigger":"due_soon","offsetDays":-3,"notify":["assignee","accountable"]},
          {"level":1,"trigger":"overdue","offsetDays":0,"notify":["assignee","accountable","secretary"]},
          {"level":2,"trigger":"overdue_l2","offsetDays":7,"notify":["assignee","accountable","secretary","assignee_org_unit_manager"]},
          {"level":3,"trigger":"overdue_l3","offsetDays":14,"notify":["secretary","chair"]}]}
        """;

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
            await InsertAsync(tx, id, next, req.EffectiveFrom!.Value, req.ToQuorum(), req.ToVoting(), req.SignoffRequired, req.MinutesReviewDays,
                req.ExtensionApprover, req.AllowProxy, req.MaxProxiesPerMember ?? 1, req.AllowRemoteAttendance, req.AllowCirculation, me.UserId, ct);
            await audit.WriteAsync(tx, new AuditEntry("BOD", "body.policy_changed", "body", id.ToString(), After: new { version = next, req.EffectiveFrom, quorum = req.ToQuorum(), voting = req.ToVoting(), req.Reason }), ct);
            return next;
        }, ct);
        return Results.Ok(new { version });
    }
}
