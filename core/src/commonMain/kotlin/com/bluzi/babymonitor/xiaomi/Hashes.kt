package com.bluzi.babymonitor.xiaomi

// MD5 / SHA-1 / SHA-256, in pure Kotlin.
//
// These were `java.security.MessageDigest`. They are textbook algorithms and porting them costs
// little; keeping ONE implementation for every platform buys a lot. The Mi signing scheme is
// exact — a byte out of place anywhere here and every signed request is rejected — so a second,
// platform-specific implementation would be a second thing that can silently disagree.
//
// Correctness is not asserted, it is proven: CryptoDifferentialTest checks all three against
// java.security.MessageDigest over thousands of random inputs, and the protocol interop vectors
// (from the working c100 implementation) run on both the JVM and macOS.

private fun ByteArray.padded(lengthBitsLittleEndian: Boolean): ByteArray {
    val bitLen = size.toLong() * 8
    val padLen = ((56 - (size + 1) % 64) + 64) % 64
    val out = ByteArray(size + 1 + padLen + 8)
    copyInto(out)
    out[size] = 0x80.toByte()
    val lenOff = size + 1 + padLen
    for (i in 0 until 8) {
        val shift = if (lengthBitsLittleEndian) 8 * i else 8 * (7 - i)
        out[lenOff + i] = ((bitLen ushr shift) and 0xff).toByte()
    }
    return out
}

private fun Int.rotl(bits: Int): Int = (this shl bits) or (this ushr (32 - bits))

private fun Int.rotr(bits: Int): Int = (this ushr bits) or (this shl (32 - bits))

fun md5Digest(data: ByteArray): ByteArray {
    val s = intArrayOf(
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    )
    // k[i] = floor(2^32 * abs(sin(i + 1)))
    val k = intArrayOf(
        -0x28955b88, -0x173848aa, 0x242070db, -0x3e423112, -0xa83f051, 0x4787c62a, -0x57cfb9ed, -0x2b96aff,
        0x698098d8, -0x74bb0851, -0xa44f, -0x76a32842, 0x6b901122, -0x2678e6d, -0x5986bc72, 0x49b40821,
        -0x9e1da9e, -0x3fbf4cc0, 0x265e5a51, -0x16493856, -0x29d0efa3, 0x02441453, -0x275e197f, -0x182c0438,
        0x21e1cde6, -0x3cc8f82a, -0xb2af279, 0x455a14ed, -0x561c16fb, -0x3105c08, 0x676f02d9, -0x72d5b376,
        -0x5c6be, -0x788e097f, 0x6d9d6122, -0x21ac7f4, -0x5b4115bc, 0x4bdecfa9, -0x944b4a0, -0x41404390,
        0x289b7ec6, -0x155ed806, -0x2b10cf7b, 0x04881d05, -0x262b2fc7, -0x1924661b, 0x1fa27cf8, -0x3b53a99b,
        -0xbd6ddbc, 0x432aff97, -0x546bdc59, -0x36c5fc7, 0x655b59c3, -0x70f3336e, -0x100b83, -0x7a7ba22f,
        0x6fa87e4f, -0x1d31920, -0x5cfebcec, 0x4e0811a1, -0x8ac817e, -0x42c50dcb, 0x2ad7d2bb, -0x14792c6f,
    )
    var a0 = 0x67452301
    var b0 = -0x10325477
    var c0 = -0x67452302
    var d0 = 0x10325476

    val msg = data.padded(lengthBitsLittleEndian = true)
    for (chunk in msg.indices step 64) {
        val m = IntArray(16) { i ->
            val o = chunk + i * 4
            (msg[o].toInt() and 0xff) or ((msg[o + 1].toInt() and 0xff) shl 8) or
                ((msg[o + 2].toInt() and 0xff) shl 16) or ((msg[o + 3].toInt() and 0xff) shl 24)
        }
        var a = a0
        var b = b0
        var c = c0
        var d = d0
        for (i in 0 until 64) {
            var f: Int
            val g: Int
            when {
                i < 16 -> { f = (b and c) or (b.inv() and d); g = i }
                i < 32 -> { f = (d and b) or (d.inv() and c); g = (5 * i + 1) % 16 }
                i < 48 -> { f = b xor c xor d; g = (3 * i + 5) % 16 }
                else -> { f = c xor (b or d.inv()); g = (7 * i) % 16 }
            }
            f += a + k[i] + m[g]
            a = d
            d = c
            c = b
            b += f.rotl(s[i])
        }
        a0 += a
        b0 += b
        c0 += c
        d0 += d
    }

    val out = ByteArray(16)
    for ((i, v) in intArrayOf(a0, b0, c0, d0).withIndex()) {
        out[i * 4] = v.toByte()
        out[i * 4 + 1] = (v ushr 8).toByte()
        out[i * 4 + 2] = (v ushr 16).toByte()
        out[i * 4 + 3] = (v ushr 24).toByte()
    }
    return out
}

fun sha1Digest(data: ByteArray): ByteArray {
    var h0 = 0x67452301
    var h1 = -0x10325477 // 0xEFCDAB89
    var h2 = -0x67452302 // 0x98BADCFE
    var h3 = 0x10325476
    var h4 = -0x3c2d1e10 // 0xC3D2E1F0

    val msg = data.padded(lengthBitsLittleEndian = false)
    val w = IntArray(80)
    for (chunk in msg.indices step 64) {
        for (i in 0 until 16) {
            val o = chunk + i * 4
            w[i] = ((msg[o].toInt() and 0xff) shl 24) or ((msg[o + 1].toInt() and 0xff) shl 16) or
                ((msg[o + 2].toInt() and 0xff) shl 8) or (msg[o + 3].toInt() and 0xff)
        }
        for (i in 16 until 80) w[i] = (w[i - 3] xor w[i - 8] xor w[i - 14] xor w[i - 16]).rotl(1)

        var a = h0
        var b = h1
        var c = h2
        var d = h3
        var e = h4
        for (i in 0 until 80) {
            val (f, k) = when {
                i < 20 -> ((b and c) or (b.inv() and d)) to 0x5a827999
                i < 40 -> (b xor c xor d) to 0x6ed9eba1
                i < 60 -> ((b and c) or (b and d) or (c and d)) to -0x70e44324
                else -> (b xor c xor d) to -0x359d3e2a
            }
            val temp = a.rotl(5) + f + e + k + w[i]
            e = d
            d = c
            c = b.rotl(30)
            b = a
            a = temp
        }
        h0 += a
        h1 += b
        h2 += c
        h3 += d
        h4 += e
    }

    val out = ByteArray(20)
    for ((i, v) in intArrayOf(h0, h1, h2, h3, h4).withIndex()) {
        out[i * 4] = (v ushr 24).toByte()
        out[i * 4 + 1] = (v ushr 16).toByte()
        out[i * 4 + 2] = (v ushr 8).toByte()
        out[i * 4 + 3] = v.toByte()
    }
    return out
}

private val SHA256_K = intArrayOf(
    0x428a2f98, 0x71374491, -0x4a3f0431, -0x164a245b, 0x3956c25b, 0x59f111f1, -0x6dc07d5c, -0x54e3a12b,
    -0x27f85568, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, -0x7f214e02, -0x6423f959, -0x3e640e8c,
    -0x1b64963f, -0x1041b87a, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    -0x67c1aeae, -0x57ce3993, -0x4ffcd838, -0x40a68039, -0x391ff40d, -0x2a586eb9, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, -0x7e3d36d2, -0x6d8dd37b,
    -0x5d40175f, -0x57e599b5, -0x3db47490, -0x3893ae5d, -0x2e6d17e7, -0x2966f9dc, -0xbf1ca7b, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, -0x7b3787ec, -0x7338fdf8, -0x6f410006, -0x5baf9315, -0x41065c09, -0x398e870e,
)

fun sha256Digest(data: ByteArray): ByteArray {
    val h = intArrayOf(
        0x6a09e667, -0x4498517b, 0x3c6ef372, -0x5ab00ac6,
        0x510e527f, -0x64fa9774, 0x1f83d9ab, 0x5be0cd19,
    )

    val msg = data.padded(lengthBitsLittleEndian = false)
    val w = IntArray(64)
    for (chunk in msg.indices step 64) {
        for (i in 0 until 16) {
            val o = chunk + i * 4
            w[i] = ((msg[o].toInt() and 0xff) shl 24) or ((msg[o + 1].toInt() and 0xff) shl 16) or
                ((msg[o + 2].toInt() and 0xff) shl 8) or (msg[o + 3].toInt() and 0xff)
        }
        for (i in 16 until 64) {
            val s0 = w[i - 15].rotr(7) xor w[i - 15].rotr(18) xor (w[i - 15] ushr 3)
            val s1 = w[i - 2].rotr(17) xor w[i - 2].rotr(19) xor (w[i - 2] ushr 10)
            w[i] = w[i - 16] + s0 + w[i - 7] + s1
        }

        var a = h[0]
        var b = h[1]
        var c = h[2]
        var d = h[3]
        var e = h[4]
        var f = h[5]
        var g = h[6]
        var hh = h[7]
        for (i in 0 until 64) {
            val s1 = e.rotr(6) xor e.rotr(11) xor e.rotr(25)
            val ch = (e and f) xor (e.inv() and g)
            val temp1 = hh + s1 + ch + SHA256_K[i] + w[i]
            val s0 = a.rotr(2) xor a.rotr(13) xor a.rotr(22)
            val maj = (a and b) xor (a and c) xor (b and c)
            val temp2 = s0 + maj
            hh = g
            g = f
            f = e
            e = d + temp1
            d = c
            c = b
            b = a
            a = temp1 + temp2
        }
        h[0] += a
        h[1] += b
        h[2] += c
        h[3] += d
        h[4] += e
        h[5] += f
        h[6] += g
        h[7] += hh
    }

    val out = ByteArray(32)
    for ((i, v) in h.withIndex()) {
        out[i * 4] = (v ushr 24).toByte()
        out[i * 4 + 1] = (v ushr 16).toByte()
        out[i * 4 + 2] = (v ushr 8).toByte()
        out[i * 4 + 3] = v.toByte()
    }
    return out
}
