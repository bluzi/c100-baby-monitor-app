package com.bluzi.babymonitor.net

/**
 * Deliberately dumb HTTP client: no cookie jar, no automatic redirects, no header rewriting.
 * The Mi gateway rejects signed requests carrying any cookie beyond the ones we set (see
 * PROTO-9), and ssecurity hides in redirect-hop headers (PROTO-4) — both need this control.
 *
 * Every platform must honour those two properties. They are not conveniences of the JVM's
 * HttpURLConnection: an implementation built on a stack with an ambient cookie store (Apple's
 * NSURLSession, say) has to switch it off explicitly, or signed requests start failing in ways
 * that look like an expired session.
 */
data class RawResponse(
    val status: Int,
    val url: String,
    val headers: List<Pair<String, String>>,
    val body: ByteArray,
) {
    fun header(name: String): String? =
        headers.firstOrNull { it.first.equals(name, ignoreCase = true) }?.second

    fun setCookies(): List<String> =
        headers.filter { it.first.equals("set-cookie", ignoreCase = true) }.map { it.second }

    override fun equals(other: Any?): Boolean =
        other is RawResponse && status == other.status && url == other.url &&
            headers == other.headers && body.contentEquals(other.body)

    override fun hashCode(): Int =
        ((status * 31 + url.hashCode()) * 31 + headers.hashCode()) * 31 + body.contentHashCode()
}

interface MiHttp {
    suspend fun request(
        url: String,
        method: String = "GET",
        headers: Map<String, String> = emptyMap(),
        body: String? = null,
    ): RawResponse
}

/** The platform's real HTTP client. */
expect val platformHttp: MiHttp

internal const val HTTP_CONNECT_TIMEOUT_MS = 15_000
internal const val HTTP_READ_TIMEOUT_MS = 20_000
