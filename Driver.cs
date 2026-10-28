using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Galactus;

/// <summary>An error reported by the server. <see cref="Code"/> and
/// <see cref="Exception.Message"/> are the server's own; the classification
/// properties are parsed from the code.</summary>
public sealed class DatabaseException : Exception
{
    public string Code { get; }
    public Dictionary<string, object?> Metadata { get; }
    /// <summary><c>ClientError</c>, <c>TransientError</c> or
    /// <c>DatabaseError</c>; empty when the code does not have four
    /// parts.</summary>
    public string Classification { get; }
    public string Category { get; }
    public string Title { get; }
    public bool IsClientError => Classification == "ClientError";
    public bool IsTransient => Classification == "TransientError";
    public bool IsDatabaseError => Classification == "DatabaseError";
    /// <summary>True for <c>TransientError</c> codes only. The driver never
    /// retries by itself.</summary>
    public bool IsRetryable => IsTransient;
    internal DatabaseException(Dictionary<string, object?> m)
        : base(m.GetValueOrDefault("message") as string)
    {
        Code = m.GetValueOrDefault("code") as string ?? "DatabaseError";
        Metadata = m;
        var parts = Code.Split('.');
        bool full = parts.Length == 4;
        Classification = full ? parts[1] : "";
        Category = full ? parts[2] : "";
        Title = full ? parts[3] : "";
    }
}

/// <summary>Server error codes published by Galactus DB. Codes the server
/// adds later still arrive as plain strings in
/// <see cref="DatabaseException.Code"/>.</summary>
public static class ErrorCodes
{
    public const string ConstraintValidationFailed =
        "Galactus.ClientError.Schema.ConstraintValidationFailed";
    public const string TypeError = "Galactus.ClientError.Statement.TypeError";
    public const string ArgumentError = "Galactus.ClientError.Statement.ArgumentError";
    public const string SqlTooManyRows = "Galactus.ClientError.Statement.SqlTooManyRows";
    public const string SqlCastFailed = "Galactus.ClientError.Statement.SqlCastFailed";
    public const string TableTooLarge = "Galactus.ClientError.Statement.TableTooLarge";
    public const string UnsupportedFeature =
        "Galactus.DatabaseError.Storage.UnsupportedFeature";
    public const string ProcedureNotFound =
        "Galactus.ClientError.Procedure.ProcedureNotFound";
    public const string LicenseRestricted = "Galactus.ClientError.License.Restricted";
    public const string ReadOnlyReplica = "Galactus.ClientError.Database.ReadOnlyReplica";
    public const string TransactionTimedOut = "Galactus.ClientError.Transaction.TimedOut";
    public const string TransactionTerminated =
        "Galactus.ClientError.Transaction.Terminated";
    public const string SnapshotTooOld =
        "Galactus.TransientError.Transaction.SnapshotTooOld";
    public const string Forbidden = "Galactus.ClientError.Security.Forbidden";
    public const string SyntaxError = "Neo.ClientError.Statement.SyntaxError";
    public const string SemanticError = "Neo.ClientError.Statement.SemanticError";
    public const string Unauthorized = "Neo.ClientError.Security.Unauthorized";
    public const string DatabaseNotFound = "Neo.ClientError.Database.DatabaseNotFound";
}

public sealed record Result(IReadOnlyList<string> Keys,
                            IReadOnlyList<Dictionary<string, object?>> Records,
                            Dictionary<string, object?> Summary);

/// <summary>One serial connection; Dispose rolls back an open
/// transaction.</summary>
public sealed class Driver : IDisposable
{
    public const string Version = "0.2.0";
    private const string Agent = "galactus-db-csharp-driver/" + Version;
    private readonly TcpClient client;
    private readonly Stream stream;
    private readonly string database;
    private readonly X509Certificate2Collection? trusted;
    private readonly bool bolt5;
    private bool transaction;
    private bool closed;
    private readonly object gate = new();

    /// <summary>Agreed Bolt version, <c>"5.4"</c> down to <c>"5.0"</c>, or
    /// <c>"4.4"</c>.</summary>
    public string ProtocolVersion { get; }
    /// <summary>The <c>server</c> field of the HELLO SUCCESS.</summary>
    public string? ServerAgent { get; }
    /// <summary>The <c>connection_id</c> field of the HELLO SUCCESS.</summary>
    public string? ConnectionId { get; }
    /// <summary>The most recent bookmark from an autocommit query or a
    /// commit. The driver does not send bookmarks.</summary>
    public string? LastBookmark { get; private set; }

    /// <param name="trustedCertificates">Optional path to a PEM file holding
    /// one or more certificates. For <c>bolt+s://</c> they replace the
    /// platform trust store; the host name is still checked. Setting it on a
    /// <c>bolt://</c> URI is an error.</param>
    public Driver(string uri, string username, string password,
                  string database = "", int timeoutMilliseconds = 30000,
                  string? trustedCertificates = null)
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
        if (trustedCertificates is not null)
        {
            if (u.Scheme != "bolt+s")
                throw new ArgumentException(
                    "trustedCertificates requires a bolt+s:// URI",
                    nameof(trustedCertificates));
            trusted = new X509Certificate2Collection();
            try
            {
                trusted.ImportFromPemFile(trustedCertificates);
            }
            catch (Exception e) when (e is not ArgumentException)
            {
                throw new ArgumentException(
                    "Cannot read trusted certificates: " + e.Message,
                    nameof(trustedCertificates), e);
            }
            if (trusted.Count == 0)
                throw new ArgumentException("No certificates in trusted certificate file",
                                            nameof(trustedCertificates));
        }
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
                var tls = new SslStream(stream, false, ValidateServerCertificate);
                stream = tls;
                tls.AuthenticateAsClient(u.DnsSafeHost);
            }
            // 5.4 down to 5.0, then 4.4.
            stream.Write(
                Convert.FromHexString("6060B01700040405000004040000000000000000"));
            var v = Read(4);
            if (v[0] != 0 || v[1] != 0 ||
                !((v[3] == 5 && v[2] <= 4) || (v[3] == 4 && v[2] == 4)))
                throw new IOException("Server did not agree Bolt 5.0-5.4 or 4.4");
            bolt5 = v[3] == 5;
            ProtocolVersion = $"{v[3]}.{v[2]}";
            var hello = new Dictionary<string, object?> { { "user_agent", Agent } };
            var auth = new Dictionary<string, object?> { { "scheme", "basic" },
                                                         { "principal", username },
                                                         { "credentials", password } };
            bool logon = bolt5 && v[2] >= 1;
            if (logon)
                hello["bolt_agent"] = BoltAgent();
            else
                foreach (var e in auth)
                    hello[e.Key] = e.Value;
            Write(Frame(0x01, hello));
            var greeting = Success();
            ServerAgent = greeting.GetValueOrDefault("server") as string;
            ConnectionId = greeting.GetValueOrDefault("connection_id") as string;
            if (logon)
            {
                Write(Frame(0x6A, auth));
                Success();
            }
        }
        catch
        {
            Close();
            throw;
        }
    }
    private static Dictionary<string, object?> BoltAgent()
    {
        var framework = RuntimeInformation.FrameworkDescription.Trim();
        int space = framework.LastIndexOf(' ');
        var language = space > 0 ? framework[..space] + "/" + framework[(space + 1)..]
                                 : framework;
        return new Dictionary<string, object?> {
            { "product", Agent },
            { "language", language },
            { "language_details", framework },
            { "platform", $"{RuntimeInformation.OSDescription.Trim()}; " +
                          $"{RuntimeInformation.OSArchitecture}" }
        };
    }
    private bool ValidateServerCertificate(object sender, X509Certificate? certificate,
                                           X509Chain? chain, SslPolicyErrors errors)
    {
        if (trusted is null)
            return errors == SslPolicyErrors.None;
        if (certificate is null ||
            (errors & (SslPolicyErrors.RemoteCertificateNameMismatch |
                       SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            return false;
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(trusted);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (chain is not null)
            foreach (var element in chain.ChainElements)
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
        using var leaf = new X509Certificate2(certificate);
        return custom.Build(leaf);
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
    /// <summary>Validates and encodes a complete request before anything is
    /// written.</summary>
    private byte[] Frame(byte tag, params object?[] fields)
    {
        if (tag == 0x10 || tag == 0x11)
        {
            var extra = (Dictionary<string, object?>)fields[tag == 0x10 ? 2 : 0]!;
            if (Equals(extra.GetValueOrDefault("db"), "")) extra.Remove("db");
        }
        if (tag == 0x10) Mapping.ValidateParameters(fields[1], bolt5);
        var b = PackStream.Encode(new Structure(tag, fields), bolt5);
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
        return frame.ToArray();
    }
    private void Write(byte[] frame)
    {
        stream.Write(frame);
        stream.Flush();
    }
    private Structure ReceiveMessage()
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
            (m.Tag == 0x7e ? m.Fields.Length > 1 : m.Fields.Length != 1))
            throw new InvalidDataException("Invalid Bolt response");
        return m;
    }
    /// <summary>Reads a RECORD or SUCCESS; FAILURE becomes a
    /// <see cref="DatabaseException"/>.</summary>
    private Structure Receive()
    {
        var m = ReceiveMessage();
        if (m.Tag == 0x7f)
            throw new DatabaseException(
                m.Fields[0] as Dictionary<string, object?> ??
                throw new InvalidDataException("Invalid FAILURE"));
        if (m.Tag != 0x70 && m.Tag != 0x71)
            throw new InvalidDataException("Unexpected Bolt response");
        return m;
    }
    private Dictionary<string, object?> Success()
    {
        var m = Receive();
        if (m.Tag != 0x70 || m.Fields[0] is not Dictionary<string, object?> meta)
            throw new InvalidDataException("Expected SUCCESS");
        return meta;
    }
    /// <summary>Clears the server's failed state after a FAILURE. The server
    /// rolls back an open transaction. Throws (and the caller discards the
    /// connection) when RESET does not end in SUCCESS.</summary>
    private void Reset()
    {
        transaction = false;
        Write(Frame(0x0F));
        while (true)
        {
            var m = ReceiveMessage();
            if (m.Tag == 0x7e)
                continue;
            if (m.Tag == 0x70)
                return;
            throw new InvalidDataException("RESET did not succeed");
        }
    }
    /// <summary>Runs one request: a database FAILURE resets the connection
    /// and is rethrown with the driver still usable; any other failure
    /// discards the connection.</summary>
    private T Exchange<T>(Func<T> body)
    {
        if (closed)
            throw new ObjectDisposedException(nameof(Driver));
        try
        {
            return body();
        }
        catch (DatabaseException)
        {
            try
            {
                Reset();
            }
            catch
            {
                Close();
                throw;
            }
            throw;
        }
        catch
        {
            Close();
            throw;
        }
    }
    public Result ExecuteQuery(string query,
                               IDictionary<string, object?>? parameters = null)
    {
        lock (gate)
        {
            if (closed)
                throw new ObjectDisposedException(nameof(Driver));
            bool autocommit = !transaction;
            var run = Frame(0x10, query, parameters ?? new Dictionary<string, object?>(),
                            autocommit
                                ? new Dictionary<string, object?> { { "db", database } }
                                : new Dictionary<string, object?>());
            var pull = Frame(0x3f, new Dictionary<string, object?> { { "n", -1L } });
            return Exchange(() =>
            {
                Write(run);
                var meta = Success();
                var keys = ((List<object?>)meta["fields"]!).Cast<string>().ToList();
                var records = new List<Dictionary<string, object?>>();
                Write(pull);
                while (true)
                {
                    var m = Receive();
                    if (m.Tag == 0x70)
                    {
                        var summary = (Dictionary<string, object?>)m.Fields[0]!;
                        if (Equals(summary.GetValueOrDefault("has_more"), true))
                        {
                            Write(pull);
                            continue;
                        }
                        foreach (var e in summary)
                            meta[e.Key] = e.Value;
                        if (autocommit && summary.GetValueOrDefault("bookmark") is string b)
                            LastBookmark = b;
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
            });
        }
    }
    public Driver Begin(bool readOnly = false)
    {
        lock (gate)
        {
            if (transaction)
                throw new InvalidOperationException("Transaction already open");
            var begin = Frame(0x11, new Dictionary<string, object?> {
                { "db", database }, { "mode", readOnly ? "r" : "w" }
            });
            return Exchange(() =>
            {
                Write(begin);
                Success();
                transaction = true;
                return this;
            });
        }
    }
    private Dictionary<string, object?> Finish(byte tag)
    {
        lock (gate)
        {
            if (!transaction)
                throw new InvalidOperationException("No transaction");
            return Exchange(() =>
            {
                Write(Frame(tag));
                var meta = Success();
                transaction = false;
                if (tag == 0x12 && meta.GetValueOrDefault("bookmark") is string b)
                    LastBookmark = b;
                return meta;
            });
        }
    }
    /// <summary>Commits the open transaction. A <see cref="DatabaseException"/>
    /// means it did not commit; an I/O failure leaves the outcome
    /// unknown.</summary>
    public Dictionary<string, object?> Commit() => Finish(0x12);
    public void Rollback() => Finish(0x13);
    private void Close()
    {
        closed = true;
        transaction = false;
        stream?.Dispose();
        client.Dispose();
    }
    public void Dispose()
    {
        lock (gate)
        {
            Close();
        }
    }
}
