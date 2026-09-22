# Galactus C# driver

[Galactus DB website](https://galactusdb.com) · [Source](https://github.com/galactusdb/galactus-db-csharp-driver) · [Type mapping](docs/BLUEPRINT.md) · [Spatial types](docs/SPATIAL.md)

Native Bolt 4.4 driver for Galactus DB. This experimental 0.1 implementation
has zero third-party runtime package dependencies. Source is available here;
no npm, NuGet, PyPI, Maven Central, or crates.io release is implied.

## How To

### 1. Get the driver

```sh
git clone --branch main https://github.com/galactusdb/galactus-db-csharp-driver.git
cd galactus-db-csharp-driver
```

.NET 6+ API surface; no runtime packages. Use a supported runtime for your application. From your application directory:

```sh
dotnet add reference /path/to/galactus-db-csharp-driver/Galactus.Driver.csproj
```

### 2. Configure your connection

Start or obtain a Galactus DB instance; visit [galactusdb.com](https://galactusdb.com)
for database information. Use its Bolt address (locally, `bolt://127.0.0.1:7687`),
username, and configured password. The example reads `GDB_PASSWORD` from your
environment; it is an application variable, not a command to change the server password.

```sh
# Bash / zsh
export GDB_PASSWORD='your-database-password'
```

```powershell
# PowerShell
$env:GDB_PASSWORD = 'your-database-password'
```

### 3. Execute a parameterised query

```csharp
using Galactus;

using var driver = new Driver("bolt://127.0.0.1:7687", "gdb",
    Environment.GetEnvironmentVariable("GDB_PASSWORD")!);
var result = driver.ExecuteQuery("RETURN $name AS name",
    new Dictionary<string, object?> { ["name"] = "Ada" });
Console.WriteLine(result.Records[0]["name"]);
```

Optional constructor arguments: `database = "neo4j"`,
`timeoutMilliseconds = 30000`. `bolt+s://` validates TLS with system trust.
`Begin(readOnly: false)`, `Commit()`, `Rollback()` and `Dispose()` manage lifecycle.
Dispose rolls back unfinished work; it never commits. Calls are synchronized,
but a transaction owns the connection, so use a driver per concurrent unit of work.

Native bool/integer/float/string/byte array, list and dictionary mapping is
recursive. DateOnly, TimeOnly, DateTimeOffset, UTC/Unspecified DateTime and
TimeSpan inputs work directly. Nanosecond values beyond .NET tick precision
return lossless records. Local DateTime requires explicit DateTimeOffset
conversion. Decimal/Guid/application classes require an explicit representation.

`Point2D`, `Point3D`, `Spatial`, `Node`, `Relationship`, `GraphPath`, `Duration`
and named temporal records are supplied. See [mapping](docs/BLUEPRINT.md),
[spatial values](docs/SPATIAL.md), and [scope](docs/OVERVIEW.md#scope-of-01).

### 4. Run the tests

From this repository's root:

```sh
dotnet run --project tests/DriverTests/Test.csproj
```

See [test instructions](tests/README.md) for prerequisites and opt-in live tests.
Connection failures discard the connection; writes are never automatically retried.
Results are eager and each driver owns one connection; see the documented scope.

Learn more at [Galactus DB website](https://galactusdb.com).
