package com.bluzi.babymonitor.xiaomi

import java.security.MessageDigest
import kotlin.random.Random
import org.bouncycastle.math.ec.rfc7748.X25519
import org.junit.Assert.assertArrayEquals
import org.junit.Test

/**
 * The hashes and X25519 used to be `java.security.MessageDigest` and BouncyCastle. They are now
 * pure Kotlin, because one implementation shared by every app cannot disagree with itself — where
 * two platform-specific ones quietly can, and the first symptom would be a monitor that cannot
 * reach the camera at 3am.
 *
 * Replacing working crypto deserves more than "the vectors still pass": the vectors are a handful
 * of fixed inputs, and a subtly wrong implementation can satisfy them. So this runs the old
 * implementation and the new one side by side over thousands of random inputs and demands they
 * agree byte for byte. It is JVM-only for the obvious reason — the reference is JVM-only. That is
 * fine: what it proves is a property of the *algorithm*, and the algorithm is shared.
 *
 * (This is not idle caution. It is what caught SHA-1's initial state being one hex digit short.)
 */
class CryptoDifferentialTest {
    private val random = Random(20260714)

    private fun inputs(): List<ByteArray> = buildList {
        add(ByteArray(0))
        // Every length either side of a block boundary — that is where padding gets it wrong.
        for (n in 1..200) add(ByteArray(n) { random.nextInt(256).toByte() })
        for (n in listOf(1000, 4096, 10_000)) add(ByteArray(n) { random.nextInt(256).toByte() })
    }

    private fun assertMatchesJdk(algorithm: String, ours: (ByteArray) -> ByteArray) {
        val jdk = MessageDigest.getInstance(algorithm)
        for (input in inputs()) {
            assertArrayEquals(
                "$algorithm disagrees on a ${input.size}-byte input",
                jdk.digest(input),
                ours(input),
            )
        }
    }

    @Test
    fun `PROTO-1 md5 matches the JDK on every input`() = assertMatchesJdk("MD5", ::md5Digest)

    @Test
    fun `PROTO-7 sha1 matches the JDK on every input`() = assertMatchesJdk("SHA-1", ::sha1Digest)

    @Test
    fun `PROTO-7 sha256 matches the JDK on every input`() = assertMatchesJdk("SHA-256", ::sha256Digest)

    @Test
    fun `PROTO-13 x25519 base point matches BouncyCastle for random private keys`() {
        repeat(200) {
            val priv = ByteArray(32) { random.nextInt(256).toByte() }
            val reference = ByteArray(32)
            X25519.scalarMultBase(priv, 0, reference, 0)
            assertArrayEquals(reference, x25519ScalarMultBase(priv))
        }
    }

    @Test
    fun `PROTO-13 x25519 scalar mult matches BouncyCastle for random key pairs`() {
        repeat(200) {
            val priv = ByteArray(32) { random.nextInt(256).toByte() }
            val peer = ByteArray(32)
            X25519.scalarMultBase(ByteArray(32) { random.nextInt(256).toByte() }, 0, peer, 0)

            val reference = ByteArray(32)
            X25519.scalarMult(priv, 0, peer, 0, reference, 0)
            assertArrayEquals(reference, x25519ScalarMult(priv, peer))
        }
    }

    @Test
    fun `PROTO-13 both sides of a generated exchange agree on the shared key`() {
        repeat(50) {
            val (pubA, privA) = Crypto.generateBoxKeyPair()
            val (pubB, privB) = Crypto.generateBoxKeyPair()
            assertArrayEquals(
                Crypto.calcSharedKey(pubB.toHex(), privA.toHex()),
                Crypto.calcSharedKey(pubA.toHex(), privB.toHex()),
            )
        }
    }
}
