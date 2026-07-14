package com.bluzi.babymonitor.xiaomi

// URL helpers. These were java.net.URLEncoder and java.net.URI.resolve.
//
// urlEncode must stay byte-for-byte what Java's URLEncoder produced: the signed request form
// (PROTO-7) carries base64, and base64's '+' and '=' MUST arrive percent-encoded or the gateway
// rejects the signature. The interop vectors compare the whole encoded form against the reference
// implementation, so a drift here fails the build rather than the camera.

private const val UNRESERVED = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-*_"
private const val UPPER_HEX = "0123456789ABCDEF"

/** application/x-www-form-urlencoded, UTF-8 — space becomes '+', as Java's URLEncoder does. */
fun urlEncode(s: String): String = buildString {
    for (byte in s.encodeToByteArray()) {
        val c = byte.toInt().toChar()
        when {
            byte >= 0 && UNRESERVED.indexOf(c) >= 0 -> append(c)
            c == ' ' -> append('+')
            else -> {
                val v = byte.toInt() and 0xff
                append('%').append(UPPER_HEX[v ushr 4]).append(UPPER_HEX[v and 0x0f])
            }
        }
    }
}

fun urlDecode(s: String): String {
    val out = ArrayList<Byte>(s.length)
    var i = 0
    while (i < s.length) {
        when (val c = s[i]) {
            '+' -> { out.add(' '.code.toByte()); i++ }
            '%' -> {
                require(i + 2 < s.length) { "url: truncated escape" }
                out.add(s.substring(i + 1, i + 3).toInt(16).toByte())
                i += 3
            }
            else -> {
                for (b in c.toString().encodeToByteArray()) out.add(b)
                i++
            }
        }
    }
    return out.toByteArray().decodeToString()
}

private val SCHEME = Regex("^[a-zA-Z][a-zA-Z0-9+.\\-]*:")

/**
 * PROTO-4: resolve a redirect's Location against the URL it came from. Xiaomi's auth chain mixes
 * absolute URLs with absolute paths, and a mis-resolved hop loses the ssecurity that only ever
 * appears in one hop's headers.
 */
fun resolveUrl(base: String, location: String): String {
    if (location.isEmpty()) return base
    if (SCHEME.containsMatchIn(location)) return location

    val schemeEnd = base.indexOf("://")
    require(schemeEnd > 0) { "url: base has no scheme: $base" }
    val scheme = base.substring(0, schemeEnd)
    val afterScheme = base.substring(schemeEnd + 3)
    val authorityEnd = afterScheme.indexOfFirst { it == '/' || it == '?' || it == '#' }
    val authority = if (authorityEnd < 0) afterScheme else afterScheme.substring(0, authorityEnd)
    val path = if (authorityEnd < 0) "" else afterScheme.substring(authorityEnd)

    if (location.startsWith("//")) return "$scheme:$location"
    if (location.startsWith("/")) return "$scheme://$authority$location"

    // Relative to the base's directory.
    val basePath = path.substringBefore('?').substringBefore('#')
    val dir = basePath.substringBeforeLast('/', "") + "/"
    return "$scheme://$authority$dir$location"
}
