using System.Net.Sockets;
using System.Net.Security;

namespace Galactus;
public sealed class DatabaseException : Exception
{
    public string Code { get; }
    public Dictionary<string, object?> Metadata { get; }
    internal DatabaseException(Dictionary<string, object?> m)
        : base(m.GetValueOrDefault("message") as string)
    {
        Code = m.GetValueOrDefault("code") as string ?? "DatabaseError";
        Metadata = m;
    }
}
public sealed record Result(IReadOnlyList<string> Keys,
                            IReadOnlyList<Dictionary<string, object?>> Records,
                            Dictionary<string, object?> Summary);

/// <summary>One serial connection; Dispose rolls back an open
/// transaction.</summary>
public sealed class Driver : IDisposable
{
    private readonly TcpClient client;
    private readonly Stream stream;
    private readonly string database;
    private bool transaction;
    private readonly object gate = new();
    public Driver(string uri, string username, string password,
                  string database = "", int timeoutMilliseconds = 30000)
    {
        var u = new Uri(uri);
        if ((u.Scheme != "bolt" && u.Scheme != "bolt+s") || u.Host.Length == 0 ||
            u.UserInfo.Length != 0 ||
            (u.AbsolutePath != "" && u.AbsolutePath != "/") ||
            u.Query.Length != 0 || u.Fragment.Length != 0)
            throw new ArgumentException(
                "Expected bolt://host:port or bolt+s://host:port");
        if (timeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        client =
            new TcpClient
            {
                NoDelay = true,
                ReceiveTimeout = timeoutMilliseconds,
                SendTimeout = timeoutMilliseconds
            };
        this.database = database;
        try
        {
            using var deadline = new CancellationTokenSource(timeoutMilliseconds);
            client
                .ConnectAsync(u.DnsSafeHost, u.Port < 0 ? 7687 : u.Port,
                              deadline.Token)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            stream = client.GetStream();
            if (u.Scheme == "bolt+s")
            {
                var tls = new SslStream(stream, false);
                stream = tls;
                tls.AuthenticateAsClient(u.DnsSafeHost);
            }
            stream.Write(
                Convert.FromHexString("6060B01700000404000000000000000000000000"));
            if (!Read(4).SequenceEqual(new byte[] { 0, 0, 4, 4 }))
                throw new IOException("Server did not select Bolt 4.4");
            Send(1, new Dictionary<string, object?> { { "user_agent",
                                                  "galactus-csharp/0.1" },
                                                { "scheme", "basic" },
                                                { "principal", username },
                                                { "credentials", password } });
            Success();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
    private byte[] Read(int n)
    {
        var b = new byte[n];
        int p = 0;
        while (p < n)
        {
            int count = stream.Read(b, p, n - p);
            if (count == 0)
                throw new EndOfStreamException("Connection closed");
            p += count;
        }
        return b;
    }
    private void Send(byte tag, params object?[] fields)
    {
        if (tag == 0x10 || tag == 0x11)
        {
            var extra = (Dictionary<string, object?>)fields[tag == 0x10 ? 2 : 0]!;
            if (Equals(extra.GetValueOrDefault("db"), "")) extra.Remove("db");
        }
        if (tag == 0x10) Mapping.ValidateParameters(fields[1]);
        var b = PackStream.Encode(new Structure(tag, fields));
        using var frame = new MemoryStream();
        for (int p = 0; p < b.Length; p += 65535)
        {
            int n = Math.Min(65535, b.Length - p);
            frame.WriteByte((byte)(n >> 8));
            frame.WriteByte((byte)n);
            frame.Write(b, p, n);
        }
        frame.WriteByte(0);
        frame.WriteByte(0);
        stream.Write(frame.ToArray());
        stream.Flush();
    }
    private Structure Receive()
    {
        using var body = new MemoryStream();
        while (true)
        {
            var h = Read(2);
            int n = h[0] * 256 + h[1];
            if (n == 0)
            {
                if (body.Length > 0)
                    break;
                continue;
            }
            if (body.Length + n > PackStream.MaxMessage)
                throw new InvalidDataException("Message exceeds 64 MiB");
            body.Write(Read(n));
        }
        if (PackStream.Decode(body.ToArray()) is not Structure m ||
            m.Fields.Length != 1)
            throw new InvalidDataException("Invalid Bolt response");
        if (m.Tag == 0x7f)
            throw new DatabaseException((Dictionary<string, object?>)m.Fields[0]!);
        if (m.Tag != 0x70 && m.Tag != 0x71)
            throw new InvalidDataException("Unexpected Bolt response");
        return m;
    }
    private Dictionary<string, object?> Success()
    {
        var m = Receive();
        if (m.Tag != 0x70)
            throw new InvalidDataException("Expected SUCCESS");
        return (Dictionary<string, object?>)m.Fields[0]!;
    }
    public Result ExecuteQuery(string query,
                               IDictionary<string, object?>? parameters = null)
    {
        lock (gate)
        {
            try
            {
                Send(0x10, query, parameters ?? new Dictionary<string, object?>(),
                     transaction
                         ? new Dictionary<string, object?>()
                         : new Dictionary<string, object?> { { "db", database } });
                var meta = Success();
                var keys = ((List<object?>)meta["fields"]!).Cast<string>().ToList();
                var records = new List<Dictionary<string, object?>>();
                Send(0x3f, new Dictionary<string, object?> { { "n", -1L } });
                while (true)
                {
                    var m = Receive();
                    if (m.Tag == 0x70)
                    {
                        var summary = (Dictionary<string, object?>)m.Fields[0]!;
                        if (Equals(summary.GetValueOrDefault("has_more"), true))
                        {
                            Send(0x3f, new Dictionary<string, object?> { { "n", -1L } });
                            continue;
                        }
                        foreach (var e in summary)
                            meta[e.Key] = e.Value;
                        return new Result(keys, records, meta);
                    }
                    var values = (List<object?>)m.Fields[0]!;
                    if (values.Count != keys.Count)
                        throw new InvalidDataException("Record width mismatch");
                    var row = new Dictionary<string, object?>();
                    for (int i = 0; i < keys.Count; i++)
                        row[keys[i]] = values[i];
                    records.Add(row);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }
    }
    public Driver Begin(bool readOnly = false)
    {
        lock (gate)
        {
            if (transaction)
                throw new InvalidOperationException("Transaction already open");
            try
            {
                Send(0x11, new Dictionary<string, object?> {
          { "db", database }, { "mode", readOnly ? "r" : "w" }
        });
                Success();
                transaction = true;
                return this;
            }
            catch
            {
                Dispose();
                throw;
            }
        }
    }
    private Dictionary<string, object?> Finish(byte tag)
    {
        lock (gate)
        {
            if (!transaction)
                throw new InvalidOperationException("No transaction");
            try
            {
                Send(tag);
                var meta = Success();
                transaction = false;
                return meta;
            }
            catch
            {
                Dispose();
                throw;
            }
        }
    }
    public Dictionary<string, object?> Commit() => Finish(0x12);
    public void Rollback() => Finish(0x13);
    public void Dispose()
    {
        lock (gate)
        {
            transaction = false;
            stream?.Dispose();
            client.Dispose();
        }
    }
}
