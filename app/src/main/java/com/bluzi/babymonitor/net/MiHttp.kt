package com.bluzi.babymonitor.net

import java.net.HttpURLConnection
import java.net.URI
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Deliberately dumb HTTP client: no cookie jar, no automatic redirects, no header rewriting.
 * The Mi gateway rejects signed requests carrying any cookie beyond the ones we set (see
 * PROTO-9), and ssecurity hides in redirect-hop headers (PROTO-4) — both need this control.
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
}

interface MiHttp {
    suspend fun request(
        url: String,
        method: String = "GET",
        headers: Map<String, String> = emptyMap(),
        body: String? = null,
    ): RawResponse
}

class JavaMiHttp : MiHttp {
    override suspend fun request(
        url: String,
        method: String,
        headers: Map<String, String>,
        body: String?,
    ): RawResponse = withContext(Dispatchers.IO) {
        val conn = URI(url).toURL().openConnection() as HttpURLConnection
        try {
            conn.requestMethod = method
            conn.instanceFollowRedirects = false
            conn.connectTimeout = 15_000
            conn.readTimeout = 20_000
            for ((k, v) in headers) conn.setRequestProperty(k, v)
            if (body != null) {
                if (headers.keys.none { it.equals("Content-Type", true) }) {
                    conn.setRequestProperty("Content-Type", "application/x-www-form-urlencoded")
                }
                conn.doOutput = true
                conn.outputStream.use { it.write(body.toByteArray()) }
            }
            val status = conn.responseCode
            val respHeaders = mutableListOf<Pair<String, String>>()
            for ((key, values) in conn.headerFields) {
                if (key == null) continue
                for (v in values) respHeaders.add(key to v)
            }
            val stream = if (status >= 400) conn.errorStream else conn.inputStream
            val bytes = stream?.use { it.readBytes() } ?: ByteArray(0)
            RawResponse(status, url, respHeaders, bytes)
        } finally {
            conn.disconnect()
        }
    }
}
