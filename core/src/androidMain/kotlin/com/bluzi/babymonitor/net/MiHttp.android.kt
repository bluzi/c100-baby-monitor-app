package com.bluzi.babymonitor.net

import com.bluzi.babymonitor.platform.ioDispatcher
import java.net.HttpURLConnection
import java.net.URI
import kotlinx.coroutines.withContext

actual val platformHttp: MiHttp get() = JavaMiHttp()

class JavaMiHttp : MiHttp {
    override suspend fun request(
        url: String,
        method: String,
        headers: Map<String, String>,
        body: String?,
    ): RawResponse = withContext(ioDispatcher) {
        val conn = URI(url).toURL().openConnection() as HttpURLConnection
        try {
            conn.requestMethod = method
            conn.instanceFollowRedirects = false
            conn.connectTimeout = HTTP_CONNECT_TIMEOUT_MS
            conn.readTimeout = HTTP_READ_TIMEOUT_MS
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
