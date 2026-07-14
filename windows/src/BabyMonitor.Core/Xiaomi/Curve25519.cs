namespace BabyMonitor.Core.Xiaomi;

/// <summary>
/// X25519 scalar multiplication — a port of TweetNaCl's crypto_scalarmult, by way of the Kotlin core.
///
/// .NET has no Curve25519 in the box (ECDiffieHellman is NIST curves only), and this computes the
/// NaCl box shared key (PROTO-13) that every media packet is encrypted with. The interop vectors pin
/// it to the implementation that talks to the real camera, so a drift here fails the build rather
/// than the monitor.
///
/// Field elements are 16 limbs of 16 bits held in longs — TweetNaCl's representation. It is
/// constant-time by construction (no data-dependent branches; the conditional swap is arithmetic),
/// which is why the port stays faithful rather than "tidied".
/// </summary>
public static class Curve25519
{
    private const int Limbs = 16;

    private static readonly long[] Gf121665 = Gf(0xdb41L, 1L);

    private static readonly byte[] BasePoint = BuildBasePoint();

    /// <summary>Montgomery ladder: q = scalar · point, both 32 bytes. The scalar is clamped per RFC 7748.</summary>
    public static byte[] ScalarMult(byte[] scalar, byte[] point)
    {
        if (scalar.Length != 32)
        {
            throw new ArgumentException("x25519: scalar must be 32 bytes", nameof(scalar));
        }

        if (point.Length != 32)
        {
            throw new ArgumentException("x25519: point must be 32 bytes", nameof(point));
        }

        var z = (byte[])scalar.Clone();
        z[31] = (byte)((z[31] & 127) | 64);
        z[0] = (byte)(z[0] & 248);

        var x = new long[Limbs];
        Unpack25519(x, point);

        var a = new long[Limbs];
        var b = (long[])x.Clone();
        var c = new long[Limbs];
        var d = new long[Limbs];
        var e = new long[Limbs];
        var f = new long[Limbs];
        a[0] = 1;
        d[0] = 1;

        for (var i = 254; i >= 0; i--)
        {
            var r = (z[i >> 3] >> (i & 7)) & 1;
            Sel25519(a, b, r);
            Sel25519(c, d, r);
            Add(e, a, c);
            Sub(a, a, c);
            Add(c, b, d);
            Sub(b, b, d);
            Sq(d, e);
            Sq(f, a);
            Mul(a, c, a);
            Mul(c, b, e);
            Add(e, a, c);
            Sub(a, a, c);
            Sq(b, a);
            Sub(c, d, f);
            Mul(a, c, Gf121665);
            Add(a, a, d);
            Mul(c, c, a);
            Mul(a, d, f);
            Mul(d, b, x);
            Sq(b, e);
            Sel25519(a, b, r);
            Sel25519(c, d, r);
        }

        Inv25519(c, c);
        Mul(a, a, c);
        var out_ = new byte[32];
        Pack25519(out_, a);
        return out_;
    }

    public static byte[] ScalarMultBase(byte[] scalar) => ScalarMult(scalar, BasePoint);

    private static byte[] BuildBasePoint()
    {
        var point = new byte[32];
        point[0] = 9;
        return point;
    }

    private static long[] Gf(params long[] init)
    {
        var r = new long[Limbs];
        Array.Copy(init, r, init.Length);
        return r;
    }

    private static void Car25519(long[] o)
    {
        for (var i = 0; i < Limbs; i++)
        {
            o[i] += 1L << 16;
            var c = o[i] >> 16;
            if (i < 15)
            {
                o[i + 1] += c - 1;
            }
            else
            {
                o[0] += 38 * (c - 1);
            }

            o[i] -= c << 16;
        }
    }

    /// <summary>Conditional swap of p and q, without branching on the secret bit.</summary>
    private static void Sel25519(long[] p, long[] q, int b)
    {
        var c = ~((long)b - 1);
        for (var i = 0; i < Limbs; i++)
        {
            var t = c & (p[i] ^ q[i]);
            p[i] ^= t;
            q[i] ^= t;
        }
    }

    private static void Pack25519(byte[] o, long[] n)
    {
        var t = (long[])n.Clone();
        Car25519(t);
        Car25519(t);
        Car25519(t);
        var m = new long[Limbs];
        for (var round = 0; round < 2; round++)
        {
            m[0] = t[0] - 0xffed;
            for (var i = 1; i < 15; i++)
            {
                m[i] = t[i] - 0xffff - ((m[i - 1] >> 16) & 1);
                m[i - 1] &= 0xffff;
            }

            m[15] = t[15] - 0x7fff - ((m[14] >> 16) & 1);
            var b = (int)((m[15] >> 16) & 1);
            m[14] &= 0xffff;
            Sel25519(t, m, 1 - b);
        }

        for (var i = 0; i < Limbs; i++)
        {
            o[2 * i] = (byte)(t[i] & 0xff);
            o[(2 * i) + 1] = (byte)((t[i] >> 8) & 0xff);
        }
    }

    private static void Unpack25519(long[] o, byte[] n)
    {
        for (var i = 0; i < Limbs; i++)
        {
            o[i] = n[2 * i] + ((long)n[(2 * i) + 1] << 8);
        }

        o[15] &= 0x7fff;
    }

    private static void Add(long[] o, long[] a, long[] b)
    {
        for (var i = 0; i < Limbs; i++)
        {
            o[i] = a[i] + b[i];
        }
    }

    private static void Sub(long[] o, long[] a, long[] b)
    {
        for (var i = 0; i < Limbs; i++)
        {
            o[i] = a[i] - b[i];
        }
    }

    private static void Mul(long[] o, long[] a, long[] b)
    {
        var t = new long[31];
        for (var i = 0; i < Limbs; i++)
        {
            var v = a[i];
            for (var j = 0; j < Limbs; j++)
            {
                t[i + j] += v * b[j];
            }
        }

        // Fold the top half back in: 2^256 ≡ 38 (mod 2^255 - 19).
        for (var i = 0; i < 15; i++)
        {
            t[i] += 38 * t[i + 16];
        }

        Array.Copy(t, o, Limbs);
        Car25519(o);
        Car25519(o);
    }

    private static void Sq(long[] o, long[] a) => Mul(o, a, a);

    /// <summary>o = 1/i in the field, by Fermat: i^(p-2).</summary>
    private static void Inv25519(long[] o, long[] i)
    {
        var c = (long[])i.Clone();
        for (var a = 253; a >= 0; a--)
        {
            Sq(c, c);
            if (a != 2 && a != 4)
            {
                Mul(c, c, i);
            }
        }

        Array.Copy(c, o, Limbs);
    }
}
