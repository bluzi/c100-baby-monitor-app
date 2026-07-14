using System.Text;

namespace BabyMonitor.Core.Xiaomi;

/// <summary>
/// Hex, base64 and the fixed-width integer reads the wire is made of.
///
/// Base64 and hex here are the framework's, not hand-rolled: .NET's Convert.ToBase64String is
/// RFC 4648 with padding, which is exactly what the Mi signing scheme wants, and the interop vectors
/// prove the whole encoded form byte for byte anyway.
/// </summary>
public static class Bytes
{
    private const string Hex = "0123456789abcdef";

    public static string ToHex(this byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (var b in data)
        {
            sb.Append(Hex[(b >> 4) & 0x0f]).Append(Hex[b & 0x0f]);
        }

        return sb.ToString();
    }

    public static byte[] HexToBytes(this string hex)
    {
        if (hex.Length % 2 != 0)
        {
            throw new ArgumentException("hex: odd length", nameof(hex));
        }

        var out_ = new byte[hex.Length / 2];
        for (var i = 0; i < out_.Length; i++)
        {
            out_[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return out_;
    }

    public static string ToBase64(this byte[] data) => Convert.ToBase64String(data);

    public static byte[] Base64ToBytes(this string text)
    {
        var clean = text.Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal);
        // Xiaomi occasionally omits the padding; Convert insists on it.
        var padding = (4 - (clean.Length % 4)) % 4;
        return Convert.FromBase64String(clean + new string('=', padding));
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var out_ = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, out_, offset, part.Length);
            offset += part.Length;
        }

        return out_;
    }

    public static byte[] Slice(this byte[] data, int from, int to)
    {
        var out_ = new byte[to - from];
        Buffer.BlockCopy(data, from, out_, 0, to - from);
        return out_;
    }

    public static int BeU16(this byte[] data, int offset) =>
        (data[offset] << 8) | data[offset + 1];

    public static long BeU32(this byte[] data, int offset) =>
        ((long)data[offset] << 24) |
        ((long)data[offset + 1] << 16) |
        ((long)data[offset + 2] << 8) |
        data[offset + 3];

    public static long LeU32(this byte[] data, int offset) =>
        data[offset] |
        ((long)data[offset + 1] << 8) |
        ((long)data[offset + 2] << 16) |
        ((long)data[offset + 3] << 24);

    public static long LeU64(this byte[] data, int offset) =>
        data.LeU32(offset) | (data.LeU32(offset + 4) << 32);

    public static void PutBeU16(this byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    public static void PutBeU32(this byte[] data, int offset, long value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    public static void PutLeU32(this byte[] data, int offset, long value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }
}
