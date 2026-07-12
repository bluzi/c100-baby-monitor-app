package com.bluzi.babymonitor.xiaomi

import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertThrows
import org.junit.Test

/**
 * Interop vectors generated from the proven c100 TypeScript implementation
 * (app/src/test/resources/protocol-vectors.json). If these pass, our bytes match the
 * implementation that actually talks to the camera.
 */
class CryptoVectorsTest {
    private val v: JSONObject by lazy {
        val stream = javaClass.classLoader!!.getResourceAsStream("protocol-vectors.json")
            ?: error("protocol-vectors.json missing from test resources")
        JSONObject(String(stream.readBytes()))
    }

    @Test
    fun `PROTO-1 password hash is uppercase-hex MD5`() {
        assertEquals(v.getString("passwordHashUpperHex"), Crypto.passwordHash(v.getString("password")))
    }

    @Test
    fun `PROTO-8 rc4 discards 1024 keystream bytes and matches the reference`() {
        val rc4 = v.getJSONObject("rc4")
        val out = Crypto.rc4(rc4.getString("keyHex").hexToBytes(), rc4.getString("plaintextUtf8").toByteArray())
        assertEquals(rc4.getString("ciphertextHex"), out.toHex())
    }

    @Test
    fun `PROTO-8 rc4 is symmetric`() {
        val key = ByteArray(32) { it.toByte() }
        val plain = "round trip".toByteArray()
        assertArrayEquals(plain, Crypto.rc4(key, Crypto.rc4(key, plain)))
    }

    @Test
    fun `PROTO-14 chacha20 with 8-byte nonce padded to 12 matches the reference`() {
        val c = v.getJSONObject("chacha")
        val out = Crypto.chachaDecodeNonce(
            c.getString("plaintextUtf8").toByteArray(),
            c.getString("nonce8Hex").hexToBytes(),
            c.getString("keyHex").hexToBytes(),
        )
        assertEquals(c.getString("ciphertextHex"), out.toHex())
    }

    @Test
    fun `PROTO-14 encode prepends fresh nonce and decode round-trips`() {
        val key = ByteArray(32) { (it * 7).toByte() }
        val plain = "wire round trip".toByteArray()
        val enc1 = Crypto.chachaEncode(plain, key)
        val enc2 = Crypto.chachaEncode(plain, key)
        assertEquals(plain.size + 8, enc1.size)
        assertNotEquals(enc1.toHex(), enc2.toHex()) // fresh random nonce each message
        assertArrayEquals(plain, Crypto.chachaDecode(enc1, key))
        assertArrayEquals(plain, Crypto.chachaDecode(enc2, key))
    }

    @Test
    fun `PROTO-14 decode rejects payloads shorter than the nonce`() {
        assertThrows(IllegalArgumentException::class.java) {
            Crypto.chachaDecode(ByteArray(7), ByteArray(32))
        }
    }

    @Test
    fun `PROTO-13 NaCl box shared key matches the reference`() {
        val s = v.getJSONObject("sharedKey")
        val shared = Crypto.calcSharedKey(s.getString("devicePublicHex"), s.getString("clientPrivateHex"))
        assertEquals(s.getString("sharedHex"), shared.toHex())
        // The reference computed the reverse direction too — both sides must agree.
        assertEquals(s.getString("sharedHex"), s.getString("sharedReverseHex"))
    }

    @Test
    fun `PROTO-13 generated key pairs agree on the shared secret`() {
        val (pubA, privA) = Crypto.generateBoxKeyPair()
        val (pubB, privB) = Crypto.generateBoxKeyPair()
        val ab = Crypto.calcSharedKey(pubB.toHex(), privA.toHex())
        val ba = Crypto.calcSharedKey(pubA.toHex(), privB.toHex())
        assertArrayEquals(ab, ba)
        assertEquals(32, ab.size)
    }

    @Test
    fun `PROTO-7 signed nonce is sha256 of ssecurity plus nonce`() {
        val s = v.getJSONObject("signing")
        val ssecurity = s.getString("ssecurityB64").base64ToBytes()
        val nonce = s.getString("nonceHex").hexToBytes()
        assertEquals(s.getString("signedNonceHex"), Crypto.genSignedNonce(ssecurity, nonce).toHex())
    }

    @Test
    fun `PROTO-7 nonce is 8 random bytes plus big-endian minutes`() {
        val random8 = "a1a2a3a4a5a6a7a8".hexToBytes()
        val nonce = Crypto.genNonce(nowMs = 29_531_092L * 60_000L, random8 = random8)
        assertEquals(12, nonce.size)
        assertEquals("a1a2a3a4a5a6a7a8", nonce.copyOfRange(0, 8).toHex())
        assertEquals(29_531_092L, nonce.beU32(8))
    }

    @Test
    fun `PROTO-7 full signed request form matches the reference byte for byte`() {
        val s = v.getJSONObject("signing")
        val r = v.getJSONObject("signedRequest")
        val ssecurity = s.getString("ssecurityB64").base64ToBytes()
        val nonce = s.getString("nonceHex").hexToBytes()
        val (form, signedNonce) = buildSignedForm(r.getString("path"), r.getString("data"), ssecurity, nonce)
        assertEquals(r.getString("formEncoded"), form)
        assertEquals(s.getString("signedNonceHex"), signedNonce.toHex())
    }

    @Test
    fun `PROTO-8 response bodies decrypt with the signed nonce`() {
        val s = v.getJSONObject("signing")
        val r = v.getJSONObject("response")
        val signedNonce = s.getString("signedNonceHex").hexToBytes()
        val plain = Crypto.rc4(signedNonce, r.getString("bodyB64").base64ToBytes())
        assertEquals(r.getString("plaintext"), String(plain))
    }
}
