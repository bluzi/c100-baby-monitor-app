package com.bluzi.babymonitor.xiaomi

import com.bluzi.babymonitor.net.MiHttp
import com.bluzi.babymonitor.net.XiaomiSocketClosed
import com.bluzi.babymonitor.net.RawResponse
import kotlinx.coroutines.test.runTest
import kotlin.test.assertFalse
import kotlin.test.assertFailsWith
import kotlin.test.Test

// BG-8: declaring "your session expired" DELETES the stored session and forces a new sign-in.
// Getting that wrong at 3am is worse than any other bug in the app: monitoring stops and the
// parent must find their Mi password to get it back. So expiry may only be declared when Xiaomi
// itself refused the long-lived token — never when we simply could not get a straight answer.

private class ScriptedHttp(val handler: (String) -> RawResponse) : MiHttp {
    override suspend fun request(
        url: String,
        method: String,
        headers: Map<String, String>,
        body: String?,
    ): RawResponse = handler(url)
}

private fun session() = Session(
    userId = "100001",
    cUserId = "CU1",
    passToken = "PT1",
    serviceToken = "ST1",
    ssecurity = ByteArray(16),
    region = "de",
)

private fun expiredApi() = RawResponse(401, "", emptyList(), "Unauthorized".encodeToByteArray())

class SessionExpiryTest {
    private suspend fun refreshWith(http: MiHttp) = MiCloud(http, session = session()).deviceList()

    @Test
    fun `AUTH-8+BG-8 Xiaomi refusing the long-lived token is a real expiry`() = runTest {
        // The account server answers properly and hands back no location: the token is dead.
        val http = ScriptedHttp { url ->
            if (url.contains("/pass/serviceLogin?")) {
                RawResponse(200, "", emptyList(), "&&&START&&&{\"code\":70016}".encodeToByteArray())
            } else {
                expiredApi()
            }
        }
        assertFailsWith<AuthExpiredException> {
            refreshWith(http)
        }
    }

    @Test
    fun `AUTH-8+BG-8 a captive portal or maintenance page must never sign the user out`() = runTest {
        // Hotel Wi-Fi (or a Xiaomi outage) answers HTML instead of a login body. The token may be
        // perfectly good — treating this as expiry would throw away a working session.
        val http = ScriptedHttp { url ->
            if (url.contains("/pass/serviceLogin?")) {
                RawResponse(200, "", emptyList(), "<html>Sign in to WiFi</html>".encodeToByteArray())
            } else {
                expiredApi()
            }
        }
        val err = assertFailsWith<Exception> {
            refreshWith(http)
        }
        assertFalse(err is AuthExpiredException, "a non-Xiaomi response must stay retryable")
    }

    @Test
    fun `AUTH-8+BG-8 a network failure during refresh must never sign the user out`() = runTest {
        val http = ScriptedHttp { url ->
            if (url.contains("/pass/serviceLogin?")) {
                // What a real transport failure looks like on every platform: the socket layer's
                // own error, not Xiaomi's. Wi-Fi dropped, DNS failed, captive portal.
                throw XiaomiSocketClosed("http: could not resolve account.xiaomi.com")
            } else {
                expiredApi()
            }
        }
        val err = assertFailsWith<Exception> {
            refreshWith(http)
        }
        assertFalse(err is AuthExpiredException, "a transport failure must stay retryable")
    }
}
