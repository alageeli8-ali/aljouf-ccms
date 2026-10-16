using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;

namespace Ccms.Modules.Tasks;

public sealed record DeliverableDecision([property: Required] Guid? DeliverableId, [property: Required, OneOf("accepted", "rejected")] string Decision,
    [property: MaxLength(2000)] string? Note);

public sealed record ActionRequest(
    [property: MaxLength(2000)] string? Reason,
    [property: MaxLength(4000)] string? Note,
    [property: MaxLength(50)] List<DeliverableDecision>? DeliverableDecisions);

/// <summary>محرك انتقالات المهمة: يقرأ الانتقالات المهيأة من القاعدة، ويتحقق من الصلاحية وصفة المستخدم، ثم يطبق القيود الثابتة
/// التي لا تُهيأ: لا اكتمال برفع ملف، والتحقق لغير المنفذ، والمخرجات الإلزامية مقبولة قبل الإغلاق.</summary>
internal sealed class TaskEngine(INotifier notifier, IAuditWriter audit, IFeatureService features, INumberingService numbering, IClock clock,
    IResolutionExecutionHook resolutions)
{
    public async Task<string> TransitionAsync(ISql tx, TaskContext ctx, string action, ActionRequest req, Guid by, string channel, CancellationToken ct)
    {
        var t = ctx.Row;
        var transition = (await TaskData.TransitionsAsync(tx, ct)).FirstOrDefault(x => x.From == t.Status && x.Action == action && x.Active)
            ?? throw new ConflictException("invalid-transition", "الإجراء غير متاح في حالة المهمة الحالية");
        if (!ctx.Matches(transition))
            throw new ForbiddenException(permission: transition.Permission);
        var reason = (req.Reason ?? "").Trim();
        if (transition.RequiresReason && reason.Length < 3) throw AppValidationException.For("reason", "السبب مطلوب لهذا الإجراء");
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        var portalEnabled = await features.IsEnabledAsync(FeatureKeys.ExternalPortalEnabled, ct);

        // ---- قيود ثابتة لا تُهيأ ----
        switch (action)
        {
            case "assign":
                if (t.AccountableUserId is null) throw AppValidationException.For("accountableUserId", "حدد مسؤول المتابعة قبل الإسناد");
                break;
            case "submit":
                if (note is null || note.Length < 5) throw AppValidationException.For("note", "اكتب ملخص ما أُنجز عند رفع المخرج للمراجعة");
                await RequireDeliverablesSubmittedAsync(tx, t.Id, ct);
                break;
            case "complete":
                if (t.VerificationRequired)
                    throw new ConflictException("verification-required", "هذه المهمة تتطلب تحققًا؛ ارفع المخرج للمراجعة بدل إقرار الإنجاز");
                if (await tx.ScalarAsync<long>("SELECT count(*) FROM tasks.task_deliverables WHERE task_id = @id AND required AND status <> 'removed'", new { id = t.Id }, ct) > 0)
                    throw new ConflictException("verification-required", "للمهمة مخرجات إلزامية تتطلب قبولًا من المتحقق");
                if (note is null || note.Length < 5) throw AppValidationException.For("note", "اكتب ما يثبت الإنجاز");
                break;
            case "verify" or "return":
                // فصل المهام: لا يتحقق من المهمة منفذها، ولا يتحقق ممثل الجهة الخارجية عبر البوابة
                if (ctx.IsExecutor || ctx.PortalOnly) throw new AccessDeniedException("separation-of-duties", "لا يتحقق من إنجاز المهمة منفذها");
                break;
        }

        var to = transition.To;
        var updates = new List<string> { "status = @to", "status_changed_at = now()", "updated_by = @by" };
        string? refNo = null;
        switch (action)
        {
            case "assign":
                updates.Add("assigned_at = now()");
                if (t.ReferenceNo is null)
                {
                    var year = clock.TodayLocal.Year;
                    refNo = $"{t.BodyCode}-T-{year}-{await numbering.NextAsync(tx, $"task:{t.BodyCode}", year, ct):0000}";
                    updates.Add("reference_no = @refNo");
                }
                break;
            case "accept": updates.Add("accepted_at = now()"); break;
            case "submit": updates.Add("submitted_at = now()"); break;
            case "complete": updates.AddRange(["completed_at = now()", "progress_pct = 100", "completion_method = 'self_declared'"]); break;
            case "verify": updates.AddRange(["completed_at = now()", "progress_pct = 100", "completion_method = 'verified'", "verified_at = now()", "verified_by = @by"]); break;
            case "reopen": updates.AddRange(["completed_at = NULL", "verified_at = NULL", "verified_by = NULL", "completion_method = NULL"]); break;
            case "cancel": updates.Add("cancelled_at = now()"); break;
        }
        var startsWork = transition.To == "in_progress" || (await TaskData.StatusesAsync(tx, ct)).Any(s => s.Code == to && s.Category == "active");
        if (startsWork) updates.Add("started_at = COALESCE(started_at, now())");
        if (reason.Length > 0) updates.Add("status_reason = @reason");
        await tx.ExecuteAsync($"UPDATE tasks.tasks SET {string.Join(", ", updates)} WHERE id = @id", new { id = t.Id, to, by, refNo, reason }, ct);

        if (action is "verify" or "return") await ReviewAsync(tx, t, action, req, reason, note, by, ct);
        if (action == "cancel")
            await tx.ExecuteAsync("UPDATE tasks.extension_requests SET status = 'withdrawn', decided_at = now(), decision_note = 'أُلغيت المهمة' WHERE task_id = @id AND status = 'pending'",
                new { id = t.Id }, ct);

        await TaskData.LogAsync(tx, t.Id, by, "status_change", reason.Length > 0 ? reason : note, new { status = t.Status }, new { status = to, action },
            progress: action is "verify" or "complete" ? 100 : null, channel: channel, visibility: "shared", ct: ct);
        await audit.WriteAsync(tx, new AuditEntry("TSK", "task." + action, "task", t.Id.ToString(), Before: new { status = t.Status },
            After: new { status = to, reason = reason.Length > 0 ? reason : null, note, channel, refNo }), ct);

        var row = t with { ReferenceNo = t.ReferenceNo ?? refNo, Status = to };
        await NotifyAsync(tx, row, action, reason.Length > 0 ? reason : note, by, portalEnabled, ct);
        if (t.ResolutionId is { } rid)
        {
            if (startsWork) await resolutions.TaskStartedAsync(tx, rid, by, ct);
            if (to == "completed"
                && await tx.ScalarAsync<long>("SELECT count(*) FROM tasks.tasks WHERE resolution_id = @rid AND status NOT IN ('completed','cancelled')", new { rid }, ct) == 0)
                await resolutions.TasksCompletedAsync(tx, rid, by, ct);
        }
        return to;
    }

    private static async Task RequireDeliverablesSubmittedAsync(ISql tx, Guid taskId, CancellationToken ct)
    {
        var missing = await tx.QueryAsync("""
            SELECT title FROM tasks.task_deliverables WHERE task_id = @taskId AND required AND status IN ('pending','rejected') ORDER BY position
            """, new { taskId }, r => r.GetString(0), ct);
        if (missing.Count > 0)
            throw new ConflictException("deliverables-missing", "قدّم المخرجات الإلزامية قبل رفع المهمة للمراجعة: " + string.Join("، ", missing),
                new Dictionary<string, object?> { ["missing"] = missing });
    }

    /// <summary>قرار التحقق لكل مخرج: القبول يلزم كل المخرجات الإلزامية المقدمة؛ والإعادة تحدد ما يُستكمل.</summary>
    private async Task ReviewAsync(ISql tx, TaskRow t, string action, ActionRequest req, string reason, string? note, Guid by, CancellationToken ct)
    {
        var deliverables = await tx.QueryAsync("SELECT id, title, required, status FROM tasks.task_deliverables WHERE task_id = @id AND status <> 'removed' FOR UPDATE",
            new { id = t.Id }, r => (Id: r.GetGuid(0), Title: r.GetString(1), Required: r.GetBoolean(2), Status: r.GetString(3)), ct);
        var decisions = (req.DeliverableDecisions ?? []).ToDictionary(d => d.DeliverableId!.Value);
        if (decisions.Keys.Any(k => deliverables.All(d => d.Id != k))) throw AppValidationException.For("deliverableDecisions", "مخرج لا يتبع المهمة");
        foreach (var d in decisions.Values.Where(d => d.Decision == "rejected"))
            if (string.IsNullOrWhiteSpace(d.Note)) throw AppValidationException.For("deliverableDecisions", "اذكر سبب رفض المخرج");
        if (action == "verify")
        {
            foreach (var d in deliverables.Where(d => d.Required))
            {
                var decision = decisions.TryGetValue(d.Id, out var x) ? x.Decision : d.Status == "accepted" ? "accepted" : null;
                if (decision != "accepted")
                    throw new ConflictException("deliverable-not-accepted", $"لا تُغلق المهمة قبل قبول المخرج الإلزامي: {d.Title}");
            }
        }
        else if (decisions.Count == 0 && deliverables.Count > 0)
        {
            // الإعادة دون قرارات تفصيلية تعيد المخرجات المقدمة كلها للاستكمال
            foreach (var d in deliverables.Where(d => d.Status == "submitted"))
                decisions[d.Id] = new DeliverableDecision(d.Id, "rejected", reason);
        }
        foreach (var d in decisions.Values)
            await tx.ExecuteAsync("""
                UPDATE tasks.task_deliverables SET status = @Decision, review_note = @Note, reviewed_by = @by, reviewed_at = now() WHERE id = @id
                """, new { id = d.DeliverableId, d.Decision, d.Note, by }, ct);
        await tx.ExecuteAsync("""
            INSERT INTO tasks.task_reviews (id, task_id, decision, note, deliverable_decisions, reviewer_user_id)
            VALUES (@rid, @taskId, @decision, @note, @decisions::jsonb, @by)
            """, new
        {
            rid = Guid.CreateVersion7(), taskId = t.Id, decision = action == "verify" ? "accepted" : "returned", note = reason.Length > 0 ? reason : note,
            decisions = Json.Serialize(decisions.Values), by,
        }, ct);
    }

    private async Task NotifyAsync(ISql tx, TaskRow t, string action, string? text, Guid by, bool portalEnabled, CancellationToken ct)
    {
        var executors = await TaskData.ExecutorUsersAsync(tx, t, portalEnabled, ct);
        switch (action)
        {
            case "assign":
                await TaskData.NotifyAsync(tx, notifier, [.. executors.Cast<Guid?>(), t.AccountableUserId], t, "task_assigned", "أُسندت إليك مهمة", "Task assigned", "task_assigned", except: by, ct: ct);
                break;
            case "reject":
                await TaskData.NotifyAsync(tx, notifier, [t.AccountableUserId, t.CreatedBy], t, "task_declined", "اعتذر المكلف عن المهمة", "Task declined", "task_declined:" + Guid.NewGuid(), "warning", by, ct: ct);
                break;
            case "submit":
                // تنبيه عند رفع المخرج للمراجعة: المتحقق المسمى، ومسؤول المتابعة، وأمين السر
                var reviewers = new List<Guid?> { t.VerifierUserId, t.AccountableUserId };
                reviewers.AddRange((await TaskData.BodyRoleUsersAsync(tx, t.BodyId, ["secretary"], ct)).Cast<Guid?>());
                await TaskData.NotifyAsync(tx, notifier, reviewers, t, "task_submitted", "مخرج مرفوع للمراجعة", "Deliverable submitted for review", "task_submitted:" + Guid.NewGuid(), "warning", by, ct: ct);
                break;
            case "return":
                await TaskData.NotifyAsync(tx, notifier, [.. executors.Cast<Guid?>(), t.AccountableUserId], t, "task_returned", "أُعيدت المهمة للاستكمال", "Task returned", "task_returned:" + Guid.NewGuid(), "warning", by, ct: ct);
                break;
            case "verify" or "complete":
                await TaskData.NotifyAsync(tx, notifier, [.. executors.Cast<Guid?>(), t.AccountableUserId, t.CreatedBy], t, "task_completed", "اكتملت المهمة", "Task completed", "task_completed:" + Guid.NewGuid(), except: by, ct: ct);
                break;
            case "cancel":
                await TaskData.NotifyAsync(tx, notifier, [.. executors.Cast<Guid?>(), t.AccountableUserId], t, "task_cancelled", "أُلغيت المهمة", "Task cancelled", "task_cancelled", except: by, ct: ct);
                break;
            case "accept":
                await TaskData.NotifyAsync(tx, notifier, [t.AccountableUserId], t, "task_accepted", "أكد المكلف استلام المهمة", "Task acknowledged", "task_accepted", except: by, ct: ct);
                break;
        }
    }
}
