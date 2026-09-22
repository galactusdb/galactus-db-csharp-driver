namespace Galactus;

/// <summary>Lossless geometry/geography: WKB plus domain, SRID, layout and
/// model.</summary>
public sealed class Spatial
{
    public string Domain { get; }
    public long Srid { get; }
    public string Layout { get; }
    public string Model { get; }
    private readonly byte[] wkb;
    public byte[] Wkb => wkb.ToArray();
    public Spatial(string domain, long srid, string layout, string model,
                   byte[] wkb)
    {
        if ((domain != "geometry" && domain != "geography") ||
            (layout != "XY" && layout != "XYZ") ||
            model != (domain == "geometry" ? "planar" : "greatCircle"))
            throw new ArgumentException("Unsupported spatial metadata");
        Domain = domain;
        Srid = srid;
        Layout = layout;
        Model = model;
        this.wkb = wkb.ToArray();
    }
    public Dictionary<string, object?> ToMap() => new() { { "$gdbType",
                                                          "spatial" },
                                                        { "version", 1L },
                                                        { "domain", Domain },
                                                        { "srid", Srid },
                                                        { "layout", Layout },
                                                        { "model", Model },
                                                        { "wkb", Wkb } };
    public static Spatial FromMap(Dictionary<string, object?> m)
    {
        string payload = m.ContainsKey("wkbHex") ? "wkbHex" : "wkb";
        if (!Equals(m.GetValueOrDefault("$gdbType"), "spatial") ||
            !Equals(m.GetValueOrDefault("version"), 1L) ||
            !m.Keys.ToHashSet().SetEquals(new[] { "$gdbType", "version", "domain",
                                              "srid", "layout", "model",
                                              payload }))
            throw new ArgumentException("Invalid spatial envelope");
        return new Spatial((string)m["domain"]!, (long)m["srid"]!,
                           (string)m["layout"]!, (string)m["model"]!,
                           payload == "wkb"
                               ? (byte[])m[payload]!
                               : Convert.FromHexString((string)m[payload]!));
    }
    internal static object Hydrate(Dictionary<string, object?> m) =>
        Equals(m.GetValueOrDefault("$gdbType"), "spatial") &&
                Equals(m.GetValueOrDefault("version"), 1L)
            ? FromMap(m)
            : m;
}
