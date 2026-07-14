package com.bluzi.babymonitor.xiaomi

import com.bluzi.babymonitor.platform.secureRandomBytes
import com.bluzi.babymonitor.platform.wallClockMs

// Kotlin port of c100/src/xiaomi/crypto.ts (itself a port of go2rtc's cloud.go / miss crypto).
// Every function here is pure and covered by interop vectors in protocol-vectors.json.

object Crypto {
    fun md5(data: ByteArray): ByteArray = md5Digest(data)
    fun sha1(data: ByteArray): ByteArray = sha1Digest(data)
    fun sha256(data: ByteArray): ByteArray = sha256Digest(data)

    fun randomBytes(n: Int): ByteArray = secureRandomBytes(n)

    fun randString(n: Int): String {
        val alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
        return buildString(n) {
            for (b in randomBytes(n)) append(alphabet[(b.toInt() and 0xff) % alphabet.length])
        }
    }

    /** PROTO-1: passwords travel as uppercase-hex MD5. */
    fun passwordHash(password: String): String =
        md5(password.encodeToByteArray()).toHex().uppercase()

    // PROTO-8: RC4 with the first 1024 keystream bytes discarded.
    fun rc4(key: ByteArray, data: ByteArray, discard: Int = 1024): ByteArray {
        val s = IntArray(256) { it }
        var j = 0
        for (i in 0 until 256) {
            j = (j + s[i] + (key[i % key.size].toInt() and 0xff)) and 0xff
            val t = s[i]; s[i] = s[j]; s[j] = t
        }
        var i = 0
        j = 0
        repeat(discard) {
            i = (i + 1) and 0xff
            j = (j + s[i]) and 0xff
            val t = s[i]; s[i] = s[j]; s[j] = t
        }
        val out = ByteArray(data.size)
        for (n in data.indices) {
            i = (i + 1) and 0xff
            j = (j + s[i]) and 0xff
            val t = s[i]; s[i] = s[j]; s[j] = t
            out[n] = (data[n].toInt() xor s[(s[i] + s[j]) and 0xff]).toByte()
        }
        return out
    }

    // --- ChaCha20 (RFC 8439 core, counter starts at 0) -------------------------

    private fun chachaBlock(key: IntArray, counter: Int, nonce: IntArray, out: ByteArray, outOff: Int) {
        val st = IntArray(16)
        st[0] = 0x61707865; st[1] = 0x3320646e; st[2] = 0x79622d32; st[3] = 0x6b206574
        key.copyInto(st, 4)
        st[12] = counter
        nonce.copyInto(st, 13)
        val x = st.copyOf()
        repeat(10) {
            qr(x, 0, 4, 8, 12); qr(x, 1, 5, 9, 13); qr(x, 2, 6, 10, 14); qr(x, 3, 7, 11, 15)
            qr(x, 0, 5, 10, 15); qr(x, 1, 6, 11, 12); qr(x, 2, 7, 8, 13); qr(x, 3, 4, 9, 14)
        }
        for (k in 0 until 16) {
            val w = x[k] + st[k]
            val base = outOff + k * 4
            out[base] = w.toByte()
            out[base + 1] = (w ushr 8).toByte()
            out[base + 2] = (w ushr 16).toByte()
            out[base + 3] = (w ushr 24).toByte()
        }
    }

    private fun qr(x: IntArray, a: Int, b: Int, c: Int, d: Int) {
        x[a] += x[b]; x[d] = (x[d] xor x[a]).rotateLeft(16)
        x[c] += x[d]; x[b] = (x[b] xor x[c]).rotateLeft(12)
        x[a] += x[b]; x[d] = (x[d] xor x[a]).rotateLeft(8)
        x[c] += x[d]; x[b] = (x[b] xor x[c]).rotateLeft(7)
    }

    private fun leWords(bytes: ByteArray, off: Int, count: Int): IntArray =
        IntArray(count) { i ->
            val b = off + i * 4
            (bytes[b].toInt() and 0xff) or
                ((bytes[b + 1].toInt() and 0xff) shl 8) or
                ((bytes[b + 2].toInt() and 0xff) shl 16) or
                ((bytes[b + 3].toInt() and 0xff) shl 24)
        }

    fun chacha20Xor(key32: ByteArray, nonce12: ByteArray, data: ByteArray): ByteArray {
        require(key32.size == 32) { "chacha: key must be 32 bytes" }
        require(nonce12.size == 12) { "chacha: nonce must be 12 bytes" }
        val keyWords = leWords(key32, 0, 8)
        val nonceWords = leWords(nonce12, 0, 3)
        val out = ByteArray(data.size)
        val block = ByteArray(64)
        var counter = 0
        var off = 0
        while (off < data.size) {
            chachaBlock(keyWords, counter, nonceWords, block, 0)
            val n = minOf(64, data.size - off)
            for (k in 0 until n) out[off + k] = (data[off + k].toInt() xor block[k].toInt()).toByte()
            counter++
            off += n
        }
        return out
    }

    /** PROTO-14: wire format is [8 nonce bytes][chacha20(key, 0x00000000‖nonce8)(plaintext)]. */
    fun chachaEncode(plaintext: ByteArray, key32: ByteArray): ByteArray {
        val nonce8 = randomBytes(8)
        return concatBytes(nonce8, chachaDecodeNonce(plaintext, nonce8, key32))
    }

    fun chachaDecode(src: ByteArray, key32: ByteArray): ByteArray {
        require(src.size >= 8) { "miss: payload too short" }
        return chachaDecodeNonce(src.copyOfRange(8, src.size), src.copyOfRange(0, 8), key32)
    }

    fun chachaDecodeNonce(src: ByteArray, nonce8: ByteArray, key32: ByteArray): ByteArray {
        val nonce12 = ByteArray(12)
        nonce8.copyInto(nonce12, 4)
        return chacha20Xor(key32, nonce12, src)
    }

    // --- NaCl box precomputation (PROTO-13) -------------------------------------
    // box.before(pub, priv) = HSalsa20(key = X25519(priv, pub), input = 0^16)

    fun generateBoxKeyPair(): Pair<ByteArray, ByteArray> {
        val priv = randomBytes(32)
        return x25519ScalarMultBase(priv) to priv
    }

    fun calcSharedKey(devicePublicHex: String, clientPrivateHex: String): ByteArray {
        val pub = devicePublicHex.hexToBytes()
        val priv = clientPrivateHex.hexToBytes()
        require(pub.size == 32 && priv.size == 32) { "box key must be 32 bytes" }
        return hsalsa20(x25519ScalarMult(priv, pub))
    }

    /** HSalsa20 with the "expand 32-byte k" constants and a zero input block. */
    internal fun hsalsa20(key32: ByteArray): ByteArray {
        val k = leWords(key32, 0, 8)
        val x = IntArray(16)
        x[0] = 0x61707865; x[5] = 0x3320646e; x[10] = 0x79622d32; x[15] = 0x6b206574
        x[1] = k[0]; x[2] = k[1]; x[3] = k[2]; x[4] = k[3]
        x[11] = k[4]; x[12] = k[5]; x[13] = k[6]; x[14] = k[7]
        // x[6..9] = input = zeros
        repeat(10) {
            // column round
            x[4] = x[4] xor (x[0] + x[12]).rotateLeft(7)
            x[8] = x[8] xor (x[4] + x[0]).rotateLeft(9)
            x[12] = x[12] xor (x[8] + x[4]).rotateLeft(13)
            x[0] = x[0] xor (x[12] + x[8]).rotateLeft(18)
            x[9] = x[9] xor (x[5] + x[1]).rotateLeft(7)
            x[13] = x[13] xor (x[9] + x[5]).rotateLeft(9)
            x[1] = x[1] xor (x[13] + x[9]).rotateLeft(13)
            x[5] = x[5] xor (x[1] + x[13]).rotateLeft(18)
            x[14] = x[14] xor (x[10] + x[6]).rotateLeft(7)
            x[2] = x[2] xor (x[14] + x[10]).rotateLeft(9)
            x[6] = x[6] xor (x[2] + x[14]).rotateLeft(13)
            x[10] = x[10] xor (x[6] + x[2]).rotateLeft(18)
            x[3] = x[3] xor (x[15] + x[11]).rotateLeft(7)
            x[7] = x[7] xor (x[3] + x[15]).rotateLeft(9)
            x[11] = x[11] xor (x[7] + x[3]).rotateLeft(13)
            x[15] = x[15] xor (x[11] + x[7]).rotateLeft(18)
            // row round
            x[1] = x[1] xor (x[0] + x[3]).rotateLeft(7)
            x[2] = x[2] xor (x[1] + x[0]).rotateLeft(9)
            x[3] = x[3] xor (x[2] + x[1]).rotateLeft(13)
            x[0] = x[0] xor (x[3] + x[2]).rotateLeft(18)
            x[6] = x[6] xor (x[5] + x[4]).rotateLeft(7)
            x[7] = x[7] xor (x[6] + x[5]).rotateLeft(9)
            x[4] = x[4] xor (x[7] + x[6]).rotateLeft(13)
            x[5] = x[5] xor (x[4] + x[7]).rotateLeft(18)
            x[11] = x[11] xor (x[10] + x[9]).rotateLeft(7)
            x[8] = x[8] xor (x[11] + x[10]).rotateLeft(9)
            x[9] = x[9] xor (x[8] + x[11]).rotateLeft(13)
            x[10] = x[10] xor (x[9] + x[8]).rotateLeft(18)
            x[12] = x[12] xor (x[15] + x[14]).rotateLeft(7)
            x[13] = x[13] xor (x[12] + x[15]).rotateLeft(9)
            x[14] = x[14] xor (x[13] + x[12]).rotateLeft(13)
            x[15] = x[15] xor (x[14] + x[13]).rotateLeft(18)
        }
        val outWords = intArrayOf(x[0], x[5], x[10], x[15], x[6], x[7], x[8], x[9])
        val out = ByteArray(32)
        for (i in 0 until 8) {
            out[i * 4] = outWords[i].toByte()
            out[i * 4 + 1] = (outWords[i] ushr 8).toByte()
            out[i * 4 + 2] = (outWords[i] ushr 16).toByte()
            out[i * 4 + 3] = (outWords[i] ushr 24).toByte()
        }
        return out
    }

    // --- Mi cloud signed-request bits (PROTO-7) ---------------------------------

    /** 8 random bytes + big-endian u32 minutes-since-epoch. */
    fun genNonce(nowMs: Long = wallClockMs(), random8: ByteArray = randomBytes(8)): ByteArray {
        require(random8.size == 8)
        val nonce = ByteArray(12)
        random8.copyInto(nonce, 0)
        nonce.putBeU32(8, nowMs / 1000 / 60)
        return nonce
    }

    fun genSignedNonce(ssecurity: ByteArray, nonce: ByteArray): ByteArray =
        sha256(concatBytes(ssecurity, nonce))

    fun genSignatureB64(method: String, path: String, data: String, rc4Hash: String?, signedNonce: ByteArray): String {
        var s = "$method&$path&data=$data"
        if (rc4Hash != null) s += "&rc4_hash__=$rc4Hash"
        s += "&" + signedNonce.toBase64()
        return sha1(s.encodeToByteArray()).toBase64()
    }
}
