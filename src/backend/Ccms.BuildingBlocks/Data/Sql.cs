using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Reflection;

namespace Ccms.BuildingBlocks.Data;

/// <summary>واجهة تنفيذ SQL مشتركة بين الاتصال المستقل والمعاملة. تعتمد على ADO.NET القياسي فقط،
/// فتعمل مع Npgsql في الإنتاج ومع الموصل المصغّر في التطوير دون تغيير في الوحدات.</summary>
public interface ISql
{
    Task<List<T>> QueryAsync<T>(string sql, object? args, Func<DbDataReader, T> map, CancellationToken ct = default);
    Task<T?> QuerySingleAsync<T>(string sql, object? args, Func<DbDataReader, T> map, CancellationToken ct = default);
    Task<int> ExecuteAsync(string sql, object? args = null, CancellationToken ct = default);
    Task<T?> ScalarAsync<T>(string sql, object? args = null, CancellationToken ct = default);
}

public sealed class Db(DbDataSource dataSource) : ISql
{
    public DbDataSource DataSource { get; } = dataSource;

    public async Task<List<T>> QueryAsync<T>(string sql, object? args, Func<DbDataReader, T> map, CancellationToken ct = default)
    {
        await using var conn = await DataSource.OpenConnectionAsync(ct);
        return await SqlCore.QueryAsync(conn, null, sql, args, map, ct);
    }

    public async Task<T?> QuerySingleAsync<T>(string sql, object? args, Func<DbDataReader, T> map, CancellationToken ct = default)
        => (await QueryAsync(sql, args, map, ct)).FirstOrDefault();

    public async Task<int> ExecuteAsync(string sql, object? args = null, CancellationToken ct = default)
    {
        await using var conn = await DataSource.OpenConnectionAsync(ct);
        return await SqlCore.ExecuteAsync(conn, null, sql, args, ct);
    }

    public async Task<T?> ScalarAsync<T>(string sql, object? args = null, CancellationToken ct = default)
    {
        await using var conn = await DataSource.OpenConnectionAsync(ct);
        return await SqlCore.ScalarAsync<T>(conn, null, sql, args, ct);
    }

    /// <summary>ينفّذ العمل في معاملة واحدة؛ أي استثناء يُرجع كل التغييرات.</summary>
    public async Task<T> InTransactionAsync<T>(Func<DbTx, Task<T>> work, CancellationToken ct = default)
    {
        await using var conn = await DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var result = await work(new DbTx(conn, tx));
        await tx.CommitAsync(ct);
        return result;
    }

    public Task InTransactionAsync(Func<DbTx, Task> work, CancellationToken ct = default)
        => InTransactionAsync<bool>(async tx => { await work(tx); return true; }, ct);
}

public sealed class DbTx(DbConnection connection, DbTransaction transaction) : ISql
{
    public Task<List<T>> QueryAsync<T>(string sql, object? args, Func<DbDataReader, T> map, CancellationToken ct = default)
        => SqlCore.QueryAsync(connection, transaction, sql, args, map, ct);

    public async Task<T?> QuerySingleAsync<T>(string sql, object? args, Func<DbDataReader, T> map, CancellationToken ct = default)
        => (await QueryAsync(sql, args, map, ct)).FirstOrDefault();

    public Task<int> ExecuteAsync(string sql, object? args = null, CancellationToken ct = default)
        => SqlCore.ExecuteAsync(connection, transaction, sql, args, ct);

    public Task<T?> ScalarAsync<T>(string sql, object? args = null, CancellationToken ct = default)
        => SqlCore.ScalarAsync<T>(connection, transaction, sql, args, ct);
}

internal static class SqlCore
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Props = new();

    private static DbCommand Create(DbConnection conn, DbTransaction? tx, string sql, object? args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        if (args is null) return cmd;
        IEnumerable<KeyValuePair<string, object?>> pairs = args is IReadOnlyDictionary<string, object?> dict
            ? dict
            : Props.GetOrAdd(args.GetType(), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Select(p => new KeyValuePair<string, object?>(p.Name, p.GetValue(args)));
        foreach (var (name, value) in pairs)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = Normalize(value) ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    /// <summary>توحيد القيم بين المشغّلين: التواريخ بتوقيت UTC، والقيم المعدودة نصًا.</summary>
    private static object? Normalize(object? value) => value switch
    {
        DateTime d when d.Kind == DateTimeKind.Unspecified => DateTime.SpecifyKind(d, DateTimeKind.Utc),
        DateTime d => d.ToUniversalTime(),
        Enum e => e.ToString(),
        _ => value,
    };

    public static async Task<List<T>> QueryAsync<T>(DbConnection conn, DbTransaction? tx, string sql, object? args, Func<DbDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = Create(conn, tx, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await reader.ReadAsync(ct)) list.Add(map(reader));
        return list;
    }

    public static async Task<int> ExecuteAsync(DbConnection conn, DbTransaction? tx, string sql, object? args, CancellationToken ct)
    {
        await using var cmd = Create(conn, tx, sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<T?> ScalarAsync<T>(DbConnection conn, DbTransaction? tx, string sql, object? args, CancellationToken ct)
    {
        await using var cmd = Create(conn, tx, sql, args);
        var v = await cmd.ExecuteScalarAsync(ct);
        if (v is null or DBNull) return default;
        if (v is T t) return t;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target == typeof(Guid)) return (T)(object)Guid.Parse(v.ToString()!);
        return (T)Convert.ChangeType(v, target, System.Globalization.CultureInfo.InvariantCulture);
    }
}

public static class ReaderExtensions
{
    public static T Get<T>(this DbDataReader r, string column) => r.GetFieldValue<T>(r.GetOrdinal(column));

    public static T? GetOrDefault<T>(this DbDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? default : r.GetFieldValue<T>(i);
    }

    public static string? Str(this DbDataReader r, string column) => r.GetOrDefault<string>(column);
    public static Guid? Uuid(this DbDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetFieldValue<Guid>(i);
    }
    public static DateTime? Time(this DbDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetFieldValue<DateTime>(i);
    }
    public static DateOnly? Date(this DbDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i);
    }
}

public static class SqlErrors
{
    public const string UniqueViolation = "23505";
    public const string ForeignKeyViolation = "23503";
    public const string CheckViolation = "23514";
    public const string InsufficientPrivilege = "42501";

    public static bool Is(this Exception e, string sqlState) => e is DbException db && db.SqlState == sqlState;
}
