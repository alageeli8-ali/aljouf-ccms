using System.ComponentModel.DataAnnotations;
using Ccms.BuildingBlocks.Data;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ccms.Modules.Tasks;

internal sealed record Recipient(Guid UserId, string Name, string Role, string? Unit);

public sealed record EscalationSummary(DateOnly AsOf, int Evaluated, int Reminders, int Escalations, int ReviewOverdue, int Notifications);

/// <summary>التنبيه قبل الاستحقاق، والتصعيد عند التأخر بمستويات قاعدة الجهة، وتنبيه تأخر المراجعة.
/// كل تنبيه أو تصعيد يُسجل مرة واحدة لكل (مهمة، نوع، مستوى، فترة) مع المتلقين وصفاتهم وجهاتهم.</summary>
internal sealed class EscalationService(Db db, IBodyPolicyQuery policies, INotifier notifier, IAuditWriter audit, IFeatureService features)
{
    private static readonly Dictionary<string, string> RoleNames = new()
    {
        ["assignee"] = "المنفذ", ["accountable"] = "مسؤول المتابعة", ["verifier"] = "المتحقق", ["secretary"] = "أمين السر", ["chair"] = "الرئيس",
        ["vice_chair"] = "نائب الرئيس", ["assignee_org_unit_manager"] = "مدير الإدارة المنفذة", ["governance_officer"] = "مسؤول المجالس", ["executive"] = "الإدارة العليا",
    };

    public async Task<EscalationSummary> RunAsync(DateOnly asOf, CancellationToken ct)
    {
        var portal = await features.IsEnabledAsync(FeatureKeys.ExternalPortalEnabled, ct);
        var ids = await db.QueryAsync("""
            SELECT t.id FROM tasks.tasks t JOIN tasks.workflow_statuses ws ON ws.code = t.status
            WHERE (ws.category IN ('open','active') AND t.due_date IS NOT NULL) OR ws.category = 'review'
            ORDER BY t.due_date NULLS LAST
            """, null, r => r.GetGuid(0), ct);
        int reminders = 0, escalations = 0, review = 0, notes = 0;
        foreach (var id in ids)
        {
            var (r, e, v, n) = await db.InTransactionAsync(tx => EvaluateAsync(tx, id, asOf, portal, ct), ct);
            reminders += r; escalations += e; review += v; notes += n;
        }
        var summary = new EscalationSummary(asOf, ids.Count, reminders, escalations, review, notes);
        if (reminders + escalations + review > 0)
            await audit.WriteAsync(new AuditEntry("TSK", "task.escalation_run", "system", null, After: summary), ct);
        return summary;
    }

    private async Task<(int, int, int, int)> EvaluateAsync(ISql tx, Guid id, DateOnly asOf, bool portal, CancellationToken ct)
    {
        var t = await TaskData.LoadAsync(tx, id, true, ct);
        var rule = (await policies.GetEffectiveAsync(t.BodyId, asOf, ct))?.EscalationFlow ?? EscalationRule.Default;
        int reminders = 0, notes = 0;
        if (t.Category == "review")
        {
            if (t.SubmittedAt is not { } submitted) return (0, 0, 0, 0);
            var since = DateOnly.FromDateTime(submitted.AddHours(3));
            if (TaskData.DaysBetween(since, asOf, rule.WorkingDays) < rule.ReviewOverdueDays) return (0, 0, 0, 0);
            var created = await RecordAsync(tx, t, "review_overdue", 0, "review_overdue", $"{since:yyyy-MM-dd}", null, null,
                $"لم يُبت في المخرج المرفوع منذ {rule.ReviewOverdueDays} أيام", ["verifier", "accountable", "secretary"], null, portal, ct);
            return created is null ? (0, 0, 0, 0) : (0, 0, 1, created.Value);
        }
        if (t.DueDate is not { } due) return (0, 0, 0, 0);
        var dueKey = $"{due:yyyy-MM-dd}";
        if (asOf <= due)
        {
            // التنبيه قبل الاستحقاق
            foreach (var l in rule.Levels.Where(l => l.Trigger == "due_soon"))
                if (asOf >= TaskData.AddDays(due, l.OffsetDays, rule.WorkingDays))
                {
                    var n = await RecordAsync(tx, t, "reminder", l.Level, l.Trigger, dueKey, due, 0,
                        $"يستحق موعد المهمة في {due:yyyy/MM/dd}", l.Notify, null, portal, ct);
                    if (n is not null) { reminders++; notes += n.Value; }
                }
            return (reminders, 0, 0, notes);
        }
        // التصعيد بعد التأخر: أعلى مستوى بلغته المهمة، ويتكرر بالفاصل المحدد ما دامت متأخرة
        var daysOver = Math.Max(1, TaskData.DaysBetween(due, asOf, rule.WorkingDays));
        var reached = rule.Levels.Where(l => l.Trigger != "due_soon" && daysOver >= Math.Max(l.OffsetDays, 0) && (l.OffsetDays > 0 || asOf > due))
            .OrderByDescending(l => l.Level).FirstOrDefault();
        if (reached is null) return (0, 0, 0, 0);
        var repeat = rule.RepeatOverdueEveryDays > 0 ? (daysOver - Math.Max(reached.OffsetDays, 0)) / rule.RepeatOverdueEveryDays : 0;
        var key = repeat > 0 ? $"{dueKey}:r{repeat}" : dueKey;
        var count = await RecordAsync(tx, t, "escalation", reached.Level, reached.Trigger, key, due, daysOver,
            $"متأخرة {daysOver} يوم{(rule.WorkingDays ? " عمل" : "")} عن موعد {due:yyyy/MM/dd}", reached.Notify, null, portal, ct);
        if (count is null) return (0, 0, 0, 0);
        if (reached.Level > t.EscalationLevel)
            await tx.ExecuteAsync("UPDATE tasks.tasks SET escalation_level = @level, last_escalated_at = now() WHERE id = @id", new { id, level = reached.Level }, ct);
        else
            await tx.ExecuteAsync("UPDATE tasks.tasks SET last_escalated_at = now() WHERE id = @id", new { id }, ct);
        return (0, 1, 0, count.Value);
    }

    /// <summary>يحدد المتلقين بأسمائهم وصفاتهم وجهاتهم، ويسجل التصعيد، ويرسل الإشعارات. يعيد null إن سُجل للفترة نفسها من قبل.</summary>
    public async Task<int?> RecordAsync(ISql tx, TaskRow t, string kind, int level, string trigger, string periodKey, DateOnly? due, int? daysOver, string reason,
        string[] roles, Guid? by, bool portal, CancellationToken ct)
    {
        if (await tx.ScalarAsync<long>("SELECT count(*) FROM tasks.escalations WHERE task_id = @id AND kind = @kind AND level = @level AND period_key = @periodKey",
                new { id = t.Id, kind, level, periodKey }, ct) > 0) return null;
        var recipients = await RecipientsAsync(tx, t, roles, portal, ct);
        var eid = Guid.CreateVersion7();
        await tx.ExecuteAsync("""
            INSERT INTO tasks.escalations (id, task_id, kind, level, trigger, period_key, due_date, days_overdue, reason, recipient_roles, recipients, created_by)
            VALUES (@eid, @taskId, @kind, @level, @trigger, @periodKey, @due, @daysOver, @reason, @roles, @recipients::jsonb, @by)
            """, new { eid, taskId = t.Id, kind, level, trigger, periodKey, due, daysOver, reason, roles, recipients = Json.Serialize(recipients), by }, ct);
        var (type, titleAr, titleEn, severity) = kind switch
        {
            "reminder" => ("task_due_soon", "تنبيه: موعد المهمة يقترب", "Task due soon", "info"),
            "review_overdue" => ("task_review_overdue", "مخرج ينتظر المراجعة", "Review overdue", "warning"),
            "manual" => ("task_escalated", $"تصعيد يدوي (المستوى {level})", $"Manual escalation (level {level})", "critical"),
            _ => ("task_overdue", $"مهمة متأخرة — تصعيد المستوى {level}", $"Task overdue — escalation level {level}", level >= 2 ? "critical" : "warning"),
        };
        await TaskData.NotifyAsync(tx, notifier, recipients.Select(r => (Guid?)r.UserId), t, type, titleAr, titleEn, $"escalation:{eid}", severity, ct: ct);
        await TaskData.LogAsync(tx, t.Id, by, "escalation", reason, null,
            new { kind, level, trigger, recipients = recipients.Select(r => $"{r.Name} ({RoleNames.GetValueOrDefault(r.Role, r.Role)})") }, ct: ct);
        await audit.WriteAsync(tx, new AuditEntry("TSK", kind == "reminder" ? "task.reminder_sent" : "task.escalated", "task", t.Id.ToString(),
            After: new { eid, kind, level, trigger, periodKey, recipients = recipients.Select(r => new { r.UserId, r.Role, r.Unit }) }, ActorUserId: by), ct);
        return recipients.Count;
    }

    private async Task<List<Recipient>> RecipientsAsync(ISql tx, TaskRow t, string[] roles, bool portal, CancellationToken ct)
    {
        var result = new List<Recipient>();
        async Task AddAsync(IEnumerable<Guid> users, string role, string? unitOverride = null)
        {
            foreach (var u in users.Distinct())
            {
                if (result.Any(r => r.UserId == u)) continue;
                var info = await tx.QuerySingleAsync("""
                    SELECT u.display_name_ar, COALESCE(o.name_ar, e.name_ar) FROM iam.users u LEFT JOIN org.org_units o ON o.id = u.org_unit_id
                    LEFT JOIN org.external_entities e ON e.id = u.external_entity_id WHERE u.id = @u AND u.status = 'active'
                    """, new { u }, r => (Name: r.GetString(0), Unit: r.IsDBNull(1) ? null : r.GetString(1)), ct);
                if (info != default) result.Add(new Recipient(u, info.Name, role, unitOverride ?? info.Unit));
            }
        }
        foreach (var role in roles)
        {
            switch (role)
            {
                case "assignee": await AddAsync(await TaskData.ExecutorUsersAsync(tx, t, portal, ct), role); break;
                case "accountable": if (t.AccountableUserId is { } a) await AddAsync([a], role); break;
                case "verifier": if (t.VerifierUserId is { } v) await AddAsync([v], role); break;
                case "secretary" or "chair" or "vice_chair": await AddAsync(await TaskData.BodyRoleUsersAsync(tx, t.BodyId, [role], ct), role); break;
                case "assignee_org_unit_manager":
                    var unit = t.ExecutingOrgUnitId ?? t.AssigneeOrgUnitId;
                    var mu = unit is null ? default : await tx.QuerySingleAsync("SELECT manager_user_id, name_ar FROM org.org_units WHERE id = @unit", new { unit },
                        r => (Manager: r.Uuid("manager_user_id"), Name: r.GetString(1)), ct);
                    if (mu.Manager is { } m) await AddAsync([m], role, mu.Name);
                    break;
                case "governance_officer" or "executive":
                    await AddAsync(await tx.QueryAsync("""
                        SELECT DISTINCT user_id FROM iam.system_role_assignments WHERE role_code = @role AND valid_from <= current_date AND (valid_to IS NULL OR valid_to >= current_date)
                        """, new { role }, r => r.GetGuid(0), ct), role);
                    break;
            }
        }
        return result;
    }
}

internal static class Escalations
{
    public sealed record ManualRequest([property: Required(ErrorMessage = "سبب التصعيد مطلوب"), MinLength(5), MaxLength(2000)] string Reason, string[]? Notify);
    public sealed record AckRequest([property: Required(ErrorMessage = "اذكر الإجراء المتخذ"), MinLength(3), MaxLength(2000)] string Note);
    public sealed record RunRequest(DateOnly? AsOf);

    /// <summary>التصعيد اليدوي بسبب: يرفع المستوى درجة، ويُشعر المتلقين المحددين أو متلقي المستوى التالي في القاعدة.</summary>
    public static async Task<IResult> Manual(Guid id, ManualRequest req, Db db, IAccessService access, ICurrentUser me, EscalationService svc, IBodyPolicyQuery policies,
        IFeatureService features, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.TaskEscalate, Scope.Task(id), ct);
        if (req.Notify is { } n && n.Any(x => !EscalationRule.Recipients.Contains(x))) throw AppValidationException.For("notify", "متلقٍ غير معروف");
        var portal = await features.IsEnabledAsync(FeatureKeys.ExternalPortalEnabled, ct);
        var result = await db.InTransactionAsync(async tx =>
        {
            var t = await TaskData.LoadAsync(tx, id, true, ct);
            if (t.Category is "done" or "closed") throw new ConflictException("task-closed", "لا تُصعد مهمة مكتملة أو ملغاة");
            var rule = (await policies.GetEffectiveAsync(t.BodyId, clock.TodayLocal, ct))?.EscalationFlow ?? EscalationRule.Default;
            var level = Math.Min(10, t.EscalationLevel + 1);
            var roles = req.Notify is { Length: > 0 } ? req.Notify
                : rule.Levels.Where(l => l.Trigger != "due_soon" && l.Level >= level).OrderBy(l => l.Level).FirstOrDefault()?.Notify ?? ["secretary", "chair"];
            var count = await svc.RecordAsync(tx, t, "manual", level, "manual", Guid.CreateVersion7().ToString("N"), t.DueDate, null, req.Reason, roles, me.UserId, portal, ct) ?? 0;
            await tx.ExecuteAsync("UPDATE tasks.tasks SET escalation_level = @level, last_escalated_at = now() WHERE id = @id", new { id, level }, ct);
            return new { level, recipients = count };
        }, ct);
        return Results.Ok(result);
    }

    /// <summary>إقرار المتلقي بالتصعيد مع الإجراء المتخذ؛ يُحفظ في سجل التصعيد.</summary>
    public static async Task<IResult> Acknowledge(Guid eid, AckRequest req, Db db, IAccessService access, ICurrentUser me, IAuditWriter audit, CancellationToken ct)
    {
        var e = await db.QuerySingleAsync("SELECT task_id, recipients::text, acknowledged_at FROM tasks.escalations WHERE id = @eid", new { eid },
            r => (Task: r.GetGuid(0), Recipients: r.GetString(1), Ack: r.Time("acknowledged_at")), ct);
        if (e == default) throw new NotFoundException();
        await access.DemandAsync(Permissions.TaskView, Scope.Task(e.Task), ct);
        if (!e.Recipients.Contains(me.UserId.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new AccessDeniedException("not-recipient", "يقر بالتصعيد من تلقاه");
        if (e.Ack is not null) throw new ConflictException("already-acknowledged", "أُقر بهذا التصعيد مسبقًا");
        await db.ExecuteAsync("UPDATE tasks.escalations SET acknowledged_by = @me, acknowledged_at = now(), ack_note = @Note WHERE id = @eid", new { eid, me = me.UserId, req.Note }, ct);
        await audit.WriteAsync(new AuditEntry("TSK", "task.escalation_acknowledged", "task", e.Task.ToString(), After: new { eid, req.Note }), ct);
        return Results.NoContent();
    }

    /// <summary>تشغيل التقييم فورًا (يعمل أيضًا آليًا كل ساعة). تحديد تاريخ التقييم مسموح خارج بيئة الإنتاج فقط لاختبار المستويات.</summary>
    public static async Task<IResult> Run(RunRequest req, IAccessService access, EscalationService svc, IHostEnvironment env, IClock clock, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.TaskWorkflowManage, Scope.System, ct);
        if (req.AsOf is not null && env.IsProduction()) throw AppValidationException.For("asOf", "تحديد تاريخ التقييم غير مسموح في الإنتاج");
        return Results.Ok(await svc.RunAsync(req.AsOf ?? clock.TodayLocal, ct));
    }
}

internal sealed class EscalationWorker(IServiceScopeFactory scopes, IClock clock, ILogger<EscalationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var s = await scope.ServiceProvider.GetRequiredService<EscalationService>().RunAsync(clock.TodayLocal, stoppingToken);
                if (s.Reminders + s.Escalations + s.ReviewOverdue > 0)
                    log.LogInformation("تقييم التصعيد: {Reminders} تنبيه، {Escalations} تصعيد، {Review} مراجعة متأخرة", s.Reminders, s.Escalations, s.ReviewOverdue);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "تعذر تقييم تصعيد المهام");
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
