# Changelog

## 0.2.0 (28/10/2026)

- Speaks Bolt 5.0 to 5.4 and falls back to Bolt 4.4. From Bolt 5.1 the driver
  sends `bolt_agent` with HELLO and authenticates with LOGON.
- New `Driver` properties: `ProtocolVersion`, `ServerAgent`, `ConnectionId` and
  `LastBookmark`.
- A database error no longer closes the connection: the driver resets it and
  stays usable. A failure inside a transaction closes that transaction. I/O,
  TLS, protocol and authentication failures still close the connection.
- `DatabaseException` adds `Classification`, `Category`, `Title`,
  `IsClientError`, `IsTransient`, `IsDatabaseError` and `IsRetryable`. The new
  `ErrorCodes` class names the codes Galactus publishes.
- `bolt+s://` connects to a Galactus server's own TLS listener and takes an
  optional `trustedCertificates` PEM file for a private CA; the host name is
  always checked. Setting it on `bolt://` is an error.
- `Node`, `Relationship` and `UnboundRelationship` expose `ElementId` (and
  `StartElementId` / `EndElementId`), filled from the integer id on Bolt 4.4.
- Offset datetimes use the Bolt 5 UTC form on 5.x and the local form on 4.4;
  all four datetime tags decode on any version. `ZonedDateTime` records a local
  or UTC `Basis` (`FromLocal`, `FromUtc`).
- `decimal` parameters are sent as exact invariant-culture text for
  `decimal($param)`.
- User agent is now `galactus-db-csharp-driver/0.2.0`.

## 0.1.0

- First release: Bolt 4.4, parameterised queries, explicit transactions,
  native type mapping and spatial values.
