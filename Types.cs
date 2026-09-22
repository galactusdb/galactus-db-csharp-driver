namespace Galactus;

public record Structure(byte Tag, params object?[] Fields);
public sealed record Node(long Id, List<object?> Labels,
                          Dictionary<string, object?> Properties);
public sealed record Relationship(long Id, long StartId, long EndId,
                                  string Type,
                                  Dictionary<string, object?> Properties);
public sealed
    record UnboundRelationship(long Id, string Type,
                               Dictionary<string, object?> Properties);
public sealed record GraphPath(List<object?> Nodes, List<object?> Relationships,
                               List<object?> Sequence);
public sealed record Date(long Days);
public sealed record LocalTime(long Nanoseconds);
public sealed record OffsetTime(long Nanoseconds, long OffsetSeconds);
public sealed record LocalDateTime(long Seconds, long Nanoseconds);
public sealed record OffsetDateTime(long Seconds, long Nanoseconds,
                                    long OffsetSeconds);
public sealed record ZonedDateTime(long Seconds, long Nanoseconds,
                                   string ZoneId);
public sealed record Duration(long Months, long Days, long Seconds,
                              long Nanoseconds);
public sealed record Point2D(long Srid, double X, double Y);
public sealed record Point3D(long Srid, double X, double Y, double Z);

internal static class Mapping
{
    internal static void ValidateParameters(object? v, int depth = 0)
    {
        if (depth >= 64) throw new ArgumentException("Nesting exceeds 64 levels"); v = Dehydrate(v);
        if (v is Structure s) { if (!new byte[] { 0x44, 0x74, 0x54, 0x64, 0x46, 0x66, 0x45, 0x58, 0x59 }.Contains(s.Tag)) throw new ArgumentException("Graph entities and unknown structures are result-only; pass properties or an ID"); foreach (var f in s.Fields) ValidateParameters(f, depth + 1); }
        else if (v is System.Collections.IDictionary m) { foreach (var f in m.Values) ValidateParameters(f, depth + 1); }
        else if (v is System.Collections.IList list && v is not byte[]) { foreach (var f in list) ValidateParameters(f, depth + 1); }
    }
    internal static object? Dehydrate(object? v)
    {
        switch (v)
        {
            case DateOnly d:
                return new Structure(
                    0x44, (long)d.DayNumber -
                              DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber);
            case TimeOnly t:
                return new Structure(0x74, t.Ticks * 100L);
            case DateTimeOffset d:
                return new Structure(
                    0x46,
                    (d.DateTime.Ticks - DateTime.UnixEpoch.Ticks) /
                            TimeSpan.TicksPerSecond -
                        FloorAdjustment(d.DateTime.Ticks - DateTime.UnixEpoch.Ticks),
                    (d.Ticks % TimeSpan.TicksPerSecond) * 100L,
                    (long)d.Offset.TotalSeconds);
            case DateTime d:
                if (d.Kind == DateTimeKind.Local)
                    throw new ArgumentException(
                        "Use DateTimeOffset for local DateTime values");
                long ticks = d.Ticks - DateTime.UnixEpoch.Ticks;
                return d.Kind == DateTimeKind.Utc
                           ? new Structure(0x46,
                                           ticks / TimeSpan.TicksPerSecond -
                                               FloorAdjustment(ticks),
                                           (d.Ticks % TimeSpan.TicksPerSecond) * 100L, 0L)
                           : new Structure(0x64,
                                           ticks / TimeSpan.TicksPerSecond -
                                               FloorAdjustment(ticks),
                                           (d.Ticks % TimeSpan.TicksPerSecond) * 100L);
            case TimeSpan t:
                return new Structure(0x45, 0L, 0L, t.Ticks / TimeSpan.TicksPerSecond,
                                     t.Ticks % TimeSpan.TicksPerSecond * 100L);
            case Node n:
                return new Structure(0x4e, n.Id, n.Labels, n.Properties);
            case Relationship r:
                return new Structure(0x52, r.Id, r.StartId, r.EndId, r.Type,
                                     r.Properties);
            case UnboundRelationship r:
                return new Structure(0x72, r.Id, r.Type, r.Properties);
            case GraphPath p:
                return new Structure(0x50, p.Nodes, p.Relationships, p.Sequence);
            case Date d:
                return new Structure(0x44, d.Days);
            case LocalTime t:
                return new Structure(0x74, t.Nanoseconds);
            case OffsetTime t:
                return new Structure(0x54, t.Nanoseconds, t.OffsetSeconds);
            case LocalDateTime t:
                return new Structure(0x64, t.Seconds, t.Nanoseconds);
            case OffsetDateTime t:
                return new Structure(0x46, t.Seconds, t.Nanoseconds, t.OffsetSeconds);
            case ZonedDateTime t:
                return new Structure(0x66, t.Seconds, t.Nanoseconds, t.ZoneId);
            case Duration d:
                return new Structure(0x45, d.Months, d.Days, d.Seconds, d.Nanoseconds);
            case Point2D p:
                return new Structure(0x58, p.Srid, p.X, p.Y);
            case Point3D p:
                return new Structure(0x59, p.Srid, p.X, p.Y, p.Z);
            case Spatial s:
                return s.ToMap();
            default:
                return v;
        }
    }
    private static long FloorAdjustment(long ticks) =>
        ticks < 0 && ticks % TimeSpan.TicksPerSecond != 0 ? 1 : 0;
    internal static object Hydrate(byte tag, object?[] f)
    {
        int expected = tag switch
        {
            0x4e or 0x72 or 0x50 or 0x46 or 0x66 or
                                    0x58 => 3,
            0x52 => 5,
            0x44 or
            0x74 => 1,
            0x54 or
            0x64 => 2,
            0x45 or
            0x59 => 4,
            _ => -1
        };
        if (expected >= 0 && f.Length != expected)
            throw new InvalidDataException("Invalid structure field count");
        long I(int i) => (long)f[i]!;
        object value = tag switch
        {
            0x4e => new Node(I(0), (List<object?>)f[1]!,
                             (Dictionary<string, object?>)f[2]!),
            0x52 => new Relationship(I(0), I(1), I(2), (string)f[3]!,
                                     (Dictionary<string, object?>)f[4]!),
            0x72 => new UnboundRelationship(I(0), (string)f[1]!,
                                            (Dictionary<string, object?>)f[2]!),
            0x50 => new GraphPath((List<object?>)f[0]!, (List<object?>)f[1]!,
                                  (List<object?>)f[2]!),
            0x44 => new Date(I(0)),
            0x74 => new LocalTime(I(0)),
            0x54 => new OffsetTime(I(0), I(1)),
            0x64 => new LocalDateTime(I(0), I(1)),
            0x46 => new OffsetDateTime(I(0), I(1), I(2)),
            0x66 => new ZonedDateTime(I(0), I(1), (string)f[2]!),
            0x45 => new Duration(I(0), I(1), I(2), I(3)),
            0x58 => new Point2D(I(0), (double)f[1]!, (double)f[2]!),
            0x59 => new Point3D(I(0), (double)f[1]!, (double)f[2]!, (double)f[3]!),
            _ => new Structure(tag, f)
        };
        try
        {
            if (tag == 0x44)
                return DateOnly.FromDateTime(DateTime.UnixEpoch.AddDays(I(0)));
            if (tag == 0x74 && I(0) % 100 == 0)
                return new TimeOnly(I(0) / 100);
            if ((tag == 0x64 || tag == 0x46) && I(1) % 100 == 0 && I(1) >= 0 &&
                I(1) < 1_000_000_000)
            {
                var local = DateTime.SpecifyKind(
                    DateTime.UnixEpoch.AddTicks(
                        checked(I(0) * TimeSpan.TicksPerSecond + I(1) / 100)),
                    DateTimeKind.Unspecified);
                if (tag == 0x64)
                    return local;
                if (I(2) % 60 == 0 && Math.Abs(I(2)) <= 14 * 3600)
                    return new DateTimeOffset(local, TimeSpan.FromSeconds(I(2)));
            }
        }
        catch (ArgumentException)
        {
        }
        catch (OverflowException)
        {
        }
        return value;
    }
}
