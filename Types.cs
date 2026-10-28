using System.Globalization;

namespace Galactus;

public record Structure(byte Tag, params object?[] Fields);
/// <summary>A returned node. <see cref="ElementId"/> is the Bolt 5 element id;
/// on Bolt 4.4 it is the decimal text of <see cref="Id"/>.</summary>
public sealed record Node(long Id, List<object?> Labels,
                          Dictionary<string, object?> Properties)
{
    public string ElementId { get; init; } =
        Id.ToString(CultureInfo.InvariantCulture);
}
public sealed record Relationship(long Id, long StartId, long EndId,
                                  string Type,
                                  Dictionary<string, object?> Properties)
{
    public string ElementId { get; init; } =
        Id.ToString(CultureInfo.InvariantCulture);
    public string StartElementId { get; init; } =
        StartId.ToString(CultureInfo.InvariantCulture);
    public string EndElementId { get; init; } =
        EndId.ToString(CultureInfo.InvariantCulture);
}
public sealed
    record UnboundRelationship(long Id, string Type,
                               Dictionary<string, object?> Properties)
{
    public string ElementId { get; init; } =
        Id.ToString(CultureInfo.InvariantCulture);
}
public sealed record GraphPath(List<object?> Nodes, List<object?> Relationships,
                               List<object?> Sequence);
public sealed record Date(long Days);
public sealed record LocalTime(long Nanoseconds);
public sealed record OffsetTime(long Nanoseconds, long OffsetSeconds);
public sealed record LocalDateTime(long Seconds, long Nanoseconds);
/// <summary>Offset datetime wrapper. <see cref="Seconds"/> are local epoch
/// seconds (UTC is Seconds - OffsetSeconds), whichever tag it arrived in.</summary>
public sealed record OffsetDateTime(long Seconds, long Nanoseconds,
                                    long OffsetSeconds);
/// <summary>Which epoch a <see cref="ZonedDateTime"/>'s seconds count
/// from.</summary>
public enum DateTimeBasis
{
    /// <summary>Local wall-clock epoch seconds in the zone (Bolt tag
    /// <c>f</c>).</summary>
    Local,
    /// <summary>UTC epoch seconds (Bolt tag <c>i</c>).</summary>
    Utc
}
/// <summary>Named-zone datetime. The three-argument constructor is
/// local-based, as in 0.1; use <see cref="FromUtc"/> for UTC seconds. The
/// value is encoded in its own basis on every protocol, and two values are
/// equal only when basis, seconds, nanoseconds and zone all match.</summary>
public sealed record ZonedDateTime(long Seconds, long Nanoseconds,
                                   string ZoneId)
{
    public DateTimeBasis Basis { get; init; } = DateTimeBasis.Local;
    public ZonedDateTime(long seconds, long nanoseconds, string zoneId,
                         DateTimeBasis basis)
        : this(seconds, nanoseconds, zoneId)
    {
        Basis = basis;
    }
    public static ZonedDateTime FromLocal(long localSeconds, long nanoseconds,
                                          string zoneId) =>
        new(localSeconds, nanoseconds, zoneId, DateTimeBasis.Local);
    public static ZonedDateTime FromUtc(long utcSeconds, long nanoseconds,
                                        string zoneId) =>
        new(utcSeconds, nanoseconds, zoneId, DateTimeBasis.Utc);
}
public sealed record Duration(long Months, long Days, long Seconds,
                              long Nanoseconds);
public sealed record Point2D(long Srid, double X, double Y);
public sealed record Point3D(long Srid, double X, double Y, double Z);

internal static class Mapping
{
    private static readonly byte[] ParameterTags = {
        0x44, 0x74, 0x54, 0x64, 0x46, 0x66, 0x49, 0x69, 0x45, 0x58, 0x59
    };
    internal static void ValidateParameters(object? v, bool bolt5,
                                            int depth = 0)
    {
        if (depth >= 64)
            throw new ArgumentException("Nesting exceeds 64 levels");
        v = Dehydrate(v, bolt5);
        if (v is Structure s)
        {
            if (!ParameterTags.Contains(s.Tag))
                throw new ArgumentException(
                    "Graph entities and unknown structures are result-only; pass properties or an ID");
            foreach (var f in s.Fields)
                ValidateParameters(f, bolt5, depth + 1);
        }
        else if (v is System.Collections.IDictionary m)
        {
            foreach (var f in m.Values)
                ValidateParameters(f, bolt5, depth + 1);
        }
        else if (v is System.Collections.IList list && v is not byte[])
        {
            foreach (var f in list)
                ValidateParameters(f, bolt5, depth + 1);
        }
    }
    /// <summary>Offset datetime from local seconds: <c>F</c> on 4.4,
    /// <c>I</c> (UTC seconds) on Bolt 5.</summary>
    private static Structure OffsetDateTimeStructure(long localSeconds,
                                                     long nanoseconds,
                                                     long offsetSeconds,
                                                     bool bolt5)
    {
        if (!bolt5)
            return new Structure(0x46, localSeconds, nanoseconds, offsetSeconds);
        try
        {
            return new Structure(0x49, checked(localSeconds - offsetSeconds),
                                 nanoseconds, offsetSeconds);
        }
        catch (OverflowException)
        {
            throw new ArgumentException("Datetime is outside the Bolt 5 range");
        }
    }
    private static long FloorSeconds(long ticks) =>
        ticks / TimeSpan.TicksPerSecond -
        (ticks < 0 && ticks % TimeSpan.TicksPerSecond != 0 ? 1 : 0);
    private static long SubsecondNanos(long ticks) =>
        (ticks % TimeSpan.TicksPerSecond + TimeSpan.TicksPerSecond) %
        TimeSpan.TicksPerSecond * 100L;
    internal static object? Dehydrate(object? v, bool bolt5)
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
            {
                long local = d.DateTime.Ticks - DateTime.UnixEpoch.Ticks;
                return OffsetDateTimeStructure(FloorSeconds(local),
                                               SubsecondNanos(local),
                                               (long)d.Offset.TotalSeconds, bolt5);
            }
            case DateTime d:
            {
                if (d.Kind == DateTimeKind.Local)
                    throw new ArgumentException(
                        "Use DateTimeOffset for local DateTime values");
                long ticks = d.Ticks - DateTime.UnixEpoch.Ticks;
                return d.Kind == DateTimeKind.Utc
                           ? OffsetDateTimeStructure(FloorSeconds(ticks),
                                                     SubsecondNanos(ticks), 0L, bolt5)
                           : new Structure(0x64, FloorSeconds(ticks),
                                           SubsecondNanos(ticks));
            }
            case TimeSpan t:
                return new Structure(0x45, 0L, 0L, t.Ticks / TimeSpan.TicksPerSecond,
                                     t.Ticks % TimeSpan.TicksPerSecond * 100L);
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case Node n:
                return bolt5 ? new Structure(0x4e, n.Id, n.Labels, n.Properties,
                                             n.ElementId)
                             : new Structure(0x4e, n.Id, n.Labels, n.Properties);
            case Relationship r:
                return bolt5 ? new Structure(0x52, r.Id, r.StartId, r.EndId, r.Type,
                                             r.Properties, r.ElementId,
                                             r.StartElementId, r.EndElementId)
                             : new Structure(0x52, r.Id, r.StartId, r.EndId, r.Type,
                                             r.Properties);
            case UnboundRelationship r:
                return bolt5 ? new Structure(0x72, r.Id, r.Type, r.Properties,
                                             r.ElementId)
                             : new Structure(0x72, r.Id, r.Type, r.Properties);
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
                return OffsetDateTimeStructure(t.Seconds, t.Nanoseconds,
                                               t.OffsetSeconds, bolt5);
            case ZonedDateTime t:
                return new Structure(t.Basis == DateTimeBasis.Utc ? (byte)0x69
                                                                  : (byte)0x66,
                                     t.Seconds, t.Nanoseconds, t.ZoneId);
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
    internal static object Hydrate(byte tag, object?[] f)
    {
        bool valid = tag switch
        {
            0x4e or 0x72 => f.Length is 3 or 4,
            0x52 => f.Length is 5 or 8,
            0x50 or 0x46 or 0x66 or 0x49 or 0x69 or 0x58 => f.Length == 3,
            0x44 or 0x74 => f.Length == 1,
            0x54 or 0x64 => f.Length == 2,
            0x45 or 0x59 => f.Length == 4,
            _ => true
        };
        if (!valid)
            throw new InvalidDataException("Invalid structure field count");
        long I(int i) => (long)f[i]!;
        string S(int i) => (string)f[i]!;
        // I carries UTC seconds; every offset datetime value keeps local seconds.
        long? local = null;
        if (tag == 0x46)
            local = I(0);
        else if (tag == 0x49)
        {
            try
            {
                local = checked(I(0) + I(2));
            }
            catch (OverflowException)
            {
                return new Structure(tag, f);
            }
        }
        object value = tag switch
        {
            0x4e when f.Length == 4 =>
                new Node(I(0), (List<object?>)f[1]!, (Dictionary<string, object?>)f[2]!)
                {
                    ElementId = S(3)
                },
            0x4e => new Node(I(0), (List<object?>)f[1]!,
                             (Dictionary<string, object?>)f[2]!),
            0x52 when f.Length == 8 =>
                new Relationship(I(0), I(1), I(2), S(3),
                                 (Dictionary<string, object?>)f[4]!)
                {
                    ElementId = S(5),
                    StartElementId = S(6),
                    EndElementId = S(7)
                },
            0x52 => new Relationship(I(0), I(1), I(2), S(3),
                                     (Dictionary<string, object?>)f[4]!),
            0x72 when f.Length == 4 =>
                new UnboundRelationship(I(0), S(1), (Dictionary<string, object?>)f[2]!)
                {
                    ElementId = S(3)
                },
            0x72 => new UnboundRelationship(I(0), S(1),
                                            (Dictionary<string, object?>)f[2]!),
            0x50 => new GraphPath((List<object?>)f[0]!, (List<object?>)f[1]!,
                                  (List<object?>)f[2]!),
            0x44 => new Date(I(0)),
            0x74 => new LocalTime(I(0)),
            0x54 => new OffsetTime(I(0), I(1)),
            0x64 => new LocalDateTime(I(0), I(1)),
            0x46 or 0x49 => new OffsetDateTime(local!.Value, I(1), I(2)),
            0x66 => ZonedDateTime.FromLocal(I(0), I(1), S(2)),
            0x69 => ZonedDateTime.FromUtc(I(0), I(1), S(2)),
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
            if ((tag == 0x64 || local is not null) && I(1) % 100 == 0 && I(1) >= 0 &&
                I(1) < 1_000_000_000)
            {
                var wall = DateTime.SpecifyKind(
                    DateTime.UnixEpoch.AddTicks(
                        checked((local ?? I(0)) * TimeSpan.TicksPerSecond + I(1) / 100)),
                    DateTimeKind.Unspecified);
                if (tag == 0x64)
                    return wall;
                if (I(2) % 60 == 0 && Math.Abs(I(2)) <= 14 * 3600)
                    return new DateTimeOffset(wall, TimeSpan.FromSeconds(I(2)));
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
