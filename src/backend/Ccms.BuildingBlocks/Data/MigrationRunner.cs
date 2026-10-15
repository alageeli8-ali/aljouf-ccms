using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Ccms.BuildingBlocks.Data;

public sealed record MigrationFile(int Version, string Name, string Path, string Sql, string Checksum);

public sealed record MigrationResult(IReadOnlyList<MigrationFile> Applied, IReadOnlyList<int> AlreadyApplied);

/// <summary>مشغّل ترحيلات SQL مرقّمة (V001__name.sql). كل ملف في معاملة مستقلة، مع قفل استشاري يمنع التشغيل المتزامن،
/// وبصمة SHA-256 تمنع تعديل ترحيل طُبق سابقًا (ADR-015).</summary>
public sealed partial class MigrationRunner(DbDataSource dataSource, ILogger<MigrationRunner> logger)
{
    private const long LockKey = 7_231_001;

    [GeneratedRegex(@"^V(\d{3,})__([A-Za-z0-9_]+)\.sql$")]
    private static partial Regex FileName();

    public static IReadOnlyList<MigrationFile> Discover(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"مجلد الترحيلات غير موجود: {directory}");
        var files = new List<MigrationFile>();
        foreach (var path in Directory.GetFiles(directory, "*.sql"))
        {
            var m = FileName().Match(System.IO.Path.GetFileName(path));
            if (!m.Success) throw new InvalidOperationException($"اسم ترحيل غير صالح: {System.IO.Path.GetFileName(path)}");
            var sql = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
            var checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            files.Add(new MigrationFile(int.Parse(m.Groups[1].Value), m.Groups[2].Value, path, sql, checksum));
        }
        var dup = files.GroupBy(f => f.Version).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidOperationException($"رقم ترحيل مكرر: {dup.Key}");
        return files.OrderBy(f => f.Version).ToList();
    }

    public async Task<MigrationResult> ApplyAsync(string directory, CancellationToken ct = default)
    {
        var files = Discover(directory);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await Exec(conn, null, """
            CREATE TABLE IF NOT EXISTS public.schema_migrations (
              version    int PRIMARY KEY,
              name       text NOT NULL,
              checksum   text NOT NULL,
              applied_at timestamptz NOT NULL DEFAULT now(),
              applied_by text NOT NULL DEFAULT current_user
            )
            """, ct);
        await Exec(conn, null, $"SELECT pg_advisory_lock({LockKey})", ct);
        try
        {
            var applied = new Dictionary<int, string>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT version, checksum FROM public.schema_migrations ORDER BY version";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) applied[r.GetInt32(0)] = r.GetString(1);
            }

            var done = new List<MigrationFile>();
            foreach (var f in files)
            {
                if (applied.TryGetValue(f.Version, out var checksum))
                {
                    if (checksum != f.Checksum)
                        throw new InvalidOperationException(
                            $"الترحيل V{f.Version:000} ({f.Name}) عُدّل بعد تطبيقه. لا تعدّل ترحيلًا مطبقًا؛ أضف ترحيلًا جديدًا.");
                    continue;
                }
                logger.LogInformation("تطبيق الترحيل V{Version:000} {Name}", f.Version, f.Name);
                await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                await Exec(conn, tx, f.Sql, ct);
                await using (var ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO public.schema_migrations (version, name, checksum) VALUES (@v, @n, @c)";
                    Add(ins, "v", f.Version);
                    Add(ins, "n", f.Name);
                    Add(ins, "c", f.Checksum);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                done.Add(f);
            }
            return new MigrationResult(done, applied.Keys.Order().ToList());
        }
        finally
        {
            await Exec(conn, null, $"SELECT pg_advisory_unlock({LockKey})", ct);
        }
    }

    private static async Task Exec(DbConnection conn, DbTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
