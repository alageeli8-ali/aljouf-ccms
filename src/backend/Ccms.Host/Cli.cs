using Ccms.BuildingBlocks.Data;
using Ccms.Contracts;
using Ccms.Modules.Iam;
using Microsoft.AspNetCore.Identity;

namespace Ccms.Host;

/// <summary>أوامر إدارية: dotnet Ccms.Host.dll &lt;command&gt; [options]
///   migrate                                   تطبيق الترحيلات بحساب المالك (ConnectionStrings:Migrations)
///   bootstrap-admin --email E --name N [--password P]   إنشاء أول مدير نظام
///   seed-sample                               بيانات تجريبية لبيئتي التطوير والاختبار فقط</summary>
internal static class Cli
{
    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        try
        {
            switch (args[0])
            {
                case "migrate":
                    await Startup.MigrateAsync(app);
                    return 0;
                case "bootstrap-admin":
                    {
                        var email = Opt("--email") ?? throw new ArgumentException("--email مطلوب");
                        var name = Opt("--name") ?? throw new ArgumentException("--name مطلوب");
                        await BootstrapAdminAsync(app.Services.GetRequiredService<Db>(), email, name, Opt("--password"));
                        Console.WriteLine($"تم: {email} مدير للنظام");
                        return 0;
                    }
                case "seed-sample":
                    if (app.Environment.IsProduction()) throw new InvalidOperationException("البيانات التجريبية ممنوعة في الإنتاج");
                    await SampleData.SeedAsync(app.Services.GetRequiredService<Db>());
                    Console.WriteLine("تمت إضافة البيانات التجريبية");
                    return 0;
                default:
                    Console.Error.WriteLine($"أمر غير معروف: {args[0]} (migrate | bootstrap-admin | seed-sample)");
                    return 2;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    public static async Task<Guid> BootstrapAdminAsync(Db db, string email, string name, string? password)
    {
        var hasher = new PasswordHasher<object>();
        return await db.InTransactionAsync(async tx =>
        {
            var id = await tx.ScalarAsync<Guid?>("SELECT id FROM iam.users WHERE lower(email) = lower(@email)", new { email }, default)
                ?? Guid.CreateVersion7();
            await tx.ExecuteAsync("""
                INSERT INTO iam.users (id, email, display_name_ar, user_type) VALUES (@id, lower(@email), @name, @type)
                ON CONFLICT (id) DO NOTHING
                """, new { id, email, name, type = password is null ? "internal" : "local_breakglass" }, default);
            await tx.ExecuteAsync("""
                INSERT INTO iam.system_role_assignments (id, user_id, role_code, reason)
                SELECT @rid, @id, 'system_admin', 'تهيئة أول مدير'
                WHERE NOT EXISTS (SELECT 1 FROM iam.system_role_assignments WHERE user_id = @id AND role_code = 'system_admin' AND valid_to IS NULL)
                """, new { rid = Guid.CreateVersion7(), id }, default);
            if (password is not null)
                await tx.ExecuteAsync("""
                    INSERT INTO iam.local_credentials (user_id, password_hash) VALUES (@id, @hash)
                    ON CONFLICT (user_id) DO UPDATE SET password_hash = EXCLUDED.password_hash, failed_attempts = 0, locked_until = NULL
                    """, new { id, hash = hasher.HashPassword(null!, password) }, default);
            await tx.ExecuteAsync("""
                INSERT INTO audit.audit_events (module, action, entity_type, entity_id, after)
                VALUES ('IAM', 'user.bootstrap_admin', 'user', @id::text, jsonb_build_object('email', @email::text))
                """, new { id, email }, default);
            return id;
        });
    }
}

/// <summary>بيانات تجريبية بأسماء وهمية لتجربة الواجهة: مستخدم لكل دور، وجهتان، واجتماع، ومهام.</summary>
internal static class SampleData
{
    public const string Password = "Ccms@Sample2026";

    public static async Task SeedAsync(Db db)
    {
        if (await db.ScalarAsync<long>("SELECT count(*) FROM bodies.bodies", null) > 0) return;
        var hasher = new PasswordHasher<object>();
        var hash = hasher.HashPassword(null!, Password);
        await db.InTransactionAsync(async tx =>
        {
            async Task<Guid> User(string email, string nameAr, string nameEn, string? role = null, Guid? entity = null, Guid? unit = null)
            {
                var id = Guid.CreateVersion7();
                await tx.ExecuteAsync("INSERT INTO iam.users (id, email, display_name_ar, display_name_en, external_entity_id, org_unit_id, user_type) VALUES (@id, @email, @nameAr, @nameEn, @entity, @unit, @type)",
                    new { id, email, nameAr, nameEn, entity, unit, type = entity is null ? "internal" : "external" });
                await tx.ExecuteAsync("INSERT INTO iam.local_credentials (user_id, password_hash) VALUES (@id, @hash)", new { id, hash });
                if (role is not null)
                    await tx.ExecuteAsync("INSERT INTO iam.system_role_assignments (id, user_id, role_code, reason) VALUES (@rid, @id, @role, 'بيانات تجريبية')",
                        new { rid = Guid.CreateVersion7(), id, role });
                return id;
            }

            var admin = await User("admin@sample.jsdo.test", "مدير النظام (تجريبي)", "System Admin (sample)", Roles.SystemAdmin);
            var gov = await User("governance@sample.jsdo.test", "مسؤول المجالس (تجريبي)", "Governance Officer (sample)", Roles.GovernanceOfficer);
            await User("ceo@sample.jsdo.test", "الإدارة العليا (تجريبي)", "Executive (sample)", Roles.Executive);
            await User("auditor@sample.jsdo.test", "المدقق (تجريبي)", "Auditor (sample)", Roles.Auditor);
            var chair = await User("chair@sample.jsdo.test", "رئيس اللجنة (تجريبي)", "Chair (sample)");
            var vice = await User("vice@sample.jsdo.test", "نائب الرئيس (تجريبي)", "Vice Chair (sample)");
            var sec = await User("secretary@sample.jsdo.test", "أمين السر (تجريبي)", "Secretary (sample)");
            var member = await User("member@sample.jsdo.test", "عضو (تجريبي)", "Member (sample)");
            var observer = await User("observer@sample.jsdo.test", "مراقب (تجريبي)", "Observer (sample)");
            var unitId = Guid.CreateVersion7();
            var manager = await User("manager@sample.jsdo.test", "مدير إدارة (تجريبي)", "Department Manager (sample)");
            await tx.ExecuteAsync("INSERT INTO org.org_units (id, code, name_ar, name_en, manager_user_id) VALUES (@unitId, 'IT', 'الإدارة العامة لتقنية المعلومات', 'IT Department', @manager)",
                new { unitId, manager });
            var executor = await User("executor@sample.jsdo.test", "موظف مكلف (تجريبي)", "Task Executor (sample)", unit: unitId);
            var entityId = Guid.CreateVersion7();
            await tx.ExecuteAsync("INSERT INTO org.external_entities (id, name_ar, name_en, entity_type, liaison_user_id) VALUES (@entityId, 'جهة حكومية (تجريبية)', 'Government entity (sample)', 'government', @sec)",
                new { entityId, sec });
            await User("external@sample.jsdo.test", "ممثل جهة خارجية (تجريبي)", "External Rep (sample)", Roles.ExternalRepresentative, entityId);

            var body = Guid.CreateVersion7();
            await tx.ExecuteAsync("""
                INSERT INTO bodies.bodies (id, code, body_type, name_ar, name_en, mandate_ar, classification, status, created_by)
                VALUES (@body, 'DTC', 'committee', 'لجنة التحول الرقمي (تجريبية)', 'Digital Transformation Committee (sample)',
                        'الإشراف على مبادرات التحول الرقمي ومتابعة تنفيذها', 'restricted', 'active', @gov)
                """, new { body, gov });
            await tx.ExecuteAsync("""
                INSERT INTO bodies.body_policies (id, body_id, version, effective_from, quorum_rule, voting_rule, signoff_rule, escalation_rule)
                VALUES (@pid, @body, 1, current_date - 30, '{"type":"majority"}', '{"threshold":"majority_of_votes_cast"}', '{"required":true}', '{"levels":[]}')
                """, new { pid = Guid.CreateVersion7(), body });
            foreach (var (uid, role, voting) in new[] { (chair, "chair", true), (vice, "vice_chair", true), (sec, "secretary", false), (member, "member", true), (observer, "observer", false) })
                await tx.ExecuteAsync("INSERT INTO bodies.body_members (id, body_id, user_id, role, is_voting, start_date) VALUES (@id, @body, @uid, @role, @voting, current_date - 30)",
                    new { id = Guid.CreateVersion7(), body, uid, role, voting });

            var body2 = Guid.CreateVersion7();
            await tx.ExecuteAsync("""
                INSERT INTO bodies.bodies (id, code, body_type, name_ar, name_en, classification, status, created_by, term_end)
                VALUES (@body2, 'URBAN-WT', 'working_team', 'فريق عمل المرصد الحضري (تجريبي)', 'Urban Observatory Working Team (sample)', 'restricted', 'draft', @gov, current_date + 180)
                """, new { body2, gov });

            var meeting = Guid.CreateVersion7();
            await tx.ExecuteAsync("""
                INSERT INTO meetings.meetings (id, body_id, sequence_year, sequence_no, reference_no, title_ar, starts_at, planned_end_at, location, status, created_by)
                VALUES (@meeting, @body, extract(year FROM now())::int, 1, 'DTC-' || extract(year FROM now())::int || '-001', 'الاجتماع الأول للجنة',
                        now() + interval '5 days', now() + interval '5 days 1 hour', 'قاعة الاجتماعات الرئيسية', 'scheduled', @sec)
                """, new { meeting, body, sec });
            await tx.ExecuteAsync("INSERT INTO config.numbering_sequences (scope_key, year, last_value) VALUES ('meeting:DTC', extract(year FROM now())::int, 1)", null);
            await tx.ExecuteAsync("INSERT INTO meetings.agenda_items (id, meeting_id, position, title_ar, item_type) VALUES (@id, @meeting, 1, 'اعتماد خطة التحول الرقمي 2027–2030', 'for_decision')",
                new { id = Guid.CreateVersion7(), meeting });

            var year = DateTime.UtcNow.Year;
            var tasks = new (string Title, string Type, Guid? User, Guid? Unit, Guid? Entity, Guid? Acc, int Days, string Status)[]
            {
                ("إعداد كراسة الشروط لنظام الأرشفة", "user", executor, null, null, null, 14, "in_progress"),
                ("تحديث سياسات أمن المعلومات", "org_unit", null, unitId, null, manager, -5, "assigned"),
                ("تزويد المكتب ببيانات المرصد الحضري", "external_entity", null, null, entityId, sec, 20, "assigned"),
            };
            var n = 0;
            foreach (var t in tasks)
            {
                n++;
                await tx.ExecuteAsync("""
                    INSERT INTO tasks.tasks (id, body_id, reference_no, title_ar, assignee_type, assignee_user_id, assignee_org_unit_id, assignee_external_entity_id,
                      accountable_user_id, original_due_date, due_date, status, progress_pct, created_by)
                    VALUES (@id, @body, @ref, @Title, @Type, @User, @Unit, @Entity, @Acc, current_date + @Days::int, current_date + @Days::int, @Status, @pct, @sec)
                    """, new { id = Guid.CreateVersion7(), body, @ref = $"DTC-T-{year}-{n:0000}", t.Title, t.Type, t.User, t.Unit, t.Entity, t.Acc, t.Days, t.Status,
                    pct = t.Status == "in_progress" ? 40 : 0, sec });
            }
            await tx.ExecuteAsync("INSERT INTO config.numbering_sequences (scope_key, year, last_value) VALUES ('task:DTC', @year, @n)", new { year, n });
        });
    }
}
