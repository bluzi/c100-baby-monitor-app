package com.bluzi.babymonitor.net

import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.addressOf
import kotlinx.cinterop.usePinned
import kotlinx.coroutines.suspendCancellableCoroutine
import platform.Foundation.NSData
import platform.Foundation.NSHTTPCookie
import platform.Foundation.NSHTTPURLResponse
import platform.Foundation.NSMutableURLRequest
import platform.Foundation.NSURL
import platform.Foundation.NSURLRequest
import platform.Foundation.NSURLResponse
import platform.Foundation.NSURLSession
import platform.Foundation.NSURLSessionConfiguration
import platform.Foundation.NSURLSessionDataTask
import platform.Foundation.NSURLSessionTask
import platform.Foundation.NSURLSessionTaskDelegateProtocol
import platform.Foundation.create
import platform.Foundation.dataTaskWithRequest
import platform.Foundation.setHTTPBody
import platform.Foundation.setHTTPMethod
import platform.Foundation.setValue
import platform.darwin.NSObject

actual val platformHttp: MiHttp get() = AppleMiHttp

/**
 * The same deliberately dumb HTTP client the JVM side has, built on NSURLSession — which takes
 * more talking out of than HttpURLConnection did, because it helpfully does the two things we
 * must not do:
 *
 *  - PROTO-9: it keeps an ambient cookie store. Any cookie beyond the ones we set ourselves makes
 *    the Mi gateway reject the signature, so the store is switched off entirely — no jar, no
 *    saving, no sending.
 *  - PROTO-4: it follows redirects. ssecurity appears only in one hop's headers, so the chain has
 *    to be walked by hand; the delegate below refuses every redirect and hands the 302 back to us.
 *
 * Getting either wrong looks exactly like an expired session, which is the failure that signs a
 * parent out in the middle of the night. Hence the noise about it here.
 */
@OptIn(ExperimentalForeignApi::class)
object AppleMiHttp : MiHttp {
    private class NoRedirects : NSObject(), NSURLSessionTaskDelegateProtocol {
        override fun URLSession(
            session: NSURLSession,
            task: NSURLSessionTask,
            willPerformHTTPRedirection: NSHTTPURLResponse,
            newRequest: NSURLRequest,
            completionHandler: (NSURLRequest?) -> Unit,
        ) {
            completionHandler(null) // PROTO-4: we walk the chain ourselves
        }
    }

    private val session: NSURLSession by lazy {
        val config = NSURLSessionConfiguration.ephemeralSessionConfiguration()
        // No jar to save into, and nothing sent from one: between them these two leave the
        // session with no cookie behaviour at all, which is exactly what PROTO-9 needs.
        config.HTTPCookieStorage = null
        config.HTTPShouldSetCookies = false
        config.timeoutIntervalForRequest = HTTP_READ_TIMEOUT_MS / 1000.0
        NSURLSession.sessionWithConfiguration(config, NoRedirects(), null)
    }

    override suspend fun request(
        url: String,
        method: String,
        headers: Map<String, String>,
        body: String?,
    ): RawResponse = suspendCancellableCoroutine { cont ->
        val target = NSURL(string = url)
        val request = NSMutableURLRequest.requestWithURL(target)
        request.setHTTPMethod(method)
        for ((k, v) in headers) request.setValue(v, forHTTPHeaderField = k)
        if (body != null) {
            if (headers.keys.none { it.equals("Content-Type", ignoreCase = true) }) {
                request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField = "Content-Type")
            }
            request.setHTTPBody(body.encodeToByteArray().toNSData())
        }

        val task: NSURLSessionDataTask = session.dataTaskWithRequest(request) { data, response, error ->
            when {
                error != null ->
                    cont.resumeWithException(
                        XiaomiSocketClosed("http: ${error.localizedDescription}"),
                    )
                response !is NSHTTPURLResponse ->
                    cont.resumeWithException(XiaomiSocketClosed("http: no response"))
                else -> cont.resume(
                    RawResponse(
                        status = response.statusCode.toInt(),
                        url = url,
                        headers = readHeaders(response, target),
                        body = data?.toByteArray() ?: ByteArray(0),
                    ),
                )
            }
        }
        cont.invokeOnCancellation { task.cancel() }
        task.resume()
    }

    /**
     * NSHTTPURLResponse merges duplicate headers into one comma-joined string — including
     * Set-Cookie, of which the Mi auth chain sends several per hop (userId, cUserId,
     * serviceToken…). Splitting that string on commas would be wrong, because cookie Expires
     * attributes contain commas of their own ("Expires=Wed, 09 Jun 2021"). NSHTTPCookie's parser
     * knows the grammar, so it does the splitting and each cookie is re-emitted as its own
     * Set-Cookie header — which is what the auth chain expects to read.
     */
    private fun readHeaders(response: NSHTTPURLResponse, url: NSURL): List<Pair<String, String>> {
        val out = mutableListOf<Pair<String, String>>()
        val fields = response.allHeaderFields
        for ((rawKey, rawValue) in fields) {
            val key = rawKey as? String ?: continue
            val value = rawValue as? String ?: continue
            if (key.equals("Set-Cookie", ignoreCase = true)) continue
            out.add(key to value)
        }
        @Suppress("UNCHECKED_CAST")
        val cookies = NSHTTPCookie.cookiesWithResponseHeaderFields(
            fields as Map<Any?, *>,
            forURL = url,
        )
        for (cookie in cookies) {
            val c = cookie as? NSHTTPCookie ?: continue
            out.add("Set-Cookie" to "${c.name}=${c.value}")
        }
        return out
    }
}

@OptIn(ExperimentalForeignApi::class)
internal fun ByteArray.toNSData(): NSData =
    if (isEmpty()) {
        NSData()
    } else {
        usePinned { NSData.create(bytes = it.addressOf(0), length = size.toULong()) }
    }

@OptIn(ExperimentalForeignApi::class)
internal fun NSData.toByteArray(): ByteArray {
    val size = length.toInt()
    if (size == 0) return ByteArray(0)
    val out = ByteArray(size)
    out.usePinned { pinned ->
        platform.posix.memcpy(pinned.addressOf(0), bytes, length)
    }
    return out
}
