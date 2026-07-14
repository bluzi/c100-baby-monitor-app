using System.Security.Cryptography;
using System.Text;
using BabyMonitor.Core.Platform;

namespace BabyMonitor.Core.Xiaomi;

/// <summary>
/// The wire's cryptography — port of the Kotlin core's <c>xiaomi/Crypto.kt</c> (itself a port of
/// c100's crypto.ts, itself go2rtc's). Every function here is pure and covered by the interop
/// vectors in protocol-vectors.json.
///
/// MD5/SHA-1/SHA-256 come from the framework rather than being ported by hand. The Kotlin core wrote
/// its own because Kotlin/Native has no MessageDigest; .NET has had these for twenty years, and a
/// hand-rolled hash next to a correct one in the box is a liability, not an asset. The signing scheme
/// is still proven end to end by the vectors — which is what actually guards it.
/// </summary>
public static class Crypto
{
    private const string RandomAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static byte[] Md5(byte[] data) => MD5.HashData(data);

    public static byte[] Sha1(byte[] data) => SHA1.HashData(data);

    public static byte[] Sha256(byte[] data) => SHA256.HashData(data);

    public static byte[] RandomBytes(int n) => Clock.SecureRandomBytes(n);

    public static string RandString(int n)
    {
        var sb = new StringBuilder(n);
        foreach (var b in RandomBytes(n))
        {
            sb.Append(RandomAlphabet[b % RandomAlphabet.Length]);
        }

        return sb.ToString();
    }

    /// <summary>PROTO-1: passwords travel as uppercase-hex MD5.</summary>
    public static string PasswordHash(string password) =>
        Md5(Encoding.UTF8.GetBytes(password)).ToHex().ToUpperInvariant();

    /// <summary>PROTO-8: RC4 with the first 1024 keystream bytes discarded.</summary>
    public static byte[] Rc4(byte[] key, byte[] data, int discard = 1024)
    {
        var s = new int[256];
        for (var i = 0; i < 256; i++)
        {
            s[i] = i;
        }

        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xff;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var a = 0;
        j = 0;
        for (var n = 0; n < discard; n++)
        {
            a = (a + 1) & 0xff;
            j = (j + s[a]) & 0xff;
            (s[a], s[j]) = (s[j], s[a]);
        }

        var out_ = new byte[data.Length];
        for (var n = 0; n < data.Length; n++)
        {
            a = (a + 1) & 0xff;
            j = (j + s[a]) & 0xff;
            (s[a], s[j]) = (s[j], s[a]);
            out_[n] = (byte)(data[n] ^ s[(s[a] + s[j]) & 0xff]);
        }

        return out_;
    }

    // --- ChaCha20 (RFC 8439 core, counter starts at 0) --------------------------

    public static byte[] Chacha20Xor(byte[] key32, byte[] nonce12, byte[] data)
    {
        if (key32.Length != 32)
        {
            throw new ArgumentException("chacha: key must be 32 bytes", nameof(key32));
        }

        if (nonce12.Length != 12)
        {
            throw new ArgumentException("chacha: nonce must be 12 bytes", nameof(nonce12));
        }

        var keyWords = LeWords(key32, 0, 8);
        var nonceWords = LeWords(nonce12, 0, 3);
        var out_ = new byte[data.Length];
        var block = new byte[64];
        var counter = 0;
        var off = 0;
        while (off < data.Length)
        {
            ChachaBlock(keyWords, counter, nonceWords, block);
            var n = Math.Min(64, data.Length - off);
            for (var k = 0; k < n; k++)
            {
                out_[off + k] = (byte)(data[off + k] ^ block[k]);
            }

            counter++;
            off += n;
        }

        return out_;
    }

    /// <summary>PROTO-14: wire format is [8 nonce bytes][chacha20(key, 0x00000000‖nonce8)(plaintext)].</summary>
    public static byte[] ChachaEncode(byte[] plaintext, byte[] key32)
    {
        var nonce8 = RandomBytes(8);
        return Bytes.Concat(nonce8, ChachaDecodeNonce(plaintext, nonce8, key32));
    }

    public static byte[] ChachaDecode(byte[] src, byte[] key32)
    {
        if (src.Length < 8)
        {
            throw new ArgumentException("miss: payload too short", nameof(src));
        }

        return ChachaDecodeNonce(src.Slice(8, src.Length), src.Slice(0, 8), key32);
    }

    public static byte[] ChachaDecodeNonce(byte[] src, byte[] nonce8, byte[] key32)
    {
        var nonce12 = new byte[12];
        Buffer.BlockCopy(nonce8, 0, nonce12, 4, 8);
        return Chacha20Xor(key32, nonce12, src);
    }

    // --- NaCl box precomputation (PROTO-13) -------------------------------------
    // box.before(pub, priv) = HSalsa20(key = X25519(priv, pub), input = 0^16)

    public static (byte[] Public, byte[] Private) GenerateBoxKeyPair()
    {
        var priv = RandomBytes(32);
        return (Curve25519.ScalarMultBase(priv), priv);
    }

    public static byte[] CalcSharedKey(string devicePublicHex, string clientPrivateHex)
    {
        var pub = devicePublicHex.HexToBytes();
        var priv = clientPrivateHex.HexToBytes();
        if (pub.Length != 32 || priv.Length != 32)
        {
            throw new ArgumentException("box key must be 32 bytes");
        }

        return Hsalsa20(Curve25519.ScalarMult(priv, pub));
    }

    /// <summary>HSalsa20 with the "expand 32-byte k" constants and a zero input block.</summary>
    public static byte[] Hsalsa20(byte[] key32)
    {
        var k = LeWords(key32, 0, 8);
        var x = new uint[16];
        x[0] = 0x61707865;
        x[5] = 0x3320646e;
        x[10] = 0x79622d32;
        x[15] = 0x6b206574;
        x[1] = k[0];
        x[2] = k[1];
        x[3] = k[2];
        x[4] = k[3];
        x[11] = k[4];
        x[12] = k[5];
        x[13] = k[6];
        x[14] = k[7];
        // x[6..9] = input = zeros

        for (var round = 0; round < 10; round++)
        {
            // column round
            x[4] ^= Rotl(x[0] + x[12], 7);
            x[8] ^= Rotl(x[4] + x[0], 9);
            x[12] ^= Rotl(x[8] + x[4], 13);
            x[0] ^= Rotl(x[12] + x[8], 18);
            x[9] ^= Rotl(x[5] + x[1], 7);
            x[13] ^= Rotl(x[9] + x[5], 9);
            x[1] ^= Rotl(x[13] + x[9], 13);
            x[5] ^= Rotl(x[1] + x[13], 18);
            x[14] ^= Rotl(x[10] + x[6], 7);
            x[2] ^= Rotl(x[14] + x[10], 9);
            x[6] ^= Rotl(x[2] + x[14], 13);
            x[10] ^= Rotl(x[6] + x[2], 18);
            x[3] ^= Rotl(x[15] + x[11], 7);
            x[7] ^= Rotl(x[3] + x[15], 9);
            x[11] ^= Rotl(x[7] + x[3], 13);
            x[15] ^= Rotl(x[11] + x[7], 18);

            // row round
            x[1] ^= Rotl(x[0] + x[3], 7);
            x[2] ^= Rotl(x[1] + x[0], 9);
            x[3] ^= Rotl(x[2] + x[1], 13);
            x[0] ^= Rotl(x[3] + x[2], 18);
            x[6] ^= Rotl(x[5] + x[4], 7);
            x[7] ^= Rotl(x[6] + x[5], 9);
            x[4] ^= Rotl(x[7] + x[6], 13);
            x[5] ^= Rotl(x[4] + x[7], 18);
            x[11] ^= Rotl(x[10] + x[9], 7);
            x[8] ^= Rotl(x[11] + x[10], 9);
            x[9] ^= Rotl(x[8] + x[11], 13);
            x[10] ^= Rotl(x[9] + x[8], 18);
            x[12] ^= Rotl(x[15] + x[14], 7);
            x[13] ^= Rotl(x[12] + x[15], 9);
            x[14] ^= Rotl(x[13] + x[12], 13);
            x[15] ^= Rotl(x[14] + x[13], 18);
        }

        var outWords = new[] { x[0], x[5], x[10], x[15], x[6], x[7], x[8], x[9] };
        var out_ = new byte[32];
        for (var i = 0; i < 8; i++)
        {
            out_[i * 4] = (byte)outWords[i];
            out_[(i * 4) + 1] = (byte)(outWords[i] >> 8);
            out_[(i * 4) + 2] = (byte)(outWords[i] >> 16);
            out_[(i * 4) + 3] = (byte)(outWords[i] >> 24);
        }

        return out_;
    }

    // --- Mi cloud signed-request bits (PROTO-7) ---------------------------------

    /// <summary>8 random bytes + big-endian u32 minutes-since-epoch.</summary>
    public static byte[] GenNonce(long? nowMs = null, byte[]? random8 = null)
    {
        var stamp = nowMs ?? Clock.WallClockMs();
        var random = random8 ?? RandomBytes(8);
        if (random.Length != 8)
        {
            throw new ArgumentException("nonce: random part must be 8 bytes", nameof(random8));
        }

        var nonce = new byte[12];
        Buffer.BlockCopy(random, 0, nonce, 0, 8);
        nonce.PutBeU32(8, stamp / 1000 / 60);
        return nonce;
    }

    public static byte[] GenSignedNonce(byte[] ssecurity, byte[] nonce) =>
        Sha256(Bytes.Concat(ssecurity, nonce));

    public static string GenSignatureB64(string method, string path, string data, string? rc4Hash, byte[] signedNonce)
    {
        var s = $"{method}&{path}&data={data}";
        if (rc4Hash != null)
        {
            s += $"&rc4_hash__={rc4Hash}";
        }

        s += "&" + signedNonce.ToBase64();
        return Sha1(Encoding.UTF8.GetBytes(s)).ToBase64();
    }

    private static void ChachaBlock(uint[] key, int counter, uint[] nonce, byte[] out_)
    {
        var st = new uint[16];
        st[0] = 0x61707865;
        st[1] = 0x3320646e;
        st[2] = 0x79622d32;
        st[3] = 0x6b206574;
        Array.Copy(key, 0, st, 4, 8);
        st[12] = (uint)counter;
        Array.Copy(nonce, 0, st, 13, 3);

        var x = (uint[])st.Clone();
        for (var round = 0; round < 10; round++)
        {
            Qr(x, 0, 4, 8, 12);
            Qr(x, 1, 5, 9, 13);
            Qr(x, 2, 6, 10, 14);
            Qr(x, 3, 7, 11, 15);
            Qr(x, 0, 5, 10, 15);
            Qr(x, 1, 6, 11, 12);
            Qr(x, 2, 7, 8, 13);
            Qr(x, 3, 4, 9, 14);
        }

        for (var k = 0; k < 16; k++)
        {
            var w = x[k] + st[k];
            var b = k * 4;
            out_[b] = (byte)w;
            out_[b + 1] = (byte)(w >> 8);
            out_[b + 2] = (byte)(w >> 16);
            out_[b + 3] = (byte)(w >> 24);
        }
    }

    private static void Qr(uint[] x, int a, int b, int c, int d)
    {
        x[a] += x[b];
        x[d] = Rotl(x[d] ^ x[a], 16);
        x[c] += x[d];
        x[b] = Rotl(x[b] ^ x[c], 12);
        x[a] += x[b];
        x[d] = Rotl(x[d] ^ x[a], 8);
        x[c] += x[d];
        x[b] = Rotl(x[b] ^ x[c], 7);
    }

    private static uint Rotl(uint value, int bits) => (value << bits) | (value >> (32 - bits));

    private static uint[] LeWords(byte[] bytes, int off, int count)
    {
        var words = new uint[count];
        for (var i = 0; i < count; i++)
        {
            var b = off + (i * 4);
            words[i] = bytes[b] |
                ((uint)bytes[b + 1] << 8) |
                ((uint)bytes[b + 2] << 16) |
                ((uint)bytes[b + 3] << 24);
        }

        return words;
    }
}
