package com.bluzi.babymonitor.xiaomi

private const val HEX = "0123456789abcdef"

fun ByteArray.toHex(): String = buildString(size * 2) {
    for (b in this@toHex) {
        val v = b.toInt() and 0xff
        append(HEX[v ushr 4])
        append(HEX[v and 0x0f])
    }
}

fun String.hexToBytes(): ByteArray {
    require(length % 2 == 0) { "hex: odd length" }
    return ByteArray(length / 2) { i ->
        substring(i * 2, i * 2 + 2).toInt(16).toByte()
    }
}

// Base64 (RFC 4648, padded) — java.util.Base64 is JVM-only, and the signing scheme is exact:
// every signature and every ssecurity travels through here. Pinned by the interop vectors.
private const val B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"

fun ByteArray.toBase64(): String {
    val out = StringBuilder((size + 2) / 3 * 4)
    var i = 0
    while (i + 2 < size) {
        val n = ((this[i].toInt() and 0xff) shl 16) or
            ((this[i + 1].toInt() and 0xff) shl 8) or
            (this[i + 2].toInt() and 0xff)
        out.append(B64[(n ushr 18) and 63]).append(B64[(n ushr 12) and 63])
        out.append(B64[(n ushr 6) and 63]).append(B64[n and 63])
        i += 3
    }
    when (size - i) {
        1 -> {
            val n = (this[i].toInt() and 0xff) shl 16
            out.append(B64[(n ushr 18) and 63]).append(B64[(n ushr 12) and 63]).append("==")
        }
        2 -> {
            val n = ((this[i].toInt() and 0xff) shl 16) or ((this[i + 1].toInt() and 0xff) shl 8)
            out.append(B64[(n ushr 18) and 63]).append(B64[(n ushr 12) and 63])
            out.append(B64[(n ushr 6) and 63]).append('=')
        }
    }
    return out.toString()
}

fun String.base64ToBytes(): ByteArray {
    val clean = filter { it != '\n' && it != '\r' }.trimEnd('=')
    val out = ByteArray(clean.length * 6 / 8)
    var buffer = 0
    var bits = 0
    var o = 0
    for (c in clean) {
        val v = B64.indexOf(c)
        require(v >= 0) { "base64: bad character '$c'" }
        buffer = (buffer shl 6) or v
        bits += 6
        if (bits >= 8) {
            bits -= 8
            out[o++] = ((buffer ushr bits) and 0xff).toByte()
        }
    }
    return out
}

fun concatBytes(vararg parts: ByteArray): ByteArray {
    val out = ByteArray(parts.sumOf { it.size })
    var off = 0
    for (p in parts) {
        p.copyInto(out, off)
        off += p.size
    }
    return out
}

fun ByteArray.beU16(offset: Int): Int =
    ((this[offset].toInt() and 0xff) shl 8) or (this[offset + 1].toInt() and 0xff)

fun ByteArray.beU32(offset: Int): Long =
    ((this[offset].toLong() and 0xff) shl 24) or
        ((this[offset + 1].toLong() and 0xff) shl 16) or
        ((this[offset + 2].toLong() and 0xff) shl 8) or
        (this[offset + 3].toLong() and 0xff)

fun ByteArray.leU32(offset: Int): Long =
    (this[offset].toLong() and 0xff) or
        ((this[offset + 1].toLong() and 0xff) shl 8) or
        ((this[offset + 2].toLong() and 0xff) shl 16) or
        ((this[offset + 3].toLong() and 0xff) shl 24)

fun ByteArray.leU64(offset: Int): Long = leU32(offset) or (leU32(offset + 4) shl 32)

fun ByteArray.putBeU16(offset: Int, value: Int) {
    this[offset] = (value ushr 8).toByte()
    this[offset + 1] = value.toByte()
}

fun ByteArray.putBeU32(offset: Int, value: Long) {
    this[offset] = (value ushr 24).toByte()
    this[offset + 1] = (value ushr 16).toByte()
    this[offset + 2] = (value ushr 8).toByte()
    this[offset + 3] = value.toByte()
}

fun ByteArray.putLeU32(offset: Int, value: Long) {
    this[offset] = value.toByte()
    this[offset + 1] = (value ushr 8).toByte()
    this[offset + 2] = (value ushr 16).toByte()
    this[offset + 3] = (value ushr 24).toByte()
}

fun ByteArray.putLeU64(offset: Int, value: Long) {
    putLeU32(offset, value and 0xffffffffL)
    putLeU32(offset + 4, value ushr 32)
}
