package com.bluzi.babymonitor.xiaomi

import com.bluzi.babymonitor.net.MiHttp
import com.bluzi.babymonitor.net.RawResponse
import java.net.URLDecoder
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

private class FakeMiHttp : MiHttp {
    data class Req(val url: String, val method: String, val headers: Map<String, String>, val body: String?)

    val log = mutableListOf<Req>()
    var handler: (Req) -> RawResponse = { error("no handler for ${it.url}") }

    override suspend fun request(
        url: String,
        method: String,
        headers: Map<String, String>,
        body: String?,
    ): RawResponse {
        val req = Req(url, method, headers, body)
        log.add(req)
        return handler(req)
    }
}

private fun loginBody(json: String) = "&&&START&&&$json".toByteArray()

private fun resp(
    status: Int = 200,
    body: ByteArray = ByteArray(0),
    headers: List<Pair<String, String>> = emptyList(),
) = RawResponse(status, "", headers, body)

private fun parseForm(body: String): Map<String, String> =
    body.split("&").associate {
        val (k, v) = it.split("=", limit = 2)
        URLDecoder.decode(k, "UTF-8") to URLDecoder.decode(v, "UTF-8")
    }

private val SSECURITY_B64 = "EBESExQVFhcYGRobHB0eHw=="

class MiCloudTest {
    // ---- helpers ------------------------------------------------------------

    private fun serviceLoginContext() = loginBody(
        """{"qs":"%3Fsid%3Dxiaomiio","_sign":"SIGN1","sid":"xiaomiio","callback":"https://sts.api.io.mi.com/sts"}""",
    )

    /** Wire a happy-path credential login into the fake. */
    private fun scriptHappyLogin(http: FakeMiHttp, auth2Extra: (FakeMiHttp.Req) -> RawResponse? = { null }) {
        http.handler = { req ->
            val url = req.url
            when {
                url.startsWith("https://account.xiaomi.com/pass/serviceLogin?") ->
                    resp(body = serviceLoginContext())
                url.startsWith("https://account.xiaomi.com/pass/serviceLoginAuth2") ->
                    auth2Extra(req) ?: resp(
                        body = loginBody(
                            """{"ssecurity":"$SSECURITY_B64","passToken":"PT1","location":"https://sts.api.io.mi.com/hop1"}""",
                        ),
                    )
                url == "https://sts.api.io.mi.com/hop1" -> resp(
                    status = 302,
                    headers = listOf(
                        "Location" to "/hop2",
                        "Set-Cookie" to "userId=100001; Path=/; HttpOnly",
                        "Set-Cookie" to "cUserId=CU1; Path=/",
                    ),
                )
                url == "https://sts.api.io.mi.com/hop2" -> resp(
                    headers = listOf("Set-Cookie" to "serviceToken=ST1; Path=/"),
                )
                else -> error("unexpected url: $url")
            }
        }
    }

    // ---- login --------------------------------------------------------------

    @Test
    fun `AUTH-2 valid credentials complete login and yield a full session`() = runTest {
        val http = FakeMiHttp()
        scriptHappyLogin(http)

        val cloud = MiCloud(http, region = "de")
        val result = cloud.login("parent@example.com", "hunter2")

        val ok = result as LoginResult.Ok
        assertEquals("100001", ok.session.userId)
        assertEquals("CU1", ok.session.cUserId)
        assertEquals("ST1", ok.session.serviceToken)
        assertEquals("PT1", ok.session.passToken)
        assertEquals(SSECURITY_B64, ok.session.ssecurity.toBase64())
        assertEquals("de", ok.session.region)
    }

    @Test
    fun `PROTO-1+3 the auth2 form carries the hashed password and serviceLogin context`() = runTest {
        val http = FakeMiHttp()
        scriptHappyLogin(http)
        MiCloud(http, region = "de").login("parent@example.com", "hunter2")

        val auth2 = http.log.first { it.url.contains("serviceLoginAuth2") }
        val form = parseForm(auth2.body!!)
        assertEquals("2AB96390C7DBE3439DE74D0C9B0B1767", form["hash"]) // vector for "hunter2"
        assertEquals("parent@example.com", form["user"])
        assertEquals("xiaomiio", form["sid"])
        assertEquals("SIGN1", form["_sign"])
        assertEquals("true", form["_json"])
        assertTrue(auth2.headers["Cookie"]!!.contains("deviceId="))
    }

    @Test
    fun `PROTO-4 the redirect chain is walked manually collecting cookies hop by hop`() = runTest {
        val http = FakeMiHttp()
        scriptHappyLogin(http)
        MiCloud(http, region = "de").login("parent@example.com", "hunter2")

        val urls = http.log.map { it.url }
        assertTrue(urls.contains("https://sts.api.io.mi.com/hop1"))
        assertTrue(urls.contains("https://sts.api.io.mi.com/hop2")) // relative Location resolved
    }

    @Test
    fun `PROTO-2 a login response without the START prefix is an error`() = runTest {
        val http = FakeMiHttp()
        http.handler = { resp(body = "<html>maintenance</html>".toByteArray()) }
        val err = assertThrows(XiaomiException::class.java) {
            kotlinx.coroutines.runBlocking { MiCloud(http).login("u", "p") }
        }
        assertTrue(err.message!!.contains("unexpected login response"))
    }

    @Test
    fun `AUTH-9 a rejected login surfaces a readable error`() = runTest {
        val http = FakeMiHttp()
        http.handler = { req ->
            when {
                req.url.contains("serviceLogin?") -> resp(body = serviceLoginContext())
                req.url.contains("serviceLoginAuth2") ->
                    resp(body = loginBody("""{"code":70016,"desc":"wrong password"}"""))
                else -> error("unexpected: ${req.url}")
            }
        }
        val err = assertThrows(XiaomiException::class.java) {
            kotlinx.coroutines.runBlocking { MiCloud(http).login("u", "wrong") }
        }
        // AUTH-9 says *readable*: the Xiaomi wrong-credentials code maps to plain words,
        // and no raw response JSON ever reaches the user.
        assertEquals("Wrong account or password.", err.message)
    }

    @Test
    fun `AUTH-9 an unrecognised login failure is readable, never raw JSON`() = runTest {
        val http = FakeMiHttp()
        http.handler = { req ->
            when {
                req.url.contains("serviceLogin?") -> resp(body = serviceLoginContext())
                req.url.contains("serviceLoginAuth2") ->
                    resp(body = loginBody("""{"code":9999,"desc":"internal error","weird":{"nested":"blob"}}"""))
                else -> error("unexpected: ${req.url}")
            }
        }
        val err = assertThrows(XiaomiException::class.java) {
            kotlinx.coroutines.runBlocking { MiCloud(http).login("u", "p") }
        }
        assertEquals("Sign-in failed: internal error (code 9999)", err.message)
        assertFalse(err.message!!.contains("{"))
    }

    @Test
    fun `AUTH-3 a captcha demand surfaces the image and the resubmission carries the code`() = runTest {
        val http = FakeMiHttp()
        val captchaBytes = byteArrayOf(0x11, 0x22, 0x33)
        var auth2Calls = 0
        scriptHappyLogin(http) { req ->
            auth2Calls++
            if (auth2Calls == 1) {
                resp(body = loginBody("""{"captchaURL":"/pass/captcha?x=1"}"""))
            } else {
                // Resubmission must carry the code + ick cookie (verified below).
                assertEquals("abcd", parseForm(req.body!!)["captCode"])
                assertTrue(req.headers["Cookie"]!!.contains("ick=ICK1"))
                null // fall through to the happy-path auth2 response
            }
        }
        val baseHandler = http.handler
        http.handler = { req ->
            if (req.url == "https://account.xiaomi.com/pass/captcha?x=1") {
                resp(
                    body = captchaBytes,
                    headers = listOf("Content-Type" to "image/jpeg", "Set-Cookie" to "ick=ICK1; Path=/"),
                )
            } else {
                baseHandler(req)
            }
        }

        val cloud = MiCloud(http, region = "de")
        val result = cloud.login("parent@example.com", "hunter2")
        val captcha = result as LoginResult.Captcha
        assertEquals("image/jpeg", captcha.contentType)
        assertEquals(captchaBytes.toList(), captcha.image.toList())

        val after = captcha.submit("abcd")
        assertTrue(after is LoginResult.Ok)
    }

    @Test
    fun `AUTH-4+PROTO-6 two-factor flow reveals the masked target and completes with the ticket`() = runTest {
        val http = FakeMiHttp()
        http.handler = { req ->
            val url = req.url
            when {
                url.startsWith("https://account.xiaomi.com/pass/serviceLogin?") ->
                    resp(body = serviceLoginContext())
                url.startsWith("https://account.xiaomi.com/pass/serviceLoginAuth2") ->
                    resp(body = loginBody("""{"notificationUrl":"https://account.xiaomi.com/fe/service/identity/authStart?context=CTX"}"""))
                url == "https://account.xiaomi.com/identity/list?context=CTX" -> resp(
                    body = loginBody("""{"code":0,"flag":4}"""),
                    headers = listOf("Set-Cookie" to "identity_session=IS1; Path=/"),
                )
                url.startsWith("https://account.xiaomi.com/identity/auth/verifyPhone?_flag=4&_json=true") -> {
                    assertTrue(req.headers["Cookie"]!!.contains("identity_session=IS1"))
                    resp(body = loginBody("""{"code":0,"maskedPhone":"+972*****99"}"""))
                }
                url == "https://account.xiaomi.com/identity/auth/sendPhoneTicket" ->
                    resp(body = loginBody("""{"code":0}"""))
                url.startsWith("https://account.xiaomi.com/identity/auth/verifyPhone?_flag=4&ticket=123456") ->
                    resp(body = loginBody("""{"location":"https://sts.api.io.mi.com/2fa-hop"}"""))
                url == "https://sts.api.io.mi.com/2fa-hop" -> resp(
                    headers = listOf(
                        "Set-Cookie" to "userId=100002; Path=/",
                        "Set-Cookie" to "passToken=PT2FA; Path=/",
                        "Set-Cookie" to "serviceToken=ST2FA; Path=/",
                        "Extension-Pragma" to """{"ssecurity":"$SSECURITY_B64"}""",
                    ),
                )
                else -> error("unexpected url: $url")
            }
        }

        val result = MiCloud(http, region = "de").login("parent@example.com", "hunter2")
        val twoFactor = result as LoginResult.TwoFactor
        assertEquals("phone", twoFactor.channel)
        assertEquals("+972*****99", twoFactor.maskedTarget)

        val after = twoFactor.submit("123456") as LoginResult.Ok
        assertEquals("100002", after.session.userId)
        assertEquals("ST2FA", after.session.serviceToken)
        // PROTO-4: ssecurity arrived via Extension-Pragma on the redirect hop.
        assertEquals(SSECURITY_B64, after.session.ssecurity.toBase64())
    }

    @Test
    fun `AUTH-9 a rejected two-factor code reads as words, never raw JSON`() = runTest {
        val http = FakeMiHttp()
        http.handler = { req ->
            val url = req.url
            when {
                url.startsWith("https://account.xiaomi.com/pass/serviceLogin?") ->
                    resp(body = serviceLoginContext())
                url.startsWith("https://account.xiaomi.com/pass/serviceLoginAuth2") ->
                    resp(body = loginBody("""{"notificationUrl":"https://account.xiaomi.com/fe/service/identity/authStart?context=CTX"}"""))
                url == "https://account.xiaomi.com/identity/list?context=CTX" -> resp(
                    body = loginBody("""{"code":0,"flag":4}"""),
                    headers = listOf("Set-Cookie" to "identity_session=IS1; Path=/"),
                )
                url.startsWith("https://account.xiaomi.com/identity/auth/verifyPhone?_flag=4&_json=true") ->
                    resp(body = loginBody("""{"code":0,"maskedPhone":"+972*****99"}"""))
                url == "https://account.xiaomi.com/identity/auth/sendPhoneTicket" ->
                    resp(body = loginBody("""{"code":0}"""))
                url.startsWith("https://account.xiaomi.com/identity/auth/verifyPhone?_flag=4&ticket=000000") ->
                    resp(body = loginBody("""{"code":70014,"desc":"invalid ticket"}""")) // wrong code
                else -> error("unexpected url: $url")
            }
        }

        val twoFactor = MiCloud(http, region = "de").login("parent@example.com", "hunter2") as LoginResult.TwoFactor
        val err = assertThrows(XiaomiException::class.java) {
            kotlinx.coroutines.runBlocking { twoFactor.submit("000000") }
        }
        assertEquals("That code wasn't accepted — check it and try again, or start over to get a new code.", err.message)
        assertFalse(err.message!!.contains("{"))
    }

    // ---- signed requests ------------------------------------------------------

    private fun session(ssecurityB64: String = SSECURITY_B64) = Session(
        userId = "U1",
        cUserId = "CU1",
        passToken = "PT1",
        serviceToken = "STOLD",
        ssecurity = ssecurityB64.base64ToBytes(),
        region = "de",
    )

    /** RC4-encrypt a result payload the way the gateway would, given the request's nonce. */
    private fun encryptResponse(req: FakeMiHttp.Req, ssecurity: ByteArray, plaintext: String): ByteArray {
        val nonce = parseForm(req.body!!)["_nonce"]!!.base64ToBytes()
        val signedNonce = Crypto.genSignedNonce(ssecurity, nonce)
        return Crypto.rc4(signedNonce, plaintext.toByteArray()).toBase64().toByteArray()
    }

    @Test
    fun `PROTO-9+11 device list uses exactly the session cookies and maps localip to ip`() = runTest {
        val http = FakeMiHttp()
        val ssecurity = SSECURITY_B64.base64ToBytes()
        http.handler = { req ->
            assertEquals("https://de.api.io.mi.com/app/v2/home/device_list_page", req.url)
            assertEquals("userId=U1; serviceToken=STOLD; cUserId=CU1", req.headers["Cookie"])
            val result = """{"code":0,"result":{"list":[
                {"did":"d1","name":"Nursery","model":"chuangmi.camera.077ac1","mac":"AA:BB","localip":"192.168.1.50"},
                {"did":"d2","name":"Lamp","model":"yeelink.light.lamp4","mac":"CC:DD","localip":"192.168.1.60"}
            ]}}"""
            resp(body = encryptResponse(req, ssecurity, result))
        }

        val cloud = MiCloud(http, session = session())
        val devices = cloud.deviceList()
        assertEquals(2, devices.size)
        assertEquals(Device("d1", "Nursery", "chuangmi.camera.077ac1", "AA:BB", "192.168.1.50"), devices[0])
        // CAM-1: the picker filter keeps only cameras.
        assertEquals(listOf("d1"), devices.filter { isCamera(it.model) }.map { it.did })
    }

    @Test
    fun `AUTH-7+PROTO-10 an expired session refreshes once via passToken and re-persists`() = runTest {
        val http = FakeMiHttp()
        val newSsecurityB64 = "ICEiIyQlJicoKSorLC0uLw==" // 202122...2f
        var apiCalls = 0
        http.handler = { req ->
            val url = req.url
            when {
                url.endsWith("/v2/home/device_list_page") -> {
                    apiCalls++
                    if (apiCalls == 1) {
                        resp(status = 401, body = "Unauthorized".toByteArray())
                    } else {
                        // PROTO-5: the retry must pair the NEW serviceToken with the NEW ssecurity.
                        assertTrue(req.headers["Cookie"]!!.contains("serviceToken=STNEW"))
                        resp(
                            body = encryptResponse(
                                req,
                                newSsecurityB64.base64ToBytes(),
                                """{"code":0,"result":{"list":[]}}""",
                            ),
                        )
                    }
                }
                url.startsWith("https://account.xiaomi.com/pass/serviceLogin?") -> {
                    assertEquals("userId=U1; passToken=PT1", req.headers["Cookie"])
                    resp(
                        body = loginBody(
                            """{"ssecurity":"$newSsecurityB64","passToken":"PT2","location":"https://sts.api.io.mi.com/refresh-hop"}""",
                        ),
                    )
                }
                url == "https://sts.api.io.mi.com/refresh-hop" ->
                    resp(headers = listOf("Set-Cookie" to "serviceToken=STNEW; Path=/"))
                else -> error("unexpected url: $url")
            }
        }

        val cloud = MiCloud(http, session = session())
        var refreshed: Session? = null
        cloud.onSessionRefreshed = { refreshed = it }

        val devices = cloud.deviceList()
        assertEquals(0, devices.size)
        assertEquals(2, apiCalls)
        assertNotNull(refreshed) // AUTH-7: refreshed session handed back for persistence
        assertEquals("STNEW", refreshed!!.serviceToken)
        assertEquals("PT2", refreshed!!.passToken)
        assertEquals(newSsecurityB64, refreshed!!.ssecurity.toBase64())
    }

    @Test
    fun `PROTO-5+AUTH-8 a refresh whose redirect chain dies part-way stays retryable and mixes no tokens`() = runTest {
        // The serviceLogin answer hands out a NEW ssecurity, but the hop that should deliver the
        // matching NEW serviceToken dies. Continuing with new-ssecurity + old-serviceToken would
        // poison the stored session (every signed request rejected until the next refresh).
        val http = FakeMiHttp()
        val newSsecurityB64 = "ICEiIyQlJicoKSorLC0uLw=="
        var apiCalls = 0
        http.handler = { req ->
            val url = req.url
            when {
                url.endsWith("/v2/home/device_list_page") -> {
                    apiCalls++
                    resp(status = 401, body = "Unauthorized".toByteArray())
                }
                url.startsWith("https://account.xiaomi.com/pass/serviceLogin?") -> resp(
                    body = loginBody(
                        """{"ssecurity":"$newSsecurityB64","passToken":"PT2","location":"https://sts.api.io.mi.com/refresh-hop"}""",
                    ),
                )
                url == "https://sts.api.io.mi.com/refresh-hop" -> resp(status = 500) // chain dies here
                else -> error("unexpected url: $url")
            }
        }

        val cloud = MiCloud(http, session = session())
        var refreshed: Session? = null
        cloud.onSessionRefreshed = { refreshed = it }

        val err = assertThrows(Exception::class.java) {
            kotlinx.coroutines.runBlocking { cloud.deviceList() }
        }
        assertFalse("a half-finished refresh must stay retryable", err is AuthExpiredException)
        assertNull("a mixed session must never be persisted", refreshed)
        assertEquals("the request must not be retried with mixed tokens", 1, apiCalls)
    }

    @Test
    fun `APP-3 non-auth server failures surface path and status`() = runTest {
        val http = FakeMiHttp()
        http.handler = { resp(status = 500, body = "boom".toByteArray()) }
        val cloud = MiCloud(http, session = session())
        val err = assertThrows(XiaomiException::class.java) {
            kotlinx.coroutines.runBlocking { cloud.request("/v2/home/device_list_page", "{}") }
        }
        assertTrue(err.message!!.contains("500"))
        assertTrue(err.message!!.contains("/v2/home/device_list_page"))
    }

    @Test
    fun `PROTO-24 miotGetProp reads a property value`() = runTest {
        val http = FakeMiHttp()
        val ssecurity = SSECURITY_B64.base64ToBytes()
        http.handler = { req ->
            assertEquals("https://de.api.io.mi.com/app/miotspec/prop/get", req.url)
            val form = parseForm(req.body!!)
            val nonce = form["_nonce"]!!.base64ToBytes()
            val signedNonce = Crypto.genSignedNonce(ssecurity, nonce)
            val data = JSONObject(String(Crypto.rc4(signedNonce, form["data"]!!.base64ToBytes())))
            val p = data.getJSONArray("params").getJSONObject(0)
            assertEquals("cam-did-1", p.getString("did"))
            assertEquals(2, p.getInt("siid"))
            assertEquals(3, p.getInt("piid"))
            val result = """{"code":0,"result":[{"did":"cam-did-1","siid":2,"piid":3,"code":0,"value":2}]}"""
            resp(body = encryptResponse(req, ssecurity, result))
        }
        val v = MiCloud(http, session = session()).miotGetProp("cam-did-1", 2, 3)
        assertEquals(2, (v as Number).toInt())
        assertEquals(NightVisionMode.AUTO, NightVisionMode.fromValue(v.toInt()))
    }

    @Test
    fun `PROTO-24 miotSetProp writes the value and reports item-level failures`() = runTest {
        val http = FakeMiHttp()
        val ssecurity = SSECURITY_B64.base64ToBytes()
        var lastValue = -1
        http.handler = { req ->
            assertEquals("https://de.api.io.mi.com/app/miotspec/prop/set", req.url)
            val form = parseForm(req.body!!)
            val nonce = form["_nonce"]!!.base64ToBytes()
            val signedNonce = Crypto.genSignedNonce(ssecurity, nonce)
            val data = JSONObject(String(Crypto.rc4(signedNonce, form["data"]!!.base64ToBytes())))
            lastValue = data.getJSONArray("params").getJSONObject(0).getInt("value")
            resp(body = encryptResponse(req, ssecurity, """{"code":0,"result":[{"code":0}]}"""))
        }
        // ON = value 0 on the wire (PROTO-24).
        MiCloud(http, session = session()).miotSetProp("cam-did-1", 2, 3, NightVisionMode.ON.value)
        assertEquals(0, lastValue)
    }

    @Test
    fun `PROTO-24 miotSetProp surfaces an item-level error code`() = runTest {
        val http = FakeMiHttp()
        val ssecurity = SSECURITY_B64.base64ToBytes()
        http.handler = { req ->
            resp(body = encryptResponse(req, ssecurity, """{"code":0,"result":[{"code":-704002}]}"""))
        }
        val err = assertThrows(XiaomiException::class.java) {
            kotlinx.coroutines.runBlocking {
                MiCloud(http, session = session()).miotSetProp("cam-did-1", 2, 3, 1)
            }
        }
        assertTrue(err.message!!.contains("-704002"))
    }

    @Test
    fun `PROTO-12 miss_get_vendor sends our public key and maps the vendor response`() = runTest {
        val http = FakeMiHttp()
        val ssecurity = SSECURITY_B64.base64ToBytes()
        val appKey = ByteArray(32) { 7 }
        http.handler = { req ->
            assertEquals("https://de.api.io.mi.com/app/v2/device/miss_get_vendor", req.url)
            // Decrypt the data param and check what we actually asked for.
            val form = parseForm(req.body!!)
            val nonce = form["_nonce"]!!.base64ToBytes()
            val signedNonce = Crypto.genSignedNonce(ssecurity, nonce)
            val data = JSONObject(String(Crypto.rc4(signedNonce, form["data"]!!.base64ToBytes())))
            assertEquals(appKey.toHex(), data.getString("app_pubkey"))
            assertEquals("cam-did-1", data.getString("did"))
            assertEquals("TUTK_CS2_MTP", data.getString("support_vendors"))

            val result = """{"code":0,"result":{
                "vendor":{"vendor":4,"vendor_params":{"p2p_id":"ABC-123"}},
                "public_key":"deadbeef","sign":"SIGN-TOKEN"
            }}"""
            resp(body = encryptResponse(req, ssecurity, result))
        }

        val vendor = MiCloud(http, session = session()).missGetVendor("cam-did-1", appKey)
        assertEquals(4, vendor.vendor)
        assertEquals("cs2", vendorName(vendor.vendor))
        assertEquals("ABC-123", vendor.uid)
        assertEquals("deadbeef", vendor.devicePublicHex)
        assertEquals("SIGN-TOKEN", vendor.sign)
    }
}
