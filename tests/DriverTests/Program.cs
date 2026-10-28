using System.Globalization;
using System.Security.Authentication;
using Galactus;
void Check(bool b, string name) {
  if (!b)
    throw new Exception(name);
}
// DateTimeOffset.Equals compares instants only; the offset matters here too.
bool Same(object? a, object? b) =>
    a is DateTimeOffset x && b is DateTimeOffset y ? x.EqualsExact(y)
                                                   : Equals(a, b);
int codecCases = 0;
foreach (var line in File.ReadAllLines("tests/fixtures/values.tsv")) {
  var p = line.Split('\t');
  var b = Convert.FromHexString(p[1]);
  Check(PackStream.Encode(PackStream.Decode(b)).SequenceEqual(b), p[0]);
  Check(PackStream.Encode(PackStream.Decode(b), false).SequenceEqual(b), p[0]);
  codecCases++;
}
foreach (var line in File.ReadAllLines("tests/fixtures/bolt5.tsv")) {
  var p = line.Split('\t');
  var b = Convert.FromHexString(p[1]);
  Check(PackStream.Encode(PackStream.Decode(b), true).SequenceEqual(b),
        "bolt5 " + p[0]);
  codecCases++;
}
foreach (var line in File.ReadAllLines("tests/fixtures/datetime_pairs.tsv")) {
  var p = line.Split('\t');
  var f = Convert.FromHexString(p[0]);
  var i = Convert.FromHexString(p[1]);
  var fromF = PackStream.Decode(f);
  var fromI = PackStream.Decode(i);
  Check(Same(fromF, fromI), "datetime pair decode " + line);
  Check(PackStream.Encode(fromF, false).SequenceEqual(f) &&
            PackStream.Encode(fromI, false).SequenceEqual(f),
        "datetime pair 4.4 " + line);
  Check(PackStream.Encode(fromF, true).SequenceEqual(i) &&
            PackStream.Encode(fromI, true).SequenceEqual(i),
        "datetime pair bolt5 " + line);
  codecCases++;
}
// 4.4 graph values fill element ids from the integer id.
var legacyNode = (Node)PackStream.Decode(
    Convert.FromHexString("B34E2A90A0"))!;
Check(legacyNode.ElementId == "42", "4.4 element id");
Check(PackStream.Encode(12.30m).SequenceEqual(PackStream.Encode("12.30")),
      "decimal encoding");
Check(PackStream.Encode(-0.000001m).SequenceEqual(PackStream.Encode("-0.000001")),
      "decimal plain text");
var zonedLocal = ZonedDateTime.FromLocal(10, 0, "Europe/London");
var zonedUtc = ZonedDateTime.FromUtc(10, 0, "Europe/London");
Check(!zonedLocal.Equals(zonedUtc) &&
          zonedLocal.Equals(new ZonedDateTime(10, 0, "Europe/London")),
      "zoned basis equality");
Check(PackStream.Encode(zonedUtc, false)[1] == 0x69 &&
          PackStream.Encode(zonedLocal, true)[1] == 0x66,
      "zoned basis encoding");
foreach (var h in File.ReadAllLines("tests/fixtures/malformed.tsv")) {
  bool failed = false;
  try {
    PackStream.Decode(Convert.FromHexString(h));
  } catch {
    failed = true;
  }
  Check(failed, "malformed");
  codecCases++;
}
Console.WriteLine($"C# codec passed ({codecCases} fixture cases)");
var uri = Environment.GetEnvironmentVariable("GDB_TEST_URI");
if (uri is null) {
  Console.WriteLine("C# live skipped");
  return;
}
var password = Environment.GetEnvironmentVariable("GDB_TEST_PASSWORD")!;
var expectBolt = Environment.GetEnvironmentVariable("GDB_TEST_EXPECT_BOLT");
var caFile = Environment.GetEnvironmentVariable("GDB_TEST_CA_FILE");
if (caFile == "")
  caFile = null;
Driver Open(string pw) =>
    new(uri, "gdb", pw, trustedCertificates: caFile);
Dictionary<string, object?> P(string k, object? v) => new() { { k, v } };
using (var d = Open(password)) {
  Check(expectBolt is null || d.ProtocolVersion == expectBolt,
        $"protocol {d.ProtocolVersion}, expected {expectBolt}");
  Check(!string.IsNullOrEmpty(d.ServerAgent) && !string.IsNullOrEmpty(d.ConnectionId),
        "server agent and connection id");
  bool bolt5 = d.ProtocolVersion.StartsWith("5.");
  var dt = new DateTimeOffset(1960, 1, 1, 1, 2, 3, TimeSpan.FromMinutes(330))
               .AddTicks(4567891);
  var v = new Dictionary<string, object?> { { "n", long.MaxValue },
                                            { "b", new byte[] { 0, 255 } },
                                            { "s", new string('x', 70000) },
                                            { "t", dt } };
  var got = (Dictionary<string, object?>)d
                .ExecuteQuery("RETURN $v AS v", P("v", v))
                .Records[0]["v"]!;
  Check(Equals(got["n"], long.MaxValue) &&
            ((byte[])got["b"]!).SequenceEqual((byte[])v["b"]!) &&
            Equals(got["s"], v["s"]) && Same(got["t"], dt),
        "native mapping");
  // Offset datetimes, native and nanosecond wrapper, both offset signs.
  foreach (var value in new object[] {
             new DateTimeOffset(2026, 10, 28, 9, 30, 15, TimeSpan.FromHours(-7))
                 .AddTicks(1234567),
             new OffsetDateTime(1_800_000_000, 123_456_789, 19800) }) {
    var echoed = d.ExecuteQuery("RETURN $v AS v", P("v", value)).Records[0]["v"];
    Check(Same(echoed, value), $"offset datetime {value}");
  }
  // Named zone: 12:00 London on 01/07/2026 is 11:00 UTC (BST). The server
  // returns f (local) on 4.4 and i (UTC) on Bolt 5, from either basis.
  long londonLocal = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)
                         .ToUnixTimeSeconds();
  var expectedZoned =
      bolt5 ? ZonedDateTime.FromUtc(londonLocal - 3600, 5, "Europe/London")
            : ZonedDateTime.FromLocal(londonLocal, 5, "Europe/London");
  foreach (var value in new[] {
             ZonedDateTime.FromLocal(londonLocal, 5, "Europe/London"),
             ZonedDateTime.FromUtc(londonLocal - 3600, 5, "Europe/London") }) {
    var echoed = d.ExecuteQuery("RETURN $v AS v", P("v", value)).Records[0]["v"];
    Check(Equals(echoed, expectedZoned), $"named-zone datetime {value} -> {echoed}");
  }
  // Element ids are the decimal id text on every protocol.
  var graph = d.ExecuteQuery(
                   "CREATE (a:DriverCsharpEl)-[r:DRIVER_CSHARP_REL]->(b:DriverCsharpEl) " +
                   "RETURN a, r, b, id(a) AS ia, id(r) AS ir, id(b) AS ib, " +
                   "elementId(a) AS ea")
                  .Records[0];
  string Text(object? id) => ((long)id!).ToString(CultureInfo.InvariantCulture);
  var na = (Node)graph["a"]!;
  var rel = (Relationship)graph["r"]!;
  Check(na.ElementId == Text(graph["ia"]) && Equals(graph["ea"], na.ElementId) &&
            ((Node)graph["b"]!).ElementId == Text(graph["ib"]),
        "node element ids");
  Check(rel.ElementId == Text(graph["ir"]) && rel.StartElementId == Text(graph["ia"]) &&
            rel.EndElementId == Text(graph["ib"]),
        "relationship element ids");
  // Decimals travel as exact text.
  Check(Equals(d.ExecuteQuery("RETURN decimal($d) AS d", P("d", 12.30m))
                   .Records[0]["d"],
               "12.30"),
        "decimal");
  foreach (var line in File.ReadAllLines("tests/fixtures/spatial.tsv")) {
    var p = line.Split('\t');
    var s = (Spatial)d
                .ExecuteQuery(
                    "RETURN spatial.fromWKT($wkt,{domain:$domain}) AS shape",
                    new Dictionary<string, object?> { { "domain", p[0] },
                                                      { "wkt", p[1] } })
                .Records[0]["shape"]!;
    var row =
        d.ExecuteQuery(
             "RETURN spatial.fromMap($s) AS shape, $nested AS nested",
             new Dictionary<string, object?> {
               { "s", s },
               { "nested",
                 new object[] { new Dictionary<string, object?> { { "shape",
                                                                    s } } } }
             })
            .Records[0];
    var echoed = (Spatial)row["shape"]!;
    var nested = (Spatial)((Dictionary<string, object?>)((
        List<object?>)row["nested"]!)[0]!)["shape"]!;
    Check(s.Wkb.SequenceEqual(echoed.Wkb) && s.Wkb.SequenceEqual(nested.Wkb) &&
              s.Domain == echoed.Domain && s.Srid == echoed.Srid &&
              s.Layout == echoed.Layout,
          line);
  }
  d.Begin();
  d.ExecuteQuery("CREATE (:DriverCsharp {n:1})");
  d.Rollback();
  Check(Equals(d.ExecuteQuery("MATCH (n:DriverCsharp) RETURN count(n) AS n")
                   .Records[0]["n"],
               0L),
        "rollback");
  d.Begin();
  d.ExecuteQuery("CREATE (:DriverCsharp {n:2})");
  d.Commit();
  Check(Equals(((Node)d.ExecuteQuery("MATCH (n:DriverCsharp) RETURN n")
                    .Records[0]["n"]!)
                   .Properties["n"],
               2L),
        "commit");
  // A database error leaves the same driver usable.
  DatabaseException? error = null;
  try {
    d.ExecuteQuery("INVALID QUERY");
  } catch (DatabaseException e) {
    error = e;
  }
  Check(error is not null && error.Code.Length > 0 && error.IsClientError &&
            error.Classification == "ClientError" && error.Category.Length > 0 &&
            error.Title.Length > 0 && !error.IsRetryable && !error.IsTransient &&
            !error.IsDatabaseError,
        "database error classification " + error?.Code);
  Check(Equals(d.ExecuteQuery("RETURN 1 AS one").Records[0]["one"], 1L),
        "recovery after database error");
  // A failure inside a transaction closes it; autocommit then works.
  d.Begin();
  d.ExecuteQuery("CREATE (:DriverCsharpTx {n:1})");
  bool txFailed = false;
  try {
    d.ExecuteQuery("INVALID QUERY");
  } catch (DatabaseException) {
    txFailed = true;
  }
  bool txClosed = false;
  try {
    d.Commit();
  } catch (InvalidOperationException) {
    txClosed = true;
  }
  Check(txFailed && txClosed, "transaction closed after failure");
  Check(Equals(d.ExecuteQuery("MATCH (n:DriverCsharpTx) RETURN count(n) AS n")
                   .Records[0]["n"],
               0L),
        "transaction rolled back after failure");
  // Constraint violations carry a published code.
  d.ExecuteQuery("CREATE CONSTRAINT FOR (n:DriverCsharp) REQUIRE n.key IS UNIQUE");
  d.ExecuteQuery("CREATE (:DriverCsharp {key:'same'})");
  DatabaseException? violation = null;
  try {
    d.ExecuteQuery("CREATE (:DriverCsharp {key:'same'})");
  } catch (DatabaseException e) {
    violation = e;
  }
  Check(violation?.Code == ErrorCodes.ConstraintValidationFailed &&
            violation.Title == "ConstraintValidationFailed" && violation.IsClientError,
        "constraint violation " + violation?.Code);
  Check(Equals(d.ExecuteQuery("RETURN 1 AS one").Records[0]["one"], 1L),
        "recovery after constraint violation");
}
bool authFailed = false;
try {
  using var bad = Open("wrong-password");
} catch (DatabaseException) {
  authFailed = true;
}
Check(authFailed, "auth failure");
if (caFile is not null) {
  bool untrusted = false;
  try {
    using var plain = new Driver(uri, "gdb", password);
  } catch (AuthenticationException) {
    untrusted = true;
  }
  Check(untrusted, "TLS without trusted certificates must fail verification");
  bool misconfigured = false;
  try {
    using var wrong = new Driver(uri.Replace("bolt+s://", "bolt://"), "gdb",
                                 password, trustedCertificates: caFile);
  } catch (ArgumentException) {
    misconfigured = true;
  }
  Check(misconfigured, "trusted certificates on bolt:// must be rejected");
}
Console.WriteLine($"C# live tests passed (Bolt {expectBolt ?? "any"}" +
                  (caFile is null ? ")" : ", TLS)"));
