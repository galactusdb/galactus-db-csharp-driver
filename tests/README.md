# galactus-db-csharp-driver tests

[Galactus DB website](https://galactusdb.com) · [Driver Quickstart](../README.md)

## Quickstart

The project targets `net6.0` and must be built with a .NET 6 SDK (6.0.411 was
used in local verification). If a newer SDK is installed too, `dotnet` picks
the newest one unless a `global.json` pins 6.0.x, and a newer SDK fails to
restore the 6.0 packs. Run `dotnet` from a folder holding a pin such as:

```json
{"sdk":{"version":"6.0.411","rollForward":"latestPatch"}}
```

With only a .NET 6 SDK installed, run from the repository root:

```sh
dotnet build tests/DriverTests/Test.csproj
dotnet tests/DriverTests/bin/Debug/net6.0/Test.dll
```

The test program reads `tests/fixtures`, so run the built `Test.dll` from the
repository root. The fixtures are included; no sibling repository is needed.
The tests use standard runtime facilities, with no test framework packages.

- `values.tsv`: integer boundaries, UTF-8, bytes, temporal values, graph values,
  points and unknown structures; each round-trips in Bolt 4.4 mode.
- `bolt5.tsv`: Bolt 5 forms (nodes and relationships with element ids,
  unbound relationships, `I`, `i` and `f` datetimes); each round-trips in Bolt 5
  mode.
- `datetime_pairs.tsv`: the same offset datetime as a Bolt 4.4 `F` (local
  seconds) and a Bolt 5 `I` (UTC seconds). Both decode to equal values, and
  each mode encodes its own column.
- `malformed.tsv`: truncated, invalid and oversized inputs that must be rejected.
- `spatial.tsv`: 57 geometry/geography cases, including XY/XYZ, empties, holes,
  multipart shapes, nested collections, and antimeridian geography.

### Live database tests

Without `GDB_TEST_URI`, live tests are skipped. To include them, start a **fresh,
disposable** Galactus database and set these environment variables before running
the same test command:

| Variable | Meaning |
|---|---|
| `GDB_TEST_URI` | `bolt://host:port` or `bolt+s://host:port` |
| `GDB_TEST_PASSWORD` | Password for user `gdb` on the disposable server |
| `GDB_TEST_EXPECT_BOLT` | Optional; the protocol the driver must agree, `5.4` or `4.4` |
| `GDB_TEST_CA_FILE` | Optional; a PEM CA for a `bolt+s://` server, passed as `trustedCertificates` |

```sh
export GDB_TEST_URI='bolt://127.0.0.1:7687'
export GDB_TEST_PASSWORD='your-disposable-database-password'
```

```powershell
$env:GDB_TEST_URI = 'bolt://127.0.0.1:7687'
$env:GDB_TEST_PASSWORD = 'your-disposable-database-password'
```

The live suite checks the agreed protocol, server agent and connection id;
native values, offset and named-zone datetimes and 57 spatial cases through
`RETURN $v`; element ids against `id()` and `elementId()`; `decimal($d)`;
commit and rollback; recovery on the same driver after a failed query and
after a failure inside a transaction; a unique constraint violation reported
as `ErrorCodes.ConstraintValidationFailed`; and rejected authentication. With
`GDB_TEST_CA_FILE` set it also checks that the server's certificate is refused
without the trusted certificate file and that the option is rejected on a
`bolt://` URI.

Tests use username `gdb` and the server's default database, and create nodes
labelled `DriverCsharp*` plus a unique constraint. Do not point them at
valuable data, and reset the disposable database between runs.

The shared driver workspace runs every language through `tests/run_live.py`,
which starts disposable servers for three passes: Bolt 5.4, a server capped at
Bolt 4.4 with `GDB_BOLT_MAX_VERSION=4.4`, and TLS with `GDB_TLS_CERT` and
`GDB_TLS_KEY`. See [implementation scope](../docs/OVERVIEW.md#scope-of-02).

Database information: [Galactus DB website](https://galactusdb.com).
