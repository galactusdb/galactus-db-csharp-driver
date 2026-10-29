# Galactus DB C# Driver

[Galactus DB](https://galactusdb.com) · [Driver catalogue](https://galactusdb.com/product/drivers) · [Type mapping](docs/BLUEPRINT.md) · [Spatial guide](docs/SPATIAL.md) · [Changelog](CHANGELOG.md) · [Issues](https://github.com/galactusdb/galactus-db-csharp-driver/issues)

Connect C# applications to Galactus DB with parameterised Cypher,
native values, and explicit transactions. The driver speaks Bolt 5.4 directly,
falls back to Bolt 4.4 when a server offers nothing newer, encrypts with the
server's own TLS certificate through `SslStream`, and has **zero third-party
runtime package dependencies**.

**Status: 0.2 / early access.** Source is available on `main`. This is a working,
tested baseline with one connection per driver and eager results. Install from
source as shown below, and read the [implementation overview](docs/OVERVIEW.md)
for the precise scope.

## Contents

- [Quickstart](#quickstart)
- [Connection configuration](#connection-configuration)
- [Queries and results](#queries-and-results)
- [Transactions](#transactions)
- [Native type mapping](#native-type-mapping)
- [Spatial values](#spatial-values)
- [Errors and connection lifecycle](#errors-and-connection-lifecycle)
- [Testing](#testing)
- [Scope](#scope)

## Quickstart

### 1. Get the source

Requirements: **.NET 6+ API surface**, Git, and a reachable Galactus DB instance.

```sh
git clone --branch main https://github.com/galactusdb/galactus-db-csharp-driver.git
cd galactus-db-csharp-driver
```

Run this from your application directory, using the path to your checkout.

```sh
dotnet add reference /path/to/galactus-db-csharp-driver/Galactus.Driver.csproj
```

Git-based installs follow `main`; pin a reviewed commit in your application for
reproducible builds. No npm, NuGet, PyPI, Maven Central or crates.io availability
is implied by the package names.

### 2. Configure your database credentials

Start Galactus DB on your infrastructure using the setup available from
[galactusdb.com](https://galactusdb.com). The examples use
`bolt://127.0.0.1:7687` and username `gdb`; replace them with your server settings.
Set your configured password in the application environment:

```sh
# Bash / zsh
export GDB_PASSWORD='your-database-password'
```

```powershell
# PowerShell
$env:GDB_PASSWORD = 'your-database-password'
```

`GDB_PASSWORD` is read by these examples; the driver does not automatically load
environment variables or change the server password. Keep credentials out of
source control and connection URIs.

### 3. Run your first query

```csharp
using Galactus;

using var driver = new Driver("bolt://127.0.0.1:7687", "gdb",
    Environment.GetEnvironmentVariable("GDB_PASSWORD")!);
var result = driver.ExecuteQuery("RETURN $name AS name",
    new Dictionary<string, object?> { ["name"] = "Ada" });
Console.WriteLine(result.Records[0]["name"]);
Console.WriteLine(driver.ProtocolVersion); // "5.4"
```

The following examples reuse an open `driver` within the same resource scope.
Adapt error handling to your application.

## Connection configuration

```csharp
new Driver(uri, username, password,
           database: "", timeoutMilliseconds: 30000, trustedCertificates: null)
```

Close with `using` / `Dispose()`; use a supported .NET runtime for deployment.

| Setting | Behaviour |
|---|---|
| URI | `bolt://host:port` (plaintext) or `bolt+s://host:port` (verified TLS); omitted port is 7687 |
| Authentication | Required username and password; no credentials in the URI |
| Database | Empty/omitted database delegates selection to the server's configured default |
| Named database | Supply an existing database name explicitly, for example `app` |
| Timeout | Milliseconds; 30 seconds by default |
| Trusted certificates | Optional path to a PEM file for `bolt+s://`; see below |
| Result handling | Records are buffered in memory until the query completes |

The driver proposes Bolt 5.4 down to 5.0, then 4.4, and uses the newest version
the server agrees. From Bolt 5.1 it greets with HELLO and authenticates with a
separate LOGON. After connecting, the driver exposes:

| Property | Value |
|---|---|
| `ProtocolVersion` | Agreed Bolt version, for example `"5.4"` or `"4.4"` |
| `ServerAgent` | The server's agent string, `GDB/` followed by the server version |
| `ConnectionId` | The server's id for this connection |
| `LastBookmark` | The bookmark from the latest autocommit query or commit, when the server sends one |

### TLS

A Galactus server encrypts connections itself when it is started with
`GDB_TLS_CERT` and `GDB_TLS_KEY`, on its usual port. Connect with `bolt+s://`.
The driver checks the certificate chain and that the certificate names the host
in the URI, and sends that host name for SNI. It never falls back to plaintext.

Trust comes from the operating system's certificate store. For a private CA or a
self-signed development certificate, pass a PEM file holding one or more
trusted certificates. They replace the system store for that driver, and the
host name check still applies:

```csharp
using var driver = new Driver("bolt+s://db.example.internal:7687", "gdb",
    Environment.GetEnvironmentVariable("GDB_PASSWORD")!,
    trustedCertificates: "/etc/galactus/ca.pem");
```

Setting `trustedCertificates` on a `bolt://` URI throws `ArgumentException`.
`bolt+ssc://` and the routing schemes (`neo4j://`, `neo4j+s://`, `neo4j+ssc://`)
are rejected.

Timeouts bound socket waits, not necessarily total query duration or DNS lookup.
Calls are synchronised. A transaction owns the connection, so use separate
drivers for concurrent units of work.

## Queries and results

Bind data as parameters, never interpolate user input into Cypher:

```csharp
var result = driver.ExecuteQuery(
    "CREATE (p:Person {name:$name, age:$age}) RETURN p.name AS name, p.age AS age",
    new Dictionary<string, object?> { ["name"] = "Ada", ["age"] = 37L });
Console.WriteLine(result.Records[0]["name"]);
Console.WriteLine(result.Summary["db"]);
```

Results expose column keys, records indexed by column name, and server summary
metadata. Read a column with `result.Records[0]["name"]`. Use unique column aliases because
records are maps. Summary fields can include the database, query classification,
timings, bookmark and update statistics; only fields supplied by the server are
present. A query without rows still returns its summary.

Lists and maps can be nested. Values are recursively encoded and hydrated,
including graph properties and spatial envelopes. Use Cypher `LIMIT` or
application-level pagination for large result sets: results are read in full
before `ExecuteQuery` returns.

## Transactions

Queries outside an explicit transaction autocommit. Group related writes with
begin/commit; explicitly roll back when abandoning work:

```csharp
driver.Begin();
driver.ExecuteQuery("CREATE (:Person {name:$name})", new Dictionary<string, object?> { ["name"] = "Ada" });
driver.ExecuteQuery("CREATE (:Person {name:$name})", new Dictionary<string, object?> { ["name"] = "Grace" });
driver.Commit();
// To deliberately abandon an open transaction: driver.Rollback();
```

The transaction belongs to the connection. Keep both statements on the same
driver and finish the transaction before reusing it for unrelated work. Closing
or disposing a connection rolls back an unfinished transaction; it never
implicitly commits. The read-only argument requests a read transaction.

When a query inside a transaction fails with a database error, the server rolls
the transaction back and the driver closes it, so the next query autocommits.
Start a new transaction to try the work again. A `DatabaseException` from
`Commit()` means the transaction did not commit. A lost connection during
`Commit()` leaves the outcome unknown; reconcile the data before retrying a
write.

## Native type mapping

`long`, `double`, `bool`, `string`, `byte[]`, `List<object?>`, and `Dictionary<string,object?>`. `DateOnly`, `TimeOnly`, `DateTimeOffset`, UTC/Unspecified `DateTime`, and `TimeSpan` inputs work directly. Values finer than .NET tick precision retain a lossless wrapper. Convert Local DateTime explicitly to DateTimeOffset.

- Database integers are signed 64-bit; out-of-range integer inputs fail.
- Bytes stay binary; values do not pass through JSON.
- Temporal wrappers preserve nanoseconds, offsets and named zones when the
  runtime's native types cannot represent the full value.
- Offset datetimes are sent in the agreed protocol's form and read back as the
  same `DateTimeOffset` on Bolt 5 and Bolt 4.4.
- `ZonedDateTime` keeps the IANA zone name and records whether its seconds are
  local wall-clock seconds (`ZonedDateTime.FromLocal`, or the three-argument
  constructor) or UTC seconds (`ZonedDateTime.FromUtc`); see `Basis`. Values the
  server returns on Bolt 5 are UTC-based, and on Bolt 4.4 local-based.
- Calendar duration keeps months, days, seconds and nanoseconds separately.
- Graph nodes, relationships and paths have named result types. `Node`,
  `Relationship` and `UnboundRelationship` expose `ElementId`, and a
  relationship also has `StartElementId` and `EndElementId`; these match
  Cypher's `elementId()`. Bind IDs or properties as parameters; the graph
  entities themselves are result-only.
- `DECIMAL` values arrive as strings holding their exact digits. A C# `decimal`
  parameter is sent as its exact invariant-culture text, ready for
  `decimal($param)` in Cypher.
- Unknown wire structures remain explicit structures and cannot be used as
  generic application-object serialisation.

```csharp
var price = driver.ExecuteQuery("RETURN decimal($p) AS price",
    new Dictionary<string, object?> { ["p"] = 12.30m }).Records[0]["price"];
// "12.30"; parse it with System.Globalization.CultureInfo.InvariantCulture
var total = decimal.Parse((string)price!, CultureInfo.InvariantCulture);
```

No ORM or automatic arbitrary-object mapping is included. Convert application
models and UUIDs explicitly to a supported value.
See the [complete type mapping](docs/BLUEPRINT.md) for precision and range details.

## Spatial values

The driver supports `Point2D`, `Point3D`, and a native `Spatial` value covering
every shape family Galactus DB supports:

| Shape family | Geometry | Geography |
|---|---|---|
| Point / LineString / Polygon, including holes | Yes | Yes |
| MultiPoint / MultiLineString / MultiPolygon | Yes | Yes |
| GeometryCollection, including nested collections | Yes | Yes |
| XY, XYZ, and typed empty shapes | Yes | Yes |

`Spatial` preserves WKB bytes together with domain, SRID, coordinate layout and
model. Geometry uses `planar`; geography uses `greatCircle`. Neither domain nor
SRID is inferred by converting through GeoJSON.

```csharp
var shape = (Spatial)driver.ExecuteQuery(
    "RETURN spatial.fromWKT($wkt, {domain:$domain}) AS shape",
    new Dictionary<string, object?> {
        ["wkt"] = "POLYGON((0 0,4 0,4 4,0 4,0 0))", ["domain"] = "geometry"
    }).Records[0]["shape"]!;
driver.ExecuteQuery(
    "CREATE (:Region {name:$name, shape:spatial.fromMap($shape)})",
    new Dictionary<string, object?> { ["name"] = "Square", ["shape"] = shape });
Console.WriteLine($"{shape.Domain} / {shape.Srid} / {shape.Layout}");
```

Binding `Spatial` sends a lossless envelope. Use `spatial.fromMap($shape)` to
promote it into a database spatial value. `RETURN $shape` alone echoes the map;
it does not change the server's value type. To import external bytes or text,
use `spatial.fromWKB`, `spatial.fromWKT`, or `spatial.fromGeoJSON` with parameters.
The server performs geometry and topology validation. See the
[spatial guide](docs/SPATIAL.md) for the shapes the database stores.

## Errors and connection lifecycle

A database error, such as a syntax error or a broken constraint, raises
`DatabaseException` and leaves the driver ready for the next query: the driver
resets the connection before it throws. The exception keeps the server's
`Code`, `Message` and `Metadata`, and splits the code into `Classification`,
`Category` and `Title`, with `IsClientError`, `IsTransient`, `IsDatabaseError`
and `IsRetryable` (true for transient errors only). The driver never retries by
itself.

`ErrorCodes` names the codes Galactus publishes, so you compare against a
constant rather than a typed string:

```csharp
try
{
    driver.ExecuteQuery("CREATE (:Account {email:$email})",
        new Dictionary<string, object?> { ["email"] = "ada@example.com" });
}
catch (DatabaseException e) when (e.Code == ErrorCodes.ConstraintValidationFailed)
{
    Console.WriteLine("That email address is already registered.");
}
```

The driver closes the connection when reading or writing fails (I/O, timeout or
TLS), when the server breaks the protocol, and when authentication fails. Open a
new driver after one of those.

| Symptom | Check |
|---|---|
| Connection refused | Host, port, listener and network reachability |
| Authentication rejected | Database username and configured password |
| Database not found | Explicit database name; omit it to use the server default |
| TLS negotiation fails | `bolt+s://` against a server with a certificate, the trust chain (`trustedCertificates` for a private CA) and the host name in the certificate |
| Unsupported parameter | Convert the object to a supported scalar/container or named value |
| `ObjectDisposedException` | The connection was closed after an I/O, TLS or protocol failure; open a new driver |

Credentials and parameters are not logged by the driver. Avoid writing them to
application logs when reporting errors. The driver performs no automatic write
retries.

## Testing

The project targets `net6.0`, so build it with a .NET 6 SDK. When a newer SDK
is also installed, run `dotnet` from a folder whose `global.json` pins 6.0.x
(for example `{"sdk":{"version":"6.0.411","rollForward":"latestPatch"}}`);
otherwise the newer SDK is selected. Then, from the repository root:

```sh
dotnet build tests/DriverTests/Test.csproj
dotnet tests/DriverTests/bin/Debug/net6.0/Test.dll
```

The codec tests round-trip the shared wire fixtures in Bolt 4.4 and Bolt 5
form and check that each datetime decodes the same from its 4.4 and Bolt 5
encodings. Without `GDB_TEST_URI`, live database tests are skipped. For
integration tests, use a fresh disposable server and set `GDB_TEST_URI` and
`GDB_TEST_PASSWORD`; tests create data and exercise transactions, recovery
after errors, constraints and TLS. Reset the disposable database before
repeating the suite.

See [test instructions](tests/README.md) for the environment variables and the
three live passes (Bolt 5.4, Bolt 4.4 and TLS).

## Scope

This release provides direct connections over Bolt 5.4 or 4.4, basic
authentication, verified TLS, parameterised queries, eager records and
summaries, explicit transactions, recovery after database errors, classified
errors, native type mapping, and full supported spatial interchange. Each
driver owns one connection. Messages are capped at 64 MiB and nesting at 64
levels; aggregate result memory can exceed a single message.

Windows builds and real-server interoperability have been tested. See
[the shared blueprint](docs/BLUEPRINT.md) and
[implementation overview](docs/OVERVIEW.md) for the precise contract and scope.

## Feedback and contribution

Use [GitHub issues](https://github.com/galactusdb/galactus-db-csharp-driver/issues) for reproducible
bugs and focused feature requests. Include runtime/OS versions, driver commit,
expected behaviour and a small reproduction with credentials and sensitive
data removed. Keep changes dependency-free and run the relevant tests before
opening a pull request against `main`.

Visit [galactusdb.com](https://galactusdb.com) for Galactus DB, or browse the
[other language drivers](https://galactusdb.com/product/drivers).

## Licence

Apache License 2.0. See [LICENSE](LICENSE).
