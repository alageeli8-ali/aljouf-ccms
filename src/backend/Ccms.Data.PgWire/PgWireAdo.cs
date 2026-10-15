using System.Collections;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Ccms.Data.PgWire;

public sealed class PgWireException(string sqlState, string message, string detail = "", string constraint = "")
    : DbException(string.IsNullOrEmpty(detail) ? message : $"{message} ({detail})")
{
    public override string SqlState { get; } = sqlState;
    public string ConstraintName { get; } = constraint;
}

public sealed record PgConnectionSettings(string Host, int Port, string Database, string Username, string Password,
    string ApplicationName, int TimeoutSeconds, int MaxPoolSize)
{
    public static PgConnectionSettings Parse(string connectionString)
    {
        var b = new DbConnectionStringBuilder { ConnectionString = connectionString };
        string Get(params string[] keys)
        {
            foreach (var k in keys)
                if (b.TryGetValue(k, out var v) && v is not null) return v.ToString()!;
            return "";
        }
        var port = Get("Port");
        var timeout = Get("Timeout");
        var pool = Get("Maximum Pool Size", "MaxPoolSize");
        return new PgConnectionSettings(
            Get("Host", "Server") is { Length: > 0 } h ? h : "localhost",
            port.Length > 0 ? int.Parse(port, CultureInfo.InvariantCulture) : 5432,
            Get("Database"),
            Get("Username", "User ID", "User"),
            Get("Password"),
            Get("Application Name") is { Length: > 0 } a ? a : "ccms",
            timeout.Length > 0 ? int.Parse(timeout, CultureInfo.InvariantCulture) : 15,
            pool.Length > 0 ? int.Parse(pool, CultureInfo.InvariantCulture) : 20);
    }
}

/// <summary>مصدر اتصالات بمجمع بسيط. يطابق واجهة DbDataSource القياسية التي يرثها NpgsqlDataSource أيضًا.</summary>
public sealed class PgWireDataSource(string connectionString) : DbDataSource
{
    private readonly ConcurrentBag<PgProtocol> _pool = [];
    private int _pooled;
    internal PgConnectionSettings Settings { get; } = PgConnectionSettings.Parse(connectionString);

    public override string ConnectionString { get; } = connectionString;

    protected override DbConnection CreateDbConnection() => new PgWireConnection(this);

    internal async Task<PgProtocol> RentAsync(CancellationToken ct)
    {
        while (_pool.TryTake(out var p))
        {
            Interlocked.Decrement(ref _pooled);
            if (!p.Broken) return p;
            p.Dispose();
        }
        return await PgProtocol.ConnectAsync(Settings, ct);
    }

    internal void Return(PgProtocol p)
    {
        if (p.Broken || p.TransactionStatus != 'I' || Interlocked.Increment(ref _pooled) > Settings.MaxPoolSize)
        {
            if (!p.Broken && p.TransactionStatus == 'I') Interlocked.Decrement(ref _pooled);
            p.Dispose();
            return;
        }
        _pool.Add(p);
    }

    protected override void Dispose(bool disposing)
    {
        while (_pool.TryTake(out var p)) p.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class PgWireConnection : DbConnection
{
    private readonly PgWireDataSource _source;
    private PgProtocol? _protocol;
    internal PgWireTransaction? CurrentTransaction { get; set; }

    internal PgWireConnection(PgWireDataSource source) => _source = source;

    internal PgProtocol Protocol => _protocol ?? throw new InvalidOperationException("الاتصال غير مفتوح");

    [AllowNull]
    public override string ConnectionString { get => _source.ConnectionString; set => throw new NotSupportedException(); }
    public override string Database => _source.Settings.Database;
    public override string DataSource => $"{_source.Settings.Host}:{_source.Settings.Port}";
    public override string ServerVersion => "16";
    public override ConnectionState State => _protocol is null ? ConnectionState.Closed : ConnectionState.Open;

    public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_protocol is not null) return;
        _protocol = await _source.RentAsync(cancellationToken);
    }

    public override void Close()
    {
        if (_protocol is null) return;
        if (_protocol.TransactionStatus != 'I' && !_protocol.Broken)
        {
            try { _protocol.SimpleQueryAsync("ROLLBACK", CancellationToken.None).GetAwaiter().GetResult(); }
            catch { /* سيُتخلص من الاتصال */ }
        }
        _source.Return(_protocol);
        _protocol = null;
        CurrentTransaction = null;
    }

    public override Task CloseAsync() { Close(); return Task.CompletedTask; }

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        BeginDbTransactionAsync(isolationLevel, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        var level = isolationLevel switch
        {
            IsolationLevel.Serializable => "SERIALIZABLE",
            IsolationLevel.RepeatableRead or IsolationLevel.Snapshot => "REPEATABLE READ",
            _ => "READ COMMITTED",
        };
        await Protocol.SimpleQueryAsync($"BEGIN ISOLATION LEVEL {level}", cancellationToken);
        CurrentTransaction = new PgWireTransaction(this, isolationLevel);
        return CurrentTransaction;
    }

    protected override DbCommand CreateDbCommand() => new PgWireCommand { Connection = this };

    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}

public sealed class PgWireTransaction(PgWireConnection connection, IsolationLevel level) : DbTransaction
{
    private bool _done;
    public override IsolationLevel IsolationLevel => level;
    protected override DbConnection DbConnection => connection;

    public override void Commit() => CommitAsync().GetAwaiter().GetResult();
    public override void Rollback() => RollbackAsync().GetAwaiter().GetResult();

    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_done) return;
        _done = true;
        await connection.Protocol.SimpleQueryAsync("COMMIT", cancellationToken);
        connection.CurrentTransaction = null;
    }

    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_done) return;
        _done = true;
        if (connection.State == ConnectionState.Open && !connection.Protocol.Broken)
            await connection.Protocol.SimpleQueryAsync("ROLLBACK", cancellationToken);
        connection.CurrentTransaction = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_done) Rollback();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_done) await RollbackAsync();
    }
}

public sealed class PgWireParameter : DbParameter
{
    public override DbType DbType { get; set; } = DbType.Object;
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; } = true;
    [AllowNull] public override string ParameterName { get; set; } = "";
    [AllowNull] public override string SourceColumn { get; set; } = "";
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    public override int Size { get; set; }
    public override void ResetDbType() => DbType = DbType.Object;
}

public sealed class PgWireParameterCollection : DbParameterCollection
{
    private readonly List<PgWireParameter> _items = [];
    internal IReadOnlyList<PgWireParameter> Items => _items;

    public override int Count => _items.Count;
    public override object SyncRoot => _items;
    public override int Add(object value) { _items.Add((PgWireParameter)value); return _items.Count - 1; }
    public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
    public override void Clear() => _items.Clear();
    public override bool Contains(object value) => _items.Contains((PgWireParameter)value);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => _items.IndexOf((PgWireParameter)value);
    public override int IndexOf(string parameterName) =>
        _items.FindIndex(p => string.Equals(Normalize(p.ParameterName), Normalize(parameterName), StringComparison.OrdinalIgnoreCase));
    public override void Insert(int index, object value) => _items.Insert(index, (PgWireParameter)value);
    public override void Remove(object value) => _items.Remove((PgWireParameter)value);
    public override void RemoveAt(int index) => _items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));
    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => _items[index] = (PgWireParameter)value;
    protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = (PgWireParameter)value;
    internal static string Normalize(string name) => name.TrimStart('@', ':', '$');
}

public sealed class PgWireCommand : DbCommand
{
    private readonly PgWireParameterCollection _parameters = new();

    [AllowNull] public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; } = 30;
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new PgWireParameter();

    private async Task<PgResult> RunAsync(CancellationToken ct)
    {
        var conn = (PgWireConnection)(DbConnection ?? throw new InvalidOperationException("لا يوجد اتصال"));
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (CommandTimeout > 0) timeout.CancelAfter(TimeSpan.FromSeconds(CommandTimeout));
        if (_parameters.Count == 0)
            return await conn.Protocol.SimpleQueryAsync(CommandText, timeout.Token);
        var (sql, values) = PgSqlRewriter.Rewrite(CommandText, _parameters.Items);
        return await conn.Protocol.ExtendedQueryAsync(sql, values, timeout.Token);
    }

    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var r = await RunAsync(cancellationToken);
        return r.RecordsAffected;
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var r = await RunAsync(cancellationToken);
        if (r.Rows.Count == 0 || r.Fields.Count == 0) return null;
        var raw = r.Rows[0][0];
        return raw is null ? DBNull.Value : PgValueConverter.Natural(raw, r.Fields[0].TypeOid);
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        => new PgWireDataReader(await RunAsync(cancellationToken));
}

/// <summary>يحوّل المعاملات المسماة @name إلى $n خارج النصوص والتعليقات، بنفس سلوك Npgsql.</summary>
internal static class PgSqlRewriter
{
    public static (string Sql, List<string?> Values) Rewrite(string sql, IReadOnlyList<PgWireParameter> parameters)
    {
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var values = new List<string?>();
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\'' || c == '"')
            {
                var end = sql.IndexOf(c, i + 1);
                while (end > 0 && end + 1 < sql.Length && sql[end + 1] == c) end = sql.IndexOf(c, end + 2);
                end = end < 0 ? sql.Length - 1 : end;
                sb.Append(sql, i, end - i + 1);
                i = end + 1;
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                end = end < 0 ? sql.Length : end;
                sb.Append(sql, i, end - i);
                i = end;
                continue;
            }
            if (c == '@' && i + 1 < sql.Length && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_')
                && (i == 0 || !(char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] == '@' || sql[i - 1] == '_')))
            {
                var j = i + 1;
                while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
                var name = sql[(i + 1)..j];
                var p = parameters.FirstOrDefault(x => string.Equals(PgWireParameterCollection.Normalize(x.ParameterName), name, StringComparison.OrdinalIgnoreCase));
                if (p is not null)
                {
                    if (!byName.TryGetValue(name, out var idx))
                    {
                        values.Add(PgValueConverter.ToText(p.Value));
                        idx = values.Count;
                        byName[name] = idx;
                    }
                    sb.Append('$').Append(idx);
                    i = j;
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return (sb.ToString(), values);
    }
}

internal static class PgValueConverter
{
    public const uint Bool = 16, Int8 = 20, Int2 = 21, Int4 = 23, Text = 25, Json = 114, Float4 = 700, Float8 = 701,
        Varchar = 1043, Date = 1082, Timestamp = 1114, TimestampTz = 1184, Numeric = 1700, Uuid = 2950, Jsonb = 3802,
        TextArray = 1009, UuidArray = 2951, VarcharArray = 1015, Int4Array = 1007;

    public static string? ToText(object? value) => value switch
    {
        null or DBNull => null,
        string s => s,
        bool b => b ? "true" : "false",
        Guid g => g.ToString(),
        DateTime d => (d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime())
            .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable seq => "{" + string.Join(",", seq.Cast<object?>().Select(x => x is null ? "NULL" : Quote(ToText(x)!))) + "}",
        _ => value.ToString(),
    };

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static object Natural(string raw, uint oid) => oid switch
    {
        Bool => raw == "t",
        Int2 => short.Parse(raw, CultureInfo.InvariantCulture),
        Int4 => int.Parse(raw, CultureInfo.InvariantCulture),
        Int8 => long.Parse(raw, CultureInfo.InvariantCulture),
        Float4 => float.Parse(raw, CultureInfo.InvariantCulture),
        Float8 => double.Parse(raw, CultureInfo.InvariantCulture),
        Numeric => decimal.Parse(raw, CultureInfo.InvariantCulture),
        Uuid => Guid.Parse(raw),
        Date => DateTime.SpecifyKind(DateTime.ParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Unspecified),
        TimestampTz => ParseTimestampTz(raw),
        Timestamp => DateTime.SpecifyKind(DateTime.Parse(raw, CultureInfo.InvariantCulture), DateTimeKind.Unspecified),
        TextArray or VarcharArray => ParseArray(raw).ToArray(),
        UuidArray => ParseArray(raw).Select(x => Guid.Parse(x!)).ToArray(),
        Int4Array => ParseArray(raw).Select(x => int.Parse(x!, CultureInfo.InvariantCulture)).ToArray(),
        _ => raw,
    };

    public static DateTime ParseTimestampTz(string raw) =>
        DateTimeOffset.Parse(raw.Length > 3 && (raw[^3] == '+' || raw[^3] == '-') ? raw + ":00" : raw, CultureInfo.InvariantCulture).UtcDateTime;

    public static List<string?> ParseArray(string raw)
    {
        var result = new List<string?>();
        if (raw.Length < 2) return result;
        var body = raw[1..^1];
        var i = 0;
        while (i < body.Length)
        {
            if (body[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < body.Length && body[i] != '"')
                {
                    if (body[i] == '\\') i++;
                    sb.Append(body[i]);
                    i++;
                }
                result.Add(sb.ToString());
                i += 2;
            }
            else
            {
                var end = body.IndexOf(',', i);
                end = end < 0 ? body.Length : end;
                var item = body[i..end];
                result.Add(item == "NULL" ? null : item);
                i = end + 1;
            }
        }
        return result;
    }
}

public sealed class PgWireDataReader : DbDataReader
{
    private readonly PgResult _result;
    private int _row = -1;

    internal PgWireDataReader(PgResult result) => _result = result;

    private string? Raw(int ordinal) => _result.Rows[_row][ordinal];

    public override int FieldCount => _result.Fields.Count;
    public override int RecordsAffected => _result.RecordsAffected;
    public override bool HasRows => _result.Rows.Count > 0;
    public override bool IsClosed => false;
    public override int Depth => 0;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => ++_row < _result.Rows.Count;
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
    public override bool NextResult() => false;

    public override string GetName(int ordinal) => _result.Fields[ordinal].Name;
    public override int GetOrdinal(string name)
    {
        var i = _result.Fields.FindIndex(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : throw new IndexOutOfRangeException(name);
    }
    public override string GetDataTypeName(int ordinal) => _result.Fields[ordinal].TypeOid.ToString(CultureInfo.InvariantCulture);
    public override Type GetFieldType(int ordinal) => GetValue(ordinal).GetType();
    public override bool IsDBNull(int ordinal) => Raw(ordinal) is null;
    public override object GetValue(int ordinal) => Raw(ordinal) is { } raw ? PgValueConverter.Natural(raw, _result.Fields[ordinal].TypeOid) : DBNull.Value;
    public override int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < n; i++) values[i] = GetValue(i);
        return n;
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        var raw = Raw(ordinal) ?? throw new InvalidCastException($"العمود {GetName(ordinal)} فارغ");
        var t = typeof(T);
        object value =
            t == typeof(string) ? raw :
            t == typeof(Guid) ? Guid.Parse(raw) :
            t == typeof(int) ? int.Parse(raw, CultureInfo.InvariantCulture) :
            t == typeof(long) ? long.Parse(raw, CultureInfo.InvariantCulture) :
            t == typeof(short) ? short.Parse(raw, CultureInfo.InvariantCulture) :
            t == typeof(bool) ? raw == "t" :
            t == typeof(decimal) ? decimal.Parse(raw, CultureInfo.InvariantCulture) :
            t == typeof(double) ? double.Parse(raw, CultureInfo.InvariantCulture) :
            t == typeof(DateOnly) ? DateOnly.ParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture) :
            t == typeof(DateTime) ? (_result.Fields[ordinal].TypeOid == PgValueConverter.Date
                ? DateTime.ParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                : PgValueConverter.ParseTimestampTz(raw)) :
            t == typeof(string[]) ? PgValueConverter.ParseArray(raw).Select(x => x ?? "").ToArray() :
            t == typeof(Guid[]) ? PgValueConverter.ParseArray(raw).Select(x => Guid.Parse(x!)).ToArray() :
            PgValueConverter.Natural(raw, _result.Fields[ordinal].TypeOid);
        return (T)value;
    }

    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);
    public override byte GetByte(int ordinal) => throw new NotSupportedException();
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override char GetChar(int ordinal) => GetString(ordinal)[0];
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override DateTime GetDateTime(int ordinal) => GetFieldValue<DateTime>(ordinal);
    public override decimal GetDecimal(int ordinal) => GetFieldValue<decimal>(ordinal);
    public override double GetDouble(int ordinal) => GetFieldValue<double>(ordinal);
    public override float GetFloat(int ordinal) => (float)GetDouble(ordinal);
    public override Guid GetGuid(int ordinal) => GetFieldValue<Guid>(ordinal);
    public override short GetInt16(int ordinal) => GetFieldValue<short>(ordinal);
    public override int GetInt32(int ordinal) => GetFieldValue<int>(ordinal);
    public override long GetInt64(int ordinal) => GetFieldValue<long>(ordinal);
    public override string GetString(int ordinal) => Raw(ordinal) ?? throw new InvalidCastException($"العمود {GetName(ordinal)} فارغ");
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}
