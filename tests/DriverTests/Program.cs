using Galactus;
void Check(bool b, string name) {
  if (!b)
    throw new Exception(name);
}
foreach (var line in File.ReadAllLines("tests/fixtures/values.tsv")) {
  var p = line.Split('\t');
  var b = Convert.FromHexString(p[1]);
  Check(PackStream.Encode(PackStream.Decode(b)).SequenceEqual(b), p[0]);
}
foreach (var h in File.ReadAllLines("tests/fixtures/malformed.tsv")) {
  bool failed = false;
  try {
    PackStream.Decode(Convert.FromHexString(h));
  } catch {
    failed = true;
  }
  Check(failed, "malformed");
}
var uri = Environment.GetEnvironmentVariable("GDB_TEST_URI");
if (uri is null) {
  Console.WriteLine("C# codec passed; live skipped");
  return;
}
using (var d = new Driver(
           uri, "gdb",
           Environment.GetEnvironmentVariable("GDB_TEST_PASSWORD")!)) {
  var dt = new DateTimeOffset(1960, 1, 1, 1, 2, 3, TimeSpan.FromMinutes(330))
               .AddTicks(4567891);
  var v = new Dictionary<string, object?> { { "n", long.MaxValue },
                                            { "b", new byte[] { 0, 255 } },
                                            { "s", new string('x', 70000) },
                                            { "t", dt } };
  var got = (Dictionary<string, object?>)d
                .ExecuteQuery("RETURN $v AS v",
                              new Dictionary<string, object?> { { "v", v } })
                .Records[0]["v"]!;
  Check(Equals(got["n"], long.MaxValue) &&
            ((byte[])got["b"]!).SequenceEqual((byte[])v["b"]!) &&
            Equals(got["s"], v["s"]) && Equals(got["t"], dt),
        "native mapping");
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
  bool failed = false;
  try {
    d.ExecuteQuery("INVALID QUERY");
  } catch (DatabaseException) {
    failed = true;
  }
  Check(failed, "database error");
}
bool authFailed = false;
try {
  using var bad = new Driver(uri, "gdb", "wrong-password");
} catch (DatabaseException) {
  authFailed = true;
}
Check(authFailed, "auth failure");
Console.WriteLine("C# codec and live tests passed");
