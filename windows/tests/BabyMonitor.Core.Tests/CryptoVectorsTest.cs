using System.Text;
using BabyMonitor.Core.Json;
using BabyMonitor.Core.Xiaomi;
using Xunit;

namespace BabyMonitor.Core.Tests;

/// <summary>
/// Interop vectors generated from the proven c100 TypeScript implementation
/// (core/protocol-vectors.json — the same file the Kotlin core's tests read). If these pass, our
/// bytes match the implementation that actually talks to the camera. This is the single most
/// important test class in the Windows port: it is what makes a second implementation of the
/// protocol safe rather than reckless.
/// </summary>
public class CryptoVectorsTest
{
    private static readonly JsonObj V = new(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "protocol-vectors.json")));

    [Fact(DisplayName = "PROTO-1 password hash is uppercase-hex MD5")]
    public void PasswordHash()
    {
        Assert.Equal(V.GetString("passwordHashUpperHex"), Crypto.PasswordHash(V.GetString("password")));
    }

    [Fact(DisplayName = "PROTO-8 rc4 discards 1024 keystream bytes and matches the reference")]
    public void Rc4MatchesReference()
    {
        var rc4 = V.GetJsonObject("rc4");
        var out_ = Crypto.Rc4(
            rc4.GetString("keyHex").HexToBytes(),
            Encoding.UTF8.GetBytes(rc4.GetString("plaintextUtf8")));
        Assert.Equal(rc4.GetString("ciphertextHex"), out_.ToHex());
    }

    [Fact(DisplayName = "PROTO-8 rc4 is symmetric")]
    public void Rc4IsSymmetric()
    {
        var key = new byte[32];
        for (var i = 0; i < 32; i++)
        {
            key[i] = (byte)i;
        }

        var plain = Encoding.UTF8.GetBytes("round trip");
        Assert.Equal(plain, Crypto.Rc4(key, Crypto.Rc4(key, plain)));
    }

    [Fact(DisplayName = "PROTO-14 chacha20 with 8-byte nonce padded to 12 matches the reference")]
    public void Chacha20MatchesReference()
    {
        var c = V.GetJsonObject("chacha");
        var out_ = Crypto.ChachaDecodeNonce(
            Encoding.UTF8.GetBytes(c.GetString("plaintextUtf8")),
            c.GetString("nonce8Hex").HexToBytes(),
            c.GetString("keyHex").HexToBytes());
        Assert.Equal(c.GetString("ciphertextHex"), out_.ToHex());
    }

    [Fact(DisplayName = "PROTO-14 encode prepends a fresh nonce and decode round-trips")]
    public void ChachaEncodeRoundTrips()
    {
        var key = new byte[32];
        for (var i = 0; i < 32; i++)
        {
            key[i] = (byte)(i * 7);
        }

        var plain = Encoding.UTF8.GetBytes("wire round trip");
        var enc1 = Crypto.ChachaEncode(plain, key);
        var enc2 = Crypto.ChachaEncode(plain, key);
        Assert.Equal(plain.Length + 8, enc1.Length);
        Assert.NotEqual(enc1.ToHex(), enc2.ToHex()); // fresh random nonce each message
        Assert.Equal(plain, Crypto.ChachaDecode(enc1, key));
        Assert.Equal(plain, Crypto.ChachaDecode(enc2, key));
    }

    [Fact(DisplayName = "PROTO-14 decode rejects payloads shorter than the nonce")]
    public void ChachaDecodeRejectsShort()
    {
        Assert.Throws<ArgumentException>(() => Crypto.ChachaDecode(new byte[7], new byte[32]));
    }

    [Fact(DisplayName = "PROTO-13 NaCl box shared key matches the reference")]
    public void SharedKeyMatchesReference()
    {
        var s = V.GetJsonObject("sharedKey");
        var shared = Crypto.CalcSharedKey(s.GetString("devicePublicHex"), s.GetString("clientPrivateHex"));
        Assert.Equal(s.GetString("sharedHex"), shared.ToHex());
        // The reference computed the reverse direction too — both sides must agree.
        Assert.Equal(s.GetString("sharedHex"), s.GetString("sharedReverseHex"));
    }

    [Fact(DisplayName = "PROTO-13 generated key pairs agree on the shared secret")]
    public void KeyPairsAgree()
    {
        var (pubA, privA) = Crypto.GenerateBoxKeyPair();
        var (pubB, privB) = Crypto.GenerateBoxKeyPair();
        var ab = Crypto.CalcSharedKey(pubB.ToHex(), privA.ToHex());
        var ba = Crypto.CalcSharedKey(pubA.ToHex(), privB.ToHex());
        Assert.Equal(ab, ba);
        Assert.Equal(32, ab.Length);
    }

    [Fact(DisplayName = "PROTO-7 signed nonce is sha256 of ssecurity plus nonce")]
    public void SignedNonce()
    {
        var s = V.GetJsonObject("signing");
        var ssecurity = s.GetString("ssecurityB64").Base64ToBytes();
        var nonce = s.GetString("nonceHex").HexToBytes();
        Assert.Equal(s.GetString("signedNonceHex"), Crypto.GenSignedNonce(ssecurity, nonce).ToHex());
    }

    [Fact(DisplayName = "PROTO-7 nonce is 8 random bytes plus big-endian minutes")]
    public void NonceLayout()
    {
        var random8 = "a1a2a3a4a5a6a7a8".HexToBytes();
        var nonce = Crypto.GenNonce(nowMs: 29_531_092L * 60_000L, random8: random8);
        Assert.Equal(12, nonce.Length);
        Assert.Equal("a1a2a3a4a5a6a7a8", nonce.Slice(0, 8).ToHex());
        Assert.Equal(29_531_092L, nonce.BeU32(8));
    }

    [Fact(DisplayName = "PROTO-7 the full signed request form matches the reference byte for byte")]
    public void SignedFormMatchesReference()
    {
        var s = V.GetJsonObject("signing");
        var r = V.GetJsonObject("signedRequest");
        var ssecurity = s.GetString("ssecurityB64").Base64ToBytes();
        var nonce = s.GetString("nonceHex").HexToBytes();
        var (form, signedNonce) = MiCloud.BuildSignedForm(
            r.GetString("path"),
            r.GetString("data"),
            ssecurity,
            nonce);
        Assert.Equal(r.GetString("formEncoded"), form);
        Assert.Equal(s.GetString("signedNonceHex"), signedNonce.ToHex());
    }

    [Fact(DisplayName = "PROTO-8 response bodies decrypt with the signed nonce")]
    public void ResponsesDecrypt()
    {
        var s = V.GetJsonObject("signing");
        var r = V.GetJsonObject("response");
        var signedNonce = s.GetString("signedNonceHex").HexToBytes();
        var plain = Crypto.Rc4(signedNonce, r.GetString("bodyB64").Base64ToBytes());
        Assert.Equal(r.GetString("plaintext"), Encoding.UTF8.GetString(plain));
    }
}
