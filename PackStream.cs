using System.Collections;
using System.Text;

namespace Galactus;
public static class PackStream
{
    public const int MaxMessage = 64 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static byte[] Encode(object? value)
    {
        using var output = new MemoryStream();
        void Number(ulong v, int n)
        {
            for (int i = n - 1; i >= 0; i--)
                output!.WriteByte((byte)(v >> (8 * i)));
        }
        void Header(int n, byte tiny, byte basis)
        {
            if (tiny != 0 && n < 16)
                output.WriteByte((byte)(tiny | n));
            else
            {
                int size = n < 256 ? 1 : n < 65536 ? 2 : 4;
                output.WriteByte((byte)(basis + (size == 4 ? 2 : size - 1)));
                Number((uint)n, size);
            }
        }
        void Write(object? v, int depth)
        {
            if (depth >= 64)
                throw new ArgumentException("Nesting exceeds 64 levels");
            v = Mapping.Dehydrate(v);
            switch (v)
            {
                case null:
                    output.WriteByte(0xc0);
                    break;
                case bool b:
                    output.WriteByte(b ? (byte)0xc3 : (byte)0xc2);
                    break;
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    long i = Convert.ToInt64(v);
                    if (i >= -16 && i <= 127)
                        output.WriteByte(unchecked((byte)i));
                    else
                    {
                        int size = i >= -128 && i <= 127 ? 1
                                   : i >= -32768 && i <= 32767 ? 2
                                   : i >= int.MinValue && i <= int.MaxValue ? 4
                                                                            : 8;
                        output.WriteByte((byte)(size == 1 ? 0xc8
                                                : size == 2 ? 0xc9
                                                : size == 4 ? 0xca
                                                            : 0xcb));
                        Number(unchecked((ulong)i), size);
                    }
                    break;
                case float or double:
                    output.WriteByte(0xc1);
                    Number(
                        unchecked((ulong)BitConverter.DoubleToInt64Bits(Convert.ToDouble(v))),
                        8);
                    break;
                case string s:
                    var bytes = Utf8.GetBytes(s);
                    Header(bytes.Length, 0x80, 0xd0);
                    output.Write(bytes);
                    break;
                case byte[] b:
                    Header(b.Length, 0, 0xcc);
                    output.Write(b);
                    break;
                case Structure s:
                    if (s.Fields.Length > 15)
                        throw new ArgumentException("Invalid structure");
                    output.WriteByte((byte)(0xb0 | s.Fields.Length));
                    output.WriteByte(s.Tag);
                    foreach (var f in s.Fields)
                        Write(f, depth + 1);
                    break;
                case IDictionary m:
                    Header(m.Count, 0xa0, 0xd8);
                    foreach (DictionaryEntry e in m)
                    {
                        if (e.Key is not string)
                            throw new ArgumentException("Map keys must be strings");
                        Write(e.Key, depth + 1);
                        Write(e.Value, depth + 1);
                    }
                    break;
                case IList l:
                    Header(l.Count, 0x90, 0xd4);
                    foreach (var f in l)
                        Write(f, depth + 1);
                    break;
                default:
                    throw new ArgumentException("Unsupported parameter type: " +
                                                v.GetType().Name);
            }
            if (output.Length > MaxMessage)
                throw new ArgumentException("Message exceeds 64 MiB");
        }
        Write(value, 0);
        return output.ToArray();
    }
    public static object? Decode(byte[] data)
    {
        int p = 0;
        byte[] Take(int n)
        {
            if (n < 0 || n > data.Length - p)
                throw new InvalidDataException("Truncated PackStream");
            var b = data.AsSpan(p, n).ToArray();
            p += n;
            return b;
        }
        ulong Number(int n)
        {
            ulong v = 0;
            foreach (var b in Take(n))
                v = v << 8 | b;
            return v;
        }
        object? Read(int depth)
        {
            if (depth >= 64)
                throw new InvalidDataException("Nesting exceeds 64 levels");
            int m = Take(1)[0];
            if (m <= 127)
                return (long)m;
            if (m >= 240)
                return (long)(m - 256);
            switch (m)
            {
                case 0xc0:
                    return null;
                case 0xc2:
                    return false;
                case 0xc3:
                    return true;
                case 0xc1:
                    return BitConverter.Int64BitsToDouble(unchecked((long)Number(8)));
                case 0xc8:
                    return (long)unchecked((sbyte)Number(1));
                case 0xc9:
                    return (long)unchecked((short)Number(2));
                case 0xca:
                    return (long)unchecked((int)Number(4));
                case 0xcb:
                    return unchecked((long)Number(8));
            }
            int kind = m & 0xf0, n = m & 15;
            if (m < 0x80 || m > 0xbf)
            {
                int basis = new[] { 0xcc, 0xd0, 0xd4, 0xd8 }.FirstOrDefault(
                    b => m >= b && m <= b + 2);
                if (basis == 0)
                    throw new InvalidDataException("Unknown PackStream marker");
                kind = basis switch
                {
                    0xcc => 0xcc,
                    0xd0 => 0x80,
                    0xd4 => 0x90,
                    _ => 0xa0
                };
                n = checked((int)Number(1 << (m - basis)));
            }
            if (n > data.Length - p)
                throw new InvalidDataException("Invalid collection size");
            if (kind == 0x80)
                return Utf8.GetString(Take(n));
            if (kind == 0xcc)
                return Take(n);
            if (kind == 0xb0)
            {
                byte tag = Take(1)[0];
                var f = new object?[n];
                for (int i = 0; i < n; i++)
                    f[i] = Read(depth + 1);
                return Mapping.Hydrate(tag, f);
            }
            if (kind == 0x90)
            {
                var list = new List<object?>();
                for (int i = 0; i < n; i++)
                    list.Add(Read(depth + 1));
                return list;
            }
            var map = new Dictionary<string, object?>();
            for (int i = 0; i < n; i++)
            {
                if (Read(depth + 1) is not string k || map.ContainsKey(k))
                    throw new InvalidDataException("Invalid or duplicate map key");
                map.Add(k, Read(depth + 1));
            }
            return Spatial.Hydrate(map);
        }
        var result = Read(0);
        if (p != data.Length)
            throw new InvalidDataException("Trailing PackStream bytes");
        return result;
    }
}
