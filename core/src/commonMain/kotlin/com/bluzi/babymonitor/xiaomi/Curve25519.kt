package com.bluzi.babymonitor.xiaomi

// X25519 scalar multiplication, in pure Kotlin — a port of TweetNaCl's crypto_scalarmult
// (the same primitive c100 uses via tweetnacl, and the same one BouncyCastle's X25519 provided
// on Android before this).
//
// Why port it rather than pick a per-platform library: this computes the NaCl box shared key
// (PROTO-13) that every media packet is encrypted with. One implementation, used by every app,
// cannot disagree with itself. CryptoDifferentialTest proves it byte-identical to BouncyCastle
// over random keys, and the interop vectors pin it to the implementation that talks to the
// real camera.
//
// Field elements are 16 limbs of 16 bits, held in Longs — the representation TweetNaCl uses.
// It is constant-time by construction (no data-dependent branches; the conditional swap is
// arithmetic), which is why the port stays faithful rather than "tidied".

private const val LIMBS = 16

private fun gf(vararg init: Long): LongArray {
    val r = LongArray(LIMBS)
    init.copyInto(r)
    return r
}

private val GF_121665 = gf(0xdb41L, 1L)

private fun car25519(o: LongArray) {
    for (i in 0 until LIMBS) {
        o[i] += 1L shl 16
        val c = o[i] shr 16
        if (i < 15) o[i + 1] += c - 1 else o[0] += 38 * (c - 1)
        o[i] -= c shl 16
    }
}

/** Conditional swap of p and q, without branching on the secret bit [b]. */
private fun sel25519(p: LongArray, q: LongArray, b: Int) {
    val c = (b - 1).toLong().inv()
    for (i in 0 until LIMBS) {
        val t = c and (p[i] xor q[i])
        p[i] = p[i] xor t
        q[i] = q[i] xor t
    }
}

private fun pack25519(o: ByteArray, n: LongArray) {
    val t = n.copyOf()
    car25519(t)
    car25519(t)
    car25519(t)
    val m = LongArray(LIMBS)
    repeat(2) {
        m[0] = t[0] - 0xffed
        for (i in 1 until 15) {
            m[i] = t[i] - 0xffff - ((m[i - 1] shr 16) and 1)
            m[i - 1] = m[i - 1] and 0xffff
        }
        m[15] = t[15] - 0x7fff - ((m[14] shr 16) and 1)
        val b = ((m[15] shr 16) and 1).toInt()
        m[14] = m[14] and 0xffff
        sel25519(t, m, 1 - b)
    }
    for (i in 0 until LIMBS) {
        o[2 * i] = (t[i] and 0xff).toByte()
        o[2 * i + 1] = ((t[i] shr 8) and 0xff).toByte()
    }
}

private fun unpack25519(o: LongArray, n: ByteArray) {
    for (i in 0 until LIMBS) {
        o[i] = (n[2 * i].toLong() and 0xff) + ((n[2 * i + 1].toLong() and 0xff) shl 8)
    }
    o[15] = o[15] and 0x7fff
}

private fun add(o: LongArray, a: LongArray, b: LongArray) {
    for (i in 0 until LIMBS) o[i] = a[i] + b[i]
}

private fun sub(o: LongArray, a: LongArray, b: LongArray) {
    for (i in 0 until LIMBS) o[i] = a[i] - b[i]
}

private fun mul(o: LongArray, a: LongArray, b: LongArray) {
    val t = LongArray(31)
    for (i in 0 until LIMBS) {
        val v = a[i]
        for (j in 0 until LIMBS) t[i + j] += v * b[j]
    }
    // Fold the top half back in: 2^256 ≡ 38 (mod 2^255 - 19).
    for (i in 0 until 15) t[i] += 38 * t[i + 16]
    t.copyInto(o, 0, 0, LIMBS)
    car25519(o)
    car25519(o)
}

private fun sq(o: LongArray, a: LongArray) = mul(o, a, a)

/** o = 1/i in the field, by Fermat: i^(p-2). */
private fun inv25519(o: LongArray, i: LongArray) {
    val c = i.copyOf()
    for (a in 253 downTo 0) {
        sq(c, c)
        if (a != 2 && a != 4) mul(c, c, i)
    }
    c.copyInto(o)
}

/** Montgomery ladder: q = scalar · point, both 32 bytes. The scalar is clamped per RFC 7748. */
fun x25519ScalarMult(scalar: ByteArray, point: ByteArray): ByteArray {
    require(scalar.size == 32) { "x25519: scalar must be 32 bytes" }
    require(point.size == 32) { "x25519: point must be 32 bytes" }

    val z = scalar.copyOf()
    z[31] = ((z[31].toInt() and 127) or 64).toByte()
    z[0] = (z[0].toInt() and 248).toByte()

    val x = LongArray(LIMBS)
    unpack25519(x, point)

    val a = LongArray(LIMBS)
    val b = x.copyOf()
    val c = LongArray(LIMBS)
    val d = LongArray(LIMBS)
    val e = LongArray(LIMBS)
    val f = LongArray(LIMBS)
    a[0] = 1
    d[0] = 1

    for (i in 254 downTo 0) {
        val r = ((z[i ushr 3].toInt() ushr (i and 7)) and 1)
        sel25519(a, b, r)
        sel25519(c, d, r)
        add(e, a, c)
        sub(a, a, c)
        add(c, b, d)
        sub(b, b, d)
        sq(d, e)
        sq(f, a)
        mul(a, c, a)
        mul(c, b, e)
        add(e, a, c)
        sub(a, a, c)
        sq(b, a)
        sub(c, d, f)
        mul(a, c, GF_121665)
        add(a, a, d)
        mul(c, c, a)
        mul(a, d, f)
        mul(d, b, x)
        sq(b, e)
        sel25519(a, b, r)
        sel25519(c, d, r)
    }

    inv25519(c, c)
    mul(a, a, c)
    val out = ByteArray(32)
    pack25519(out, a)
    return out
}

private val BASE_POINT = ByteArray(32).also { it[0] = 9 }

fun x25519ScalarMultBase(scalar: ByteArray): ByteArray = x25519ScalarMult(scalar, BASE_POINT)
