package com.bluzi.babymonitor.xiaomi

import com.bluzi.babymonitor.log.Log
import com.bluzi.babymonitor.net.MiHttp
import com.bluzi.babymonitor.net.RawResponse
import java.net.URI
import java.net.URLEncoder
import org.json.JSONArray
import org.json.JSONObject

// Kotlin port of c100/src/xiaomi/cloud.ts, minus the React Native cookie-jar workarounds —
// our MiHttp has no jar and no auto-redirects, so what we send is exactly what we signed.

private const val SID = "xiaomiio"
private const val ACCOUNT_BASE = "https://account.xiaomi.com"
private const val LOGIN_BODY_PREFIX = "&&&START&&&"

fun getApiBase(region: String): String = when (region) {
    "cn" -> "https://api.io.mi.com/app"
    "de", "i2", "ru", "sg", "us" -> "https://$region.api.io.mi.com/app"
    else -> "https://api.io.mi.com/app"
}

/** PROTO-2: login endpoints prefix their JSON with &&&START&&&. */
fun readLoginBody(body: ByteArray): JSONObject {
    val text = String(body)
    if (!text.startsWith(LOGIN_BODY_PREFIX)) {
        throw XiaomiException("xiaomi: unexpected login response: ${text.take(200)}")
    }
    return JSONObject(text.substring(LOGIN_BODY_PREFIX.length))
}

fun parseSetCookie(setCookie: String): Pair<String, String>? {
    val pair = setCookie.substringBefore(';')
    val eq = pair.indexOf('=')
    if (eq == -1) return null
    return pair.substring(0, eq).trim() to pair.substring(eq + 1).trim()
}

private fun urlEncode(s: String): String = URLEncoder.encode(s, "UTF-8")

fun formEncode(pairs: List<Pair<String, String>>): String =
    pairs.joinToString("&") { (k, v) -> "${urlEncode(k)}=${urlEncode(v)}" }

private fun cookieHeader(cookies: Map<String, String>): Map<String, String> =
    if (cookies.isEmpty()) emptyMap()
    else mapOf("Cookie" to cookies.entries.joinToString("; ") { "${it.key}=${it.value}" })

/** PROTO-7/8: the full signed+encrypted request form, deterministic given the nonce. */
fun buildSignedForm(path: String, data: String, ssecurity: ByteArray, nonce: ByteArray): Pair<String, ByteArray> {
    val signedNonce = Crypto.genSignedNonce(ssecurity, nonce)
    val rc4Hash = Crypto.genSignatureB64("POST", path, data, null, signedNonce)
    val encData = Crypto.rc4(signedNonce, data.toByteArray()).toBase64()
    val encHash = Crypto.rc4(signedNonce, rc4Hash.toByteArray()).toBase64()
    val signature = Crypto.genSignatureB64("POST", path, encData, encHash, signedNonce)
    val form = formEncode(
        listOf(
            "data" to encData,
            "rc4_hash__" to encHash,
            "signature" to signature,
            "_nonce" to nonce.toBase64(),
        ),
    )
    return form to signedNonce
}

private data class AuthState(
    val username: String,
    val password: String,
    var ick: String? = null,
    var identitySession: String? = null,
    var flag: String? = null,
    var captchaCode: String? = null,
)

class MiCloud(
    private val http: MiHttp,
    var region: String = "sg", // matches the sign-in screen's default

    session: Session? = null,
    private val nonceSource: () -> ByteArray = { Crypto.genNonce() },
) {
    private var ssecurity: ByteArray? = null
    private var passToken: String? = null
    private var userId: String? = null
    private var cUserId: String? = null
    private var serviceToken: String? = null
    private var auth: AuthState? = null

    var onSessionRefreshed: (suspend (Session) -> Unit)? = null

    init {
        if (session != null) {
            ssecurity = session.ssecurity
            passToken = session.passToken
            userId = session.userId
            cUserId = session.cUserId
            serviceToken = session.serviceToken
            region = session.region
        }
    }

    fun getSession(): Session {
        val missing = buildList {
            if (userId == null) add("userId")
            if (serviceToken == null) add("serviceToken")
            if (ssecurity == null) add("ssecurity")
            if (passToken == null) add("passToken")
        }
        if (missing.isNotEmpty()) {
            throw XiaomiException("xiaomi: login incomplete, missing: ${missing.joinToString(", ")}")
        }
        return Session(
            userId = userId!!,
            cUserId = cUserId ?: "",
            passToken = passToken!!,
            serviceToken = serviceToken!!,
            ssecurity = ssecurity!!,
            region = region,
        )
    }

    suspend fun login(username: String, password: String): LoginResult {
        Log.i("login", "login start: user=${maskUser(username)} region=$region")
        ssecurity = null
        passToken = null
        userId = null
        cUserId = null
        serviceToken = null
        auth = AuthState(username, password)
        return try {
            doLoginAuth2()
        } catch (e: Exception) {
            Log.e("login", "login failed", e)
            throw e
        }
    }

    private suspend fun doLoginAuth2(): LoginResult {
        val auth = this.auth ?: throw XiaomiException("no auth state")

        // Step 1: serviceLogin hands us the signing context for this attempt.
        val res1 = http.request("$ACCOUNT_BASE/pass/serviceLogin?_json=true&sid=$SID")
        val v1 = readLoginBody(res1.body)

        // Step 2: serviceLoginAuth2 with the hashed password (PROTO-1/3).
        val form = mutableListOf(
            "_json" to "true",
            "hash" to Crypto.passwordHash(auth.password),
            "sid" to v1.optString("sid", SID),
            "callback" to v1.optString("callback"),
            "_sign" to v1.optString("_sign"),
            "qs" to v1.optString("qs"),
            "user" to auth.username,
        )
        val cookies = mutableMapOf<String, String>()
        val captchaCode = auth.captchaCode
        val ick = auth.ick
        if (captchaCode != null && ick != null) {
            form.add("captCode" to captchaCode)
            cookies["ick"] = ick
        } else {
            cookies["deviceId"] = Crypto.randString(16)
        }

        val res2 = http.request(
            "$ACCOUNT_BASE/pass/serviceLoginAuth2",
            method = "POST",
            headers = cookieHeader(cookies),
            body = formEncode(form),
        )
        val v2 = readLoginBody(res2.body)

        v2.optString("captchaURL").takeIf { it.isNotEmpty() }?.let {
            Log.i("login", "captcha required")
            return handleCaptcha(it)
        }
        v2.optString("notificationUrl").takeIf { it.isNotEmpty() }?.let {
            Log.i("login", "two-factor required")
            return handle2FA(it)
        }

        val location = v2.optString("location")
        if (location.isEmpty()) {
            // Body may carry a Xiaomi error code/description — log it (no secrets in it).
            Log.w("login", "auth2 returned no location: code=${v2.opt("code")} desc=${v2.optString("desc")}")
            throw XiaomiException(loginFailureMessage(v2))
        }

        v2.optString("ssecurity").takeIf { it.isNotEmpty() }?.let { ssecurity = it.base64ToBytes() }
        v2.optString("passToken").takeIf { it.isNotEmpty() }?.let { passToken = it }

        finishAuth(location)
        recoverSsecurityIfMissing()
        val session = getSession()
        this.auth = null
        Log.i("login", "login ok: userId=${session.userId} ssecurity=${session.ssecurity.size}B region=$region")
        return LoginResult.Ok(session)
    }

    /** AUTH-9: a failed sign-in must read as words — never as the gateway's raw JSON. */
    private fun loginFailureMessage(v2: JSONObject): String {
        val code = v2.opt("code")
        if (code == 70016) return "Wrong account or password."
        val desc = v2.optString("desc")
        return when {
            desc.isNotEmpty() && code != null -> "Sign-in failed: $desc (code $code)"
            desc.isNotEmpty() -> "Sign-in failed: $desc"
            code != null -> "Sign-in failed (code $code)"
            else -> "Sign-in failed — Xiaomi returned an unexpected response."
        }
    }

    private suspend fun handleCaptcha(captchaURL: String): LoginResult {
        val auth = this.auth ?: throw XiaomiException("no auth state")
        val res = http.request("$ACCOUNT_BASE$captchaURL")
        for (sc in res.setCookies()) {
            parseSetCookie(sc)?.let { (k, v) -> if (k == "ick") auth.ick = v }
        }
        return LoginResult.Captcha(
            image = res.body,
            contentType = res.header("content-type") ?: "image/jpeg",
            submit = { code ->
                val a = this.auth ?: throw XiaomiException("captcha state lost")
                a.captchaCode = code
                if (a.flag != null) sendTicket() else doLoginAuth2()
            },
        )
    }

    private suspend fun handle2FA(notificationURL: String): LoginResult {
        val auth = this.auth ?: throw XiaomiException("no auth state")
        val listURL = notificationURL.replace("/fe/service/identity/authStart", "/identity/list")
        val res = http.request(listURL)
        val body = readLoginBody(res.body)
        auth.flag = body.optInt("flag").toString()
        for (sc in res.setCookies()) {
            parseSetCookie(sc)?.let { (k, v) -> if (k == "identity_session") auth.identitySession = v }
        }
        return sendTicket()
    }

    private fun verifyName(): String = when (auth?.flag) {
        "4" -> "Phone"
        "8" -> "Email"
        else -> ""
    }

    private suspend fun sendTicket(): LoginResult {
        val auth = this.auth ?: throw XiaomiException("no auth state")
        val name = verifyName()
        val cookies = mutableMapOf<String, String>()
        auth.identitySession?.let { cookies["identity_session"] = it }

        // Discover the masked phone/email the code goes to.
        val verifyRes = http.request(
            "$ACCOUNT_BASE/identity/auth/verify$name?_flag=${auth.flag}&_json=true",
            headers = cookieHeader(cookies),
        )
        val v1 = readLoginBody(verifyRes.body)

        // Ask Xiaomi to text/email the code.
        val cookies2 = cookies.toMutableMap()
        if (auth.captchaCode != null && auth.ick != null) cookies2["ick"] = auth.ick!!
        val sendRes = http.request(
            "$ACCOUNT_BASE/identity/auth/send${name}Ticket",
            method = "POST",
            headers = cookieHeader(cookies2),
            body = formEncode(
                listOf("_json" to "true", "icode" to (auth.captchaCode ?: ""), "retry" to "0"),
            ),
        )
        val v2 = readLoginBody(sendRes.body)
        v2.optString("captchaURL").takeIf { it.isNotEmpty() }?.let { return handleCaptcha(it) }
        if (v2.optInt("code", -1) != 0) {
            Log.w("login", "sendTicket failed: code=${v2.opt("code")} desc=${v2.optString("desc")}")
            throw XiaomiException(loginFailureMessage(v2)) // AUTH-9: words, never raw JSON
        }

        return LoginResult.TwoFactor(
            channel = if (name == "Phone") "phone" else "email",
            maskedTarget = v1.optString("maskedPhone").ifEmpty { v1.optString("maskedEmail") },
            submit = { ticket -> loginWithVerify(ticket) },
        )
    }

    private suspend fun loginWithVerify(ticket: String): LoginResult {
        val auth = this.auth ?: throw XiaomiException("wrong login step")
        if (auth.flag == null) throw XiaomiException("wrong login step")
        val name = verifyName()
        Log.i("login", "submitting 2FA ticket (channel=$name)")
        val cookies = mutableMapOf<String, String>()
        auth.identitySession?.let { cookies["identity_session"] = it }

        val qs = "_flag=${auth.flag}&ticket=${urlEncode(ticket)}&trust=false&_json=true"
        val res = http.request(
            "$ACCOUNT_BASE/identity/auth/verify$name?$qs",
            method = "POST",
            headers = cookieHeader(cookies),
            body = "",
        )
        val v1 = readLoginBody(res.body)
        val location = v1.optString("location")
        if (location.isEmpty()) {
            // AUTH-9: the overwhelmingly likely cause is a wrong or expired code — say that.
            Log.w("login", "2FA verify returned no location: code=${v1.opt("code")}")
            throw XiaomiException("That code wasn't accepted — request a new one and try again.")
        }
        finishAuth(location)
        recoverSsecurityIfMissing()
        // Only clear auth state once the session is complete, so a partial failure can retry.
        val session = getSession()
        this.auth = null
        Log.i("login", "2FA login ok: userId=${session.userId}")
        return LoginResult.Ok(session)
    }

    /**
     * PROTO-5 safety net: if the redirect walk didn't surface ssecurity, re-hit serviceLogin
     * with only userId+passToken — it returns ssecurity directly, paired with a NEW
     * serviceToken from its own location chain (never keep the old one).
     */
    private suspend fun recoverSsecurityIfMissing() {
        if (ssecurity != null) return
        val uid = userId ?: return
        val pt = passToken ?: return
        val res = http.request(
            "$ACCOUNT_BASE/pass/serviceLogin?_json=true&sid=$SID",
            headers = cookieHeader(mapOf("userId" to uid, "passToken" to pt)),
        )
        val v = readLoginBody(res.body)
        v.optString("ssecurity").takeIf { it.isNotEmpty() }?.let { ssecurity = it.base64ToBytes() }
        v.optString("passToken").takeIf { it.isNotEmpty() }?.let { passToken = it }
        v.optString("location").takeIf { it.isNotEmpty() }?.let {
            serviceToken = null
            finishAuth(it)
        }
    }

    /** PROTO-4: walk the redirect chain manually, harvesting cookies + Extension-Pragma. */
    private suspend fun finishAuth(location: String) {
        var url = location
        repeat(10) { hop ->
            val res = http.request(url)
            collectAuthArtifacts(res)
            Log.d(
                "login",
                "finishAuth hop $hop: status=${res.status} have userId=${userId != null} " +
                    "serviceToken=${serviceToken != null} ssecurity=${ssecurity != null}",
            )
            val loc = res.header("location")
            if (loc == null || res.status < 300 || res.status >= 400) return
            url = URI(url).resolve(loc).toString()
        }
        throw XiaomiException("finishAuth: too many redirects")
    }

    private fun collectAuthArtifacts(res: RawResponse) {
        for (sc in res.setCookies()) {
            val (k, v) = parseSetCookie(sc) ?: continue
            when (k) {
                "userId" -> userId = v
                "cUserId" -> cUserId = v
                "serviceToken" -> serviceToken = v
                "passToken" -> passToken = v
            }
        }
        res.header("extension-pragma")?.let { ep ->
            try {
                JSONObject(ep).optString("ssecurity").takeIf { it.isNotEmpty() }?.let {
                    ssecurity = it.base64ToBytes()
                }
            } catch (_: Exception) {
            }
        }
    }

    /** PROTO-5: re-authenticate from the stored long-lived passToken. */
    suspend fun loginWithToken(userId: String, passToken: String) {
        Log.i("login", "refreshing session via passToken for userId=$userId")
        val res = http.request(
            "$ACCOUNT_BASE/pass/serviceLogin?_json=true&sid=$SID",
            headers = cookieHeader(mapOf("userId" to userId, "passToken" to passToken)),
        )
        val v1 = readLoginBody(res.body)
        val location = v1.optString("location")
        // BG-8: Xiaomi answered in its own format and gave us nowhere to go — the long-lived token
        // is dead and only a fresh sign-in helps. This is the ONLY thing that counts as an expiry:
        // a garbled answer (captive portal, maintenance page) or no answer at all must stay
        // retryable, because declaring expiry throws away a session that may be perfectly good.
        if (location.isEmpty()) throw AuthExpiredException("Your session expired — please sign in again.")
        v1.optString("ssecurity").takeIf { it.isNotEmpty() }?.let { ssecurity = it.base64ToBytes() }
        v1.optString("passToken").takeIf { it.isNotEmpty() }?.let { this.passToken = it }
        this.userId = userId
        finishAuth(location)
    }

    /** PROTO-10: authed request with a one-shot passToken refresh on auth-shaped failures. */
    suspend fun request(apiPath: String, params: String): Any {
        try {
            return doRequest(apiPath, params)
        } catch (cancel: kotlinx.coroutines.CancellationException) {
            throw cancel // a cancelled request is not an auth failure to be "recovered" from
        } catch (err: Exception) {
            val pt = passToken
            val uid = userId
            if (pt == null || uid == null) throw err
            val msg = err.message ?: err.toString()
            val authShaped = Regex("401|403|auth|token|invalid|expired|login", RegexOption.IGNORE_CASE)
                .containsMatchIn(msg) || Regex("xiaomi: 4\\d\\d").containsMatchIn(msg)
            if (!authShaped) throw err
            Log.w("cloud", "auth-shaped failure on $apiPath, refreshing session once: $msg")
            try {
                loginWithToken(uid, pt)
            } catch (expired: AuthExpiredException) {
                // Xiaomi refused the long-lived token — the user really must sign in again (BG-8).
                Log.w("cloud", "session refresh refused — session expired")
                throw expired
            } catch (cancel: kotlinx.coroutines.CancellationException) {
                throw cancel
            } catch (refresh: Exception) {
                // We could not get a straight answer (Wi-Fi blip, captive portal, Xiaomi outage,
                // too many redirects). The token may be fine, so keep retrying rather than sign the
                // parent out in the middle of the night — an expiry deletes their stored session.
                Log.w("cloud", "session refresh could not complete — staying retryable: ${refresh.message}")
                throw err
            }
            onSessionRefreshed?.invoke(getSession())
            return doRequest(apiPath, params)
        }
    }

    private suspend fun doRequest(apiPath: String, params: String): Any {
        val sec = ssecurity
        val uid = userId
        val st = serviceToken
        if (sec == null || uid == null || st == null) throw XiaomiException("request: not logged in")

        val nonce = nonceSource()
        val (form, signedNonce) = buildSignedForm(apiPath, params, sec, nonce)

        val cookies = mutableMapOf("userId" to uid, "serviceToken" to st)
        cUserId?.let { cookies["cUserId"] = it }

        Log.d("cloud", "POST $apiPath")
        val res = http.request(
            getApiBase(region) + apiPath,
            method = "POST",
            headers = cookieHeader(cookies),
            body = form,
        )
        if (res.status != 200) {
            Log.w("cloud", "$apiPath HTTP ${res.status}: ${String(res.body).take(200)}")
            throw XiaomiException("xiaomi: ${res.status} on $apiPath — ${String(res.body).take(300)}")
        }
        val decoded = Crypto.rc4(signedNonce, String(res.body).base64ToBytes())
        val json = JSONObject(String(decoded))
        if (json.optInt("code", -1) != 0) {
            Log.w("cloud", "$apiPath code=${json.optInt("code", -1)} message=${json.optString("message")}")
            throw XiaomiException("xiaomi: ${json.optString("message", json.toString())}")
        }
        return json.get("result")
    }

    /** PROTO-11: list devices; callers filter cameras via isCamera(model). */
    suspend fun deviceList(): List<Device> {
        val result = request("/v2/home/device_list_page", "{}") as JSONObject
        val list = result.optJSONArray("list") ?: JSONArray()
        val devices = (0 until list.length()).map { i ->
            val d = list.getJSONObject(i)
            Device(
                did = d.optString("did"),
                name = d.optString("name"),
                model = d.optString("model"),
                mac = d.optString("mac"),
                ip = d.optString("localip"),
            )
        }
        Log.i("cloud", "deviceList: ${devices.size} devices, ${devices.count { isCamera(it.model) }} cameras")
        for (d in devices) Log.d("cloud", "  device did=${d.did} model=${d.model} ip=${d.ip} name=${d.name}")
        return devices
    }

    /** PROTO-24: read one MiOT property. Returns its value, or null when the item has none. */
    suspend fun miotGetProp(did: String, siid: Int, piid: Int): Any? {
        val params = JSONObject()
            .put("params", JSONArray().put(JSONObject().put("did", did).put("siid", siid).put("piid", piid)))
            .toString()
        val res = request("/miotspec/prop/get", params) as? JSONArray
            ?: throw XiaomiException("miot: unexpected get response")
        val first = res.optJSONObject(0) ?: throw XiaomiException("miot: empty response for $siid/$piid")
        if (!first.isNull("code") && first.optInt("code") != 0) {
            throw XiaomiException("miot: get $siid/$piid failed (code ${first.optInt("code")})")
        }
        val value = if (first.isNull("value")) null else first.get("value")
        Log.i("cloud", "miotGet $siid/$piid = $value")
        return value
    }

    /** PROTO-24: write one MiOT property. */
    suspend fun miotSetProp(did: String, siid: Int, piid: Int, value: Any) {
        val params = JSONObject()
            .put(
                "params",
                JSONArray().put(
                    JSONObject().put("did", did).put("siid", siid).put("piid", piid).put("value", value),
                ),
            )
            .toString()
        val res = request("/miotspec/prop/set", params) as? JSONArray
            ?: throw XiaomiException("miot: unexpected set response")
        val first = res.optJSONObject(0)
        if (first != null && !first.isNull("code") && first.optInt("code") != 0) {
            throw XiaomiException("miot: set $siid/$piid failed (code ${first.optInt("code")})")
        }
        Log.i("cloud", "miotSet $siid/$piid = $value ok")
    }

    /** PROTO-12: exchange NaCl public keys with the camera through the cloud. */
    suspend fun missGetVendor(did: String, appPubkey: ByteArray): MissVendor {
        val params = JSONObject()
            .put("app_pubkey", appPubkey.toHex())
            .put("did", did)
            .put("support_vendors", "TUTK_CS2_MTP")
            .toString()
        val res = request("/v2/device/miss_get_vendor", params) as JSONObject
        val vendor = res.optJSONObject("vendor")
        val result = MissVendor(
            vendor = vendor?.optInt("vendor") ?: 0,
            uid = vendor?.optJSONObject("vendor_params")?.optString("p2p_id"),
            devicePublicHex = res.optString("public_key"),
            sign = res.optString("sign"),
        )
        Log.i(
            "cloud",
            "missGetVendor did=$did: vendor=${result.vendor} (${vendorName(result.vendor)}) " +
                "uid=${result.uid} devicePubKey=${result.devicePublicHex.take(12)}…",
        )
        return result
    }

    private fun maskUser(user: String): String =
        if (user.length <= 3) "***" else "${user.take(2)}***${user.takeLast(2)}"
}
