// موصل PostgreSQL مصغّر للتطوير والاختبار دون اتصال بـ NuGet.
// لا يُستخدم في الإنتاج: بناء الإنتاج يُفعّل UseNpgsql=true فيستخدم Npgsql الرسمي (ADR-015).
// يدعم: المصادقة (trust/cleartext/md5/SCRAM-SHA-256)، البروتوكول البسيط والممتد بمعاملات نصية،
// المعاملات (BEGIN/COMMIT/ROLLBACK)، والنتائج النصية. لا يدعم TLS ولا COPY ولا الإشعارات.
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Ccms.Data.PgWire;

internal sealed record PgField(string Name, uint TypeOid);

internal sealed class PgResult
{
    public List<PgField> Fields { get; } = [];
    public List<string?[]> Rows { get; } = [];
    public int RecordsAffected { get; set; } = -1;
    public bool HasRowDescription { get; set; }
}

internal sealed class PgProtocol : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly BufferedStream _out;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public bool Broken { get; private set; }
    public char TransactionStatus { get; private set; } = 'I';

    private PgProtocol(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _out = new BufferedStream(_stream, 16 * 1024);
    }

    public static async Task<PgProtocol> ConnectAsync(PgConnectionSettings s, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(s.TimeoutSeconds));
        await tcp.ConnectAsync(s.Host, s.Port, timeout.Token);
        var p = new PgProtocol(tcp);
        try
        {
            await p.StartupAsync(s, timeout.Token);
            return p;
        }
        catch
        {
            p.Dispose();
            throw;
        }
    }

    // ---------------- بدء الاتصال والمصادقة ----------------
    private async Task StartupAsync(PgConnectionSettings s, CancellationToken ct)
    {
        var body = new MemoryStream();
        WriteInt32(body, 196608); // 3.0
        void Kv(string k, string v) { WriteCString(body, k); WriteCString(body, v); }
        Kv("user", s.Username);
        Kv("database", s.Database);
        Kv("client_encoding", "UTF8");
        Kv("TimeZone", "UTC");
        Kv("DateStyle", "ISO, YMD");
        Kv("application_name", s.ApplicationName);
        body.WriteByte(0);
        var len = (int)body.Length + 4;
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, len);
        await _out.WriteAsync(header, ct);
        await _out.WriteAsync(body.ToArray(), ct);
        await _out.FlushAsync(ct);

        string? scramClientFirstBare = null, scramNonce = null, scramServerFirst = null;
        byte[]? scramSaltedPassword = null;

        while (true)
        {
            var (type, payload) = await ReadMessageAsync(ct);
            switch (type)
            {
                case 'R':
                    var code = BinaryPrimitives.ReadInt32BigEndian(payload);
                    switch (code)
                    {
                        case 0: break; // AuthenticationOk
                        case 3: await SendPasswordAsync(Utf8.GetBytes(s.Password + "\0"), ct); break;
                        case 5:
                            {
                                var salt = payload.AsSpan(4, 4).ToArray();
                                var inner = Convert.ToHexStringLower(MD5.HashData(Utf8.GetBytes(s.Password + s.Username)));
                                var outer = Convert.ToHexStringLower(MD5.HashData([.. Utf8.GetBytes(inner), .. salt]));
                                await SendPasswordAsync(Utf8.GetBytes("md5" + outer + "\0"), ct);
                                break;
                            }
                        case 10:
                            {
                                var mechs = Utf8.GetString(payload, 4, payload.Length - 4);
                                if (!mechs.Contains("SCRAM-SHA-256"))
                                    throw new PgWireException("08004", "الخادم لا يدعم SCRAM-SHA-256");
                                scramNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
                                scramClientFirstBare = "n=,r=" + scramNonce;
                                var first = Utf8.GetBytes("n,," + scramClientFirstBare);
                                var m = new MemoryStream();
                                WriteCString(m, "SCRAM-SHA-256");
                                WriteInt32(m, first.Length);
                                m.Write(first);
                                await SendAsync('p', m.ToArray(), ct);
                                await _out.FlushAsync(ct);
                                break;
                            }
                        case 11:
                            {
                                scramServerFirst = Utf8.GetString(payload, 4, payload.Length - 4);
                                var attrs = ParseScram(scramServerFirst);
                                var serverNonce = attrs["r"];
                                if (!serverNonce.StartsWith(scramNonce!, StringComparison.Ordinal))
                                    throw new PgWireException("08004", "SCRAM: رمز الخادم غير صالح");
                                var salt = Convert.FromBase64String(attrs["s"]);
                                var iterations = int.Parse(attrs["i"]);
                                scramSaltedPassword = Rfc2898DeriveBytes.Pbkdf2(Utf8.GetBytes(s.Password), salt, iterations, HashAlgorithmName.SHA256, 32);
                                var clientKey = HMACSHA256.HashData(scramSaltedPassword, Utf8.GetBytes("Client Key"));
                                var storedKey = SHA256.HashData(clientKey);
                                var finalNoProof = "c=biws,r=" + serverNonce;
                                var authMessage = scramClientFirstBare + "," + scramServerFirst + "," + finalNoProof;
                                var signature = HMACSHA256.HashData(storedKey, Utf8.GetBytes(authMessage));
                                var proof = new byte[clientKey.Length];
                                for (var i = 0; i < proof.Length; i++) proof[i] = (byte)(clientKey[i] ^ signature[i]);
                                var final = Utf8.GetBytes(finalNoProof + ",p=" + Convert.ToBase64String(proof));
                                await SendAsync('p', final, ct);
                                await _out.FlushAsync(ct);
                                _scramAuthMessage = authMessage;
                                break;
                            }
                        case 12:
                            {
                                var attrs = ParseScram(Utf8.GetString(payload, 4, payload.Length - 4));
                                var serverKey = HMACSHA256.HashData(scramSaltedPassword!, Utf8.GetBytes("Server Key"));
                                var expected = Convert.ToBase64String(HMACSHA256.HashData(serverKey, Utf8.GetBytes(_scramAuthMessage!)));
                                if (!attrs.TryGetValue("v", out var v) || v != expected)
                                    throw new PgWireException("08004", "SCRAM: توقيع الخادم غير مطابق");
                                break;
                            }
                        default:
                            throw new PgWireException("08004", $"طريقة مصادقة غير مدعومة ({code})");
                    }
                    break;
                case 'E': throw ParseError(payload);
                case 'Z': TransactionStatus = (char)payload[0]; return;
                default: break; // S (ParameterStatus), K (BackendKeyData), N (Notice)
            }
        }
    }

    private string? _scramAuthMessage;

    private static Dictionary<string, string> ParseScram(string s) =>
        s.Split(',').Where(p => p.Length > 2 && p[1] == '=').ToDictionary(p => p[..1], p => p[2..]);

    private async Task SendPasswordAsync(byte[] body, CancellationToken ct)
    {
        await SendAsync('p', body, ct);
        await _out.FlushAsync(ct);
    }

    // ---------------- تنفيذ الاستعلامات ----------------

    /// <summary>البروتوكول البسيط: يسمح بعدة جمل (للترحيلات) ويعيد آخر مجموعة نتائج تحتوي صفوفًا أو الأخيرة.</summary>
    public async Task<PgResult> SimpleQueryAsync(string sql, CancellationToken ct)
    {
        EnsureUsable();
        try
        {
            var m = new MemoryStream();
            WriteCString(m, sql);
            await SendAsync('Q', m.ToArray(), ct);
            await _out.FlushAsync(ct);
            return await ReadResultsAsync(ct);
        }
        catch (Exception e) when (e is not PgWireException)
        {
            Broken = true;
            throw;
        }
    }

    /// <summary>البروتوكول الممتد بمعاملات نصية؛ الخادم يستنتج أنواعها من السياق أو من التحويل الصريح ::type.</summary>
    public async Task<PgResult> ExtendedQueryAsync(string sql, IReadOnlyList<string?> parameters, CancellationToken ct)
    {
        EnsureUsable();
        try
        {
            var parse = new MemoryStream();
            WriteCString(parse, "");
            WriteCString(parse, sql);
            WriteInt16(parse, 0);
            await SendAsync('P', parse.ToArray(), ct);

            var bind = new MemoryStream();
            WriteCString(bind, "");
            WriteCString(bind, "");
            WriteInt16(bind, 0); // كل المعاملات نصية
            WriteInt16(bind, (short)parameters.Count);
            foreach (var p in parameters)
            {
                if (p is null) { WriteInt32(bind, -1); continue; }
                var bytes = Utf8.GetBytes(p);
                WriteInt32(bind, bytes.Length);
                bind.Write(bytes);
            }
            WriteInt16(bind, 0); // النتائج نصية
            await SendAsync('B', bind.ToArray(), ct);

            await SendAsync('D', [(byte)'P', 0], ct);
            var exec = new MemoryStream();
            WriteCString(exec, "");
            WriteInt32(exec, 0);
            await SendAsync('E', exec.ToArray(), ct);
            await SendAsync('S', [], ct);
            await _out.FlushAsync(ct);
            return await ReadResultsAsync(ct);
        }
        catch (Exception e) when (e is not PgWireException)
        {
            Broken = true;
            throw;
        }
    }

    private async Task<PgResult> ReadResultsAsync(CancellationToken ct)
    {
        var results = new List<PgResult>();
        var current = new PgResult();
        PgWireException? error = null;
        while (true)
        {
            var (type, payload) = await ReadMessageAsync(ct);
            switch (type)
            {
                case 'T':
                    current = new PgResult { HasRowDescription = true };
                    ReadRowDescription(payload, current.Fields);
                    break;
                case 'D':
                    current.Rows.Add(ReadDataRow(payload, current.Fields.Count));
                    break;
                case 'C':
                    {
                        var tag = Encoding.ASCII.GetString(payload, 0, payload.Length - 1);
                        var parts = tag.Split(' ');
                        if (parts.Length >= 2 && int.TryParse(parts[^1], out var n)
                            && parts[0] is "INSERT" or "UPDATE" or "DELETE" or "MERGE")
                            current.RecordsAffected = n;
                        results.Add(current);
                        current = new PgResult();
                        break;
                    }
                case 'E':
                    error ??= ParseError(payload);
                    break;
                case 'Z':
                    TransactionStatus = (char)payload[0];
                    if (error is not null) throw error;
                    return results.LastOrDefault(r => r.HasRowDescription) ?? results.LastOrDefault() ?? current;
                default:
                    break; // 1, 2, n, N, S, I(EmptyQueryResponse)
            }
        }
    }

    private static void ReadRowDescription(byte[] p, List<PgField> fields)
    {
        var count = BinaryPrimitives.ReadInt16BigEndian(p);
        var pos = 2;
        for (var i = 0; i < count; i++)
        {
            var end = Array.IndexOf(p, (byte)0, pos);
            var name = Utf8.GetString(p, pos, end - pos);
            pos = end + 1;
            pos += 4 + 2; // table oid, attnum
            var typeOid = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(pos));
            pos += 4 + 2 + 4 + 2; // type oid, typlen, typmod, format
            fields.Add(new PgField(name, typeOid));
        }
    }

    private static string?[] ReadDataRow(byte[] p, int expected)
    {
        var count = BinaryPrimitives.ReadInt16BigEndian(p);
        var row = new string?[Math.Max(count, expected)];
        var pos = 2;
        for (var i = 0; i < count; i++)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(pos));
            pos += 4;
            if (len < 0) { row[i] = null; continue; }
            row[i] = Utf8.GetString(p, pos, len);
            pos += len;
        }
        return row;
    }

    private static PgWireException ParseError(byte[] p)
    {
        string sqlState = "XX000", message = "PostgreSQL error", detail = "", constraint = "";
        var pos = 0;
        while (pos < p.Length && p[pos] != 0)
        {
            var code = (char)p[pos++];
            var end = Array.IndexOf(p, (byte)0, pos);
            var value = Utf8.GetString(p, pos, end - pos);
            pos = end + 1;
            switch (code)
            {
                case 'C': sqlState = value; break;
                case 'M': message = value; break;
                case 'D': detail = value; break;
                case 'n': constraint = value; break;
            }
        }
        return new PgWireException(sqlState, message, detail, constraint);
    }

    // ---------------- الإطارات ----------------
    private async Task SendAsync(char type, byte[] body, CancellationToken ct)
    {
        var header = new byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), body.Length + 4);
        await _out.WriteAsync(header, ct);
        if (body.Length > 0) await _out.WriteAsync(body, ct);
    }

    private async Task<(char Type, byte[] Payload)> ReadMessageAsync(CancellationToken ct)
    {
        var header = new byte[5];
        await _stream.ReadExactlyAsync(header, ct);
        var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4;
        var payload = len > 0 ? new byte[len] : [];
        if (len > 0) await _stream.ReadExactlyAsync(payload, ct);
        return ((char)header[0], payload);
    }

    private void EnsureUsable()
    {
        if (Broken) throw new InvalidOperationException("اتصال PostgreSQL غير صالح");
    }

    private static void WriteInt32(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
    private static void WriteInt16(Stream s, short v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, v); s.Write(b); }
    private static void WriteCString(Stream s, string v) { s.Write(Utf8.GetBytes(v)); s.WriteByte(0); }

    public void Dispose()
    {
        try
        {
            if (!Broken && _tcp.Connected)
            {
                _out.Write([(byte)'X', 0, 0, 0, 4]);
                _out.Flush();
            }
        }
        catch { /* الإغلاق أفضل جهد */ }
        _tcp.Dispose();
    }
}
