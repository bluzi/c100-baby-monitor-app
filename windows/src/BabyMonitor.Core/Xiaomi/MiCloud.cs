using System.Text;
using System.Text.RegularExpressions;
using BabyMonitor.Core.Json;
using BabyMonitor.Core.Logging;
using BabyMonitor.Core.Net;

namespace BabyMonitor.Core.Xiaomi;

/// <summary>
/// Mi Cloud: sign-in, the signed API, and the key exchange that unlocks the camera's stream.
///
/// Port of the Kotlin core's MiCloud.kt. Our <see cref="IMiHttp"/> has no cookie jar and no
/// auto-redirects, so what we send is exactly what we signed.
/// </summary>
public sealed class MiCloud
{
    private const string Sid = "xiaomiio";
    private const string AccountBase = "https://account.xiaomi.com";
    private const string LoginBodyPrefix = "&&&START&&&";

    /// <summary>Xiaomi's "wrong account or password" (AUTH-9).</summary>
    private const int WrongCredentials = 70016;

    private static readonly Regex AuthShaped =
        new("401|403|auth|token|invalid|expired|login", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HttpFourHundred = new(@"xiaomi: 4\d\d", RegexOptions.Compiled);

    private readonly IMiHttp _http;
    private readonly Func<byte[]> _nonceSource;

    private byte[]? _ssecurity;
    private string? _passToken;
    private string? _userId;
    private string? _cUserId;
    private string? _serviceToken;
    private AuthState? _auth;

    public MiCloud(
        IMiHttp http,
        string region = "sg", // matches the sign-in screen's default
        Session? session = null,
        Func<byte[]>? nonceSource = null)
    {
        _http = http;
        Region = region;
        _nonceSource = nonceSource ?? (() => Crypto.GenNonce());

        if (session != null)
        {
            _ssecurity = session.Ssecurity;
            _passToken = session.PassToken;
            _userId = session.UserId;
            _cUserId = session.CUserId;
            _serviceToken = session.ServiceToken;
            Region = session.Region;
        }
    }

    public string Region { get; private set; }

    public Func<Session, Task>? OnSessionRefreshed { get; set; }

    public static string GetApiBase(string region) => region switch
    {
        "cn" => "https://api.io.mi.com/app",
        "de" or "i2" or "ru" or "sg" or "us" => $"https://{region}.api.io.mi.com/app",
        _ => "https://api.io.mi.com/app",
    };

    /// <summary>PROTO-2: login endpoints prefix their JSON with &amp;&amp;&amp;START&amp;&amp;&amp;.</summary>
    public static JsonObj ReadLoginBody(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        if (!text.StartsWith(LoginBodyPrefix, StringComparison.Ordinal))
        {
            throw new XiaomiException($"xiaomi: unexpected login response: {Truncate(text, 200)}");
        }

        return new JsonObj(text[LoginBodyPrefix.Length..]);
    }

    public static KeyValuePair<string, string>? ParseSetCookie(string setCookie)
    {
        var pair = setCookie.Split(';')[0];
        var eq = pair.IndexOf('=', StringComparison.Ordinal);
        if (eq == -1)
        {
            return null;
        }

        return new KeyValuePair<string, string>(pair[..eq].Trim(), pair[(eq + 1)..].Trim());
    }

    public static string FormEncode(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join("&", pairs.Select(p => $"{Urls.UrlEncode(p.Key)}={Urls.UrlEncode(p.Value)}"));

    /// <summary>PROTO-7/8: the full signed+encrypted request form, deterministic given the nonce.</summary>
    public static (string Form, byte[] SignedNonce) BuildSignedForm(
        string path,
        string data,
        byte[] ssecurity,
        byte[] nonce)
    {
        var signedNonce = Crypto.GenSignedNonce(ssecurity, nonce);
        var rc4Hash = Crypto.GenSignatureB64("POST", path, data, null, signedNonce);
        var encData = Crypto.Rc4(signedNonce, Encoding.UTF8.GetBytes(data)).ToBase64();
        var encHash = Crypto.Rc4(signedNonce, Encoding.UTF8.GetBytes(rc4Hash)).ToBase64();
        var signature = Crypto.GenSignatureB64("POST", path, encData, encHash, signedNonce);
        var form = FormEncode(new[]
        {
            new KeyValuePair<string, string>("data", encData),
            new KeyValuePair<string, string>("rc4_hash__", encHash),
            new KeyValuePair<string, string>("signature", signature),
            new KeyValuePair<string, string>("_nonce", nonce.ToBase64()),
        });
        return (form, signedNonce);
    }

    public Session GetSession()
    {
        var missing = new List<string>();
        if (_userId == null)
        {
            missing.Add("userId");
        }

        if (_serviceToken == null)
        {
            missing.Add("serviceToken");
        }

        if (_ssecurity == null)
        {
            missing.Add("ssecurity");
        }

        if (_passToken == null)
        {
            missing.Add("passToken");
        }

        if (missing.Count > 0)
        {
            throw new XiaomiException($"xiaomi: login incomplete, missing: {string.Join(", ", missing)}");
        }

        return new Session(_userId!, _cUserId ?? string.Empty, _passToken!, _serviceToken!, _ssecurity!, Region);
    }

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        Log.I("login", $"login start: user={MaskUser(username)} region={Region}");
        _ssecurity = null;
        _passToken = null;
        _userId = null;
        _cUserId = null;
        _serviceToken = null;
        _auth = new AuthState(username, password);
        try
        {
            return await DoLoginAuth2Async(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.E("login", "login failed", e);
            throw;
        }
    }

    /// <summary>PROTO-5: re-authenticate from the stored long-lived passToken.</summary>
    public async Task LoginWithTokenAsync(string userId, string passToken, CancellationToken ct = default)
    {
        Log.I("login", $"refreshing session via passToken for userId={userId}");
        var res = await _http.RequestAsync(
            $"{AccountBase}/pass/serviceLogin?_json=true&sid={Sid}",
            headers: CookieHeader(new Dictionary<string, string>
            {
                ["userId"] = userId,
                ["passToken"] = passToken,
            }),
            ct: ct).ConfigureAwait(false);

        var v1 = ReadLoginBody(res.Body);
        var location = v1.OptString("location");

        // BG-8: Xiaomi answered in its own format and gave us nowhere to go — the long-lived token is
        // dead and only a fresh sign-in helps. This is the ONLY thing that counts as an expiry: a
        // garbled answer (captive portal, maintenance page) or no answer at all must stay retryable,
        // because declaring expiry throws away a session that may be perfectly good.
        if (location.Length == 0)
        {
            throw new AuthExpiredException("Your session expired — please sign in again.");
        }

        // PROTO-5: a new ssecurity is always paired with a NEW serviceToken. Drop the stored pair
        // before harvesting, so a redirect chain that dies part-way can never leave a fresh ssecurity
        // married to the stale serviceToken — the gateway rejects that mix on every signed request,
        // and persisting it would poison the stored session too.
        _ssecurity = null;
        _serviceToken = null;
        var fresh = v1.OptString("ssecurity");
        if (fresh.Length > 0)
        {
            _ssecurity = fresh.Base64ToBytes();
        }

        var freshPassToken = v1.OptString("passToken");
        if (freshPassToken.Length > 0)
        {
            _passToken = freshPassToken;
        }

        _userId = userId;
        await FinishAuthAsync(location, ct).ConfigureAwait(false);

        // A chain that ended early (a dead hop) is an incomplete refresh: throw here so the caller
        // treats it as temporary (AUTH-8) instead of continuing with half a session.
        GetSession();
    }

    /// <summary>PROTO-10: authed request with a one-shot passToken refresh on auth-shaped failures.</summary>
    public async Task<object> RequestAsync(string apiPath, string parameters, CancellationToken ct = default)
    {
        try
        {
            return await DoRequestAsync(apiPath, parameters, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // a cancelled request is not an auth failure to be "recovered" from
        }
        catch (Exception err)
        {
            var pt = _passToken;
            var uid = _userId;
            if (pt == null || uid == null)
            {
                throw;
            }

            var msg = err.Message;
            if (!AuthShaped.IsMatch(msg) && !HttpFourHundred.IsMatch(msg))
            {
                throw;
            }

            Log.W("cloud", $"auth-shaped failure on {apiPath}, refreshing session once: {msg}");
            try
            {
                await LoginWithTokenAsync(uid, pt, ct).ConfigureAwait(false);
            }
            catch (AuthExpiredException)
            {
                // Xiaomi refused the long-lived token — the user really must sign in again (BG-8).
                Log.W("cloud", "session refresh refused — session expired");
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception refresh)
            {
                // We could not get a straight answer (Wi-Fi blip, captive portal, Xiaomi outage, too
                // many redirects). The token may be fine, so keep retrying rather than sign the parent
                // out in the middle of the night — an expiry deletes their stored session.
                Log.W("cloud", $"session refresh could not complete — staying retryable: {refresh.Message}");
                throw err;
            }

            if (OnSessionRefreshed != null)
            {
                await OnSessionRefreshed(GetSession()).ConfigureAwait(false);
            }

            return await DoRequestAsync(apiPath, parameters, ct).ConfigureAwait(false);
        }
    }

    /// <summary>PROTO-11: list devices; callers filter cameras via IsCamera(model).</summary>
    public async Task<IReadOnlyList<Device>> DeviceListAsync(CancellationToken ct = default)
    {
        var result = (JsonObj)await RequestAsync("/v2/home/device_list_page", "{}", ct).ConfigureAwait(false);
        var list = result.OptJsonArray("list") ?? new JsonArr();
        var devices = new List<Device>();
        for (var i = 0; i < list.Length; i++)
        {
            var d = list.GetJsonObject(i);
            devices.Add(new Device(
                Did: d.OptString("did"),
                Name: d.OptString("name"),
                Model: d.OptString("model"),
                Mac: d.OptString("mac"),
                Ip: d.OptString("localip")));
        }

        Log.I("cloud", $"deviceList: {devices.Count} devices, {devices.Count(d => Mi.IsCamera(d.Model))} cameras");
        foreach (var d in devices)
        {
            Log.D("cloud", $"  device did={d.Did} model={d.Model} ip={d.Ip} name={d.Name}");
        }

        return devices;
    }

    /// <summary>PROTO-24: read one MiOT property. Returns its value, or null when the item has none.</summary>
    public async Task<object?> MiotGetPropAsync(string did, int siid, int piid, CancellationToken ct = default)
    {
        var parameters = new JsonObj()
            .Put("params", new JsonArr().Put(new JsonObj().Put("did", did).Put("siid", siid).Put("piid", piid)))
            .ToString();

        if (await RequestAsync("/miotspec/prop/get", parameters, ct).ConfigureAwait(false) is not JsonArr res)
        {
            throw new XiaomiException("miot: unexpected get response");
        }

        var first = res.OptJsonObject(0) ?? throw new XiaomiException($"miot: empty response for {siid}/{piid}");
        if (!first.IsNull("code") && first.OptInt("code") != 0)
        {
            throw new XiaomiException($"miot: get {siid}/{piid} failed (code {first.OptInt("code")})");
        }

        var value = first.IsNull("value") ? null : first.Get("value");
        Log.I("cloud", $"miotGet {siid}/{piid} = {value}");
        return value;
    }

    /// <summary>PROTO-24: write one MiOT property.</summary>
    public async Task MiotSetPropAsync(string did, int siid, int piid, object value, CancellationToken ct = default)
    {
        var parameters = new JsonObj()
            .Put(
                "params",
                new JsonArr().Put(
                    new JsonObj().Put("did", did).Put("siid", siid).Put("piid", piid).Put("value", value)))
            .ToString();

        if (await RequestAsync("/miotspec/prop/set", parameters, ct).ConfigureAwait(false) is not JsonArr res)
        {
            throw new XiaomiException("miot: unexpected set response");
        }

        var first = res.OptJsonObject(0);
        if (first != null && !first.IsNull("code") && first.OptInt("code") != 0)
        {
            throw new XiaomiException($"miot: set {siid}/{piid} failed (code {first.OptInt("code")})");
        }

        Log.I("cloud", $"miotSet {siid}/{piid} = {value} ok");
    }

    /// <summary>PROTO-12: exchange NaCl public keys with the camera through the cloud.</summary>
    public async Task<MissVendor> MissGetVendorAsync(string did, byte[] appPubkey, CancellationToken ct = default)
    {
        var parameters = new JsonObj()
            .Put("app_pubkey", appPubkey.ToHex())
            .Put("did", did)
            .Put("support_vendors", "TUTK_CS2_MTP")
            .ToString();

        var res = (JsonObj)await RequestAsync("/v2/device/miss_get_vendor", parameters, ct).ConfigureAwait(false);
        var vendor = res.OptJsonObject("vendor");
        var result = new MissVendor(
            Vendor: vendor?.OptInt("vendor") ?? 0,
            Uid: vendor?.OptJsonObject("vendor_params")?.OptString("p2p_id"),
            DevicePublicHex: res.OptString("public_key"),
            Sign: res.OptString("sign"));

        Log.I(
            "cloud",
            $"missGetVendor did={did}: vendor={result.Vendor} ({Mi.VendorName(result.Vendor)}) " +
            $"uid={result.Uid} devicePubKey={Truncate(result.DevicePublicHex, 12)}…");
        return result;
    }

    private static IReadOnlyDictionary<string, string> CookieHeader(IReadOnlyDictionary<string, string> cookies) =>
        cookies.Count == 0
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>
            {
                ["Cookie"] = string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")),
            };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private static string MaskUser(string user) =>
        user.Length <= 3 ? "***" : $"{user[..2]}***{user[^2..]}";

    private async Task<LoginResult> DoLoginAuth2Async(CancellationToken ct)
    {
        var auth = _auth ?? throw new XiaomiException("no auth state");

        // Step 1: serviceLogin hands us the signing context for this attempt.
        var res1 = await _http.RequestAsync($"{AccountBase}/pass/serviceLogin?_json=true&sid={Sid}", ct: ct)
            .ConfigureAwait(false);
        var v1 = ReadLoginBody(res1.Body);

        // Step 2: serviceLoginAuth2 with the hashed password (PROTO-1/3).
        var form = new List<KeyValuePair<string, string>>
        {
            new("_json", "true"),
            new("hash", Crypto.PasswordHash(auth.Password)),
            new("sid", v1.OptString("sid", Sid)),
            new("callback", v1.OptString("callback")),
            new("_sign", v1.OptString("_sign")),
            new("qs", v1.OptString("qs")),
            new("user", auth.Username),
        };

        var cookies = new Dictionary<string, string>();
        if (auth.CaptchaCode != null && auth.Ick != null)
        {
            form.Add(new KeyValuePair<string, string>("captCode", auth.CaptchaCode));
            cookies["ick"] = auth.Ick;
        }
        else
        {
            cookies["deviceId"] = Crypto.RandString(16);
        }

        var res2 = await _http.RequestAsync(
            $"{AccountBase}/pass/serviceLoginAuth2",
            method: "POST",
            headers: CookieHeader(cookies),
            body: FormEncode(form),
            ct: ct).ConfigureAwait(false);
        var v2 = ReadLoginBody(res2.Body);

        var captchaUrl = v2.OptString("captchaURL");
        if (captchaUrl.Length > 0)
        {
            Log.I("login", "captcha required");
            return await HandleCaptchaAsync(captchaUrl, ct).ConfigureAwait(false);
        }

        var notificationUrl = v2.OptString("notificationUrl");
        if (notificationUrl.Length > 0)
        {
            Log.I("login", "two-factor required");
            return await Handle2FaAsync(notificationUrl, ct).ConfigureAwait(false);
        }

        var location = v2.OptString("location");
        if (location.Length == 0)
        {
            // Body may carry a Xiaomi error code/description — log it (no secrets in it).
            Log.W("login", $"auth2 returned no location: code={v2.Opt("code")} desc={v2.OptString("desc")}");
            throw new XiaomiException(LoginFailureMessage(v2));
        }

        var ssecurity = v2.OptString("ssecurity");
        if (ssecurity.Length > 0)
        {
            _ssecurity = ssecurity.Base64ToBytes();
        }

        var passToken = v2.OptString("passToken");
        if (passToken.Length > 0)
        {
            _passToken = passToken;
        }

        await FinishAuthAsync(location, ct).ConfigureAwait(false);
        await RecoverSsecurityIfMissingAsync(ct).ConfigureAwait(false);
        var session = GetSession();
        _auth = null;
        Log.I("login", $"login ok: userId={session.UserId} ssecurity={session.Ssecurity.Length}B region={Region}");
        return new LoginResult.Ok(session);
    }

    /// <summary>AUTH-9: a failed sign-in must read as words — never as the gateway's raw JSON.</summary>
    private static string LoginFailureMessage(JsonObj v2)
    {
        int? code = v2.IsNull("code") ? null : v2.OptInt("code", int.MinValue);
        if (code == int.MinValue)
        {
            code = null;
        }

        if (code == WrongCredentials)
        {
            return "Wrong account or password.";
        }

        var desc = v2.OptString("desc");
        return (desc.Length > 0, code) switch
        {
            (true, not null) => $"Sign-in failed: {desc} (code {code})",
            (true, null) => $"Sign-in failed: {desc}",
            (false, not null) => $"Sign-in failed (code {code})",
            _ => "Sign-in failed — Xiaomi returned an unexpected response.",
        };
    }

    private async Task<LoginResult> HandleCaptchaAsync(string captchaUrl, CancellationToken ct)
    {
        var auth = _auth ?? throw new XiaomiException("no auth state");
        var res = await _http.RequestAsync($"{AccountBase}{captchaUrl}", ct: ct).ConfigureAwait(false);
        foreach (var sc in res.SetCookies())
        {
            var cookie = ParseSetCookie(sc);
            if (cookie?.Key == "ick")
            {
                auth.Ick = cookie.Value.Value;
            }
        }

        return new LoginResult.Captcha(
            Image: res.Body,
            ContentType: res.Header("content-type") ?? "image/jpeg",
            Submit: async code =>
            {
                var a = _auth ?? throw new XiaomiException("captcha state lost");
                a.CaptchaCode = code;
                return a.Flag != null
                    ? await SendTicketAsync(ct).ConfigureAwait(false)
                    : await DoLoginAuth2Async(ct).ConfigureAwait(false);
            });
    }

    private async Task<LoginResult> Handle2FaAsync(string notificationUrl, CancellationToken ct)
    {
        var auth = _auth ?? throw new XiaomiException("no auth state");
        var listUrl = notificationUrl.Replace(
            "/fe/service/identity/authStart",
            "/identity/list",
            StringComparison.Ordinal);
        var res = await _http.RequestAsync(listUrl, ct: ct).ConfigureAwait(false);
        var body = ReadLoginBody(res.Body);
        auth.Flag = body.OptInt("flag").ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var sc in res.SetCookies())
        {
            var cookie = ParseSetCookie(sc);
            if (cookie?.Key == "identity_session")
            {
                auth.IdentitySession = cookie.Value.Value;
            }
        }

        return await SendTicketAsync(ct).ConfigureAwait(false);
    }

    private string VerifyName() => _auth?.Flag switch
    {
        "4" => "Phone",
        "8" => "Email",
        _ => string.Empty,
    };

    private async Task<LoginResult> SendTicketAsync(CancellationToken ct)
    {
        var auth = _auth ?? throw new XiaomiException("no auth state");
        var name = VerifyName();
        var cookies = new Dictionary<string, string>();
        if (auth.IdentitySession != null)
        {
            cookies["identity_session"] = auth.IdentitySession;
        }

        // Discover the masked phone/email the code goes to.
        var verifyRes = await _http.RequestAsync(
            $"{AccountBase}/identity/auth/verify{name}?_flag={auth.Flag}&_json=true",
            headers: CookieHeader(cookies),
            ct: ct).ConfigureAwait(false);
        var v1 = ReadLoginBody(verifyRes.Body);

        // Ask Xiaomi to text/email the code.
        var cookies2 = new Dictionary<string, string>(cookies);
        if (auth.CaptchaCode != null && auth.Ick != null)
        {
            cookies2["ick"] = auth.Ick;
        }

        var sendRes = await _http.RequestAsync(
            $"{AccountBase}/identity/auth/send{name}Ticket",
            method: "POST",
            headers: CookieHeader(cookies2),
            body: FormEncode(new[]
            {
                new KeyValuePair<string, string>("_json", "true"),
                new KeyValuePair<string, string>("icode", auth.CaptchaCode ?? string.Empty),
                new KeyValuePair<string, string>("retry", "0"),
            }),
            ct: ct).ConfigureAwait(false);

        var v2 = ReadLoginBody(sendRes.Body);
        var captchaUrl = v2.OptString("captchaURL");
        if (captchaUrl.Length > 0)
        {
            return await HandleCaptchaAsync(captchaUrl, ct).ConfigureAwait(false);
        }

        if (v2.OptInt("code", -1) != 0)
        {
            Log.W("login", $"sendTicket failed: code={v2.Opt("code")} desc={v2.OptString("desc")}");
            throw new XiaomiException(LoginFailureMessage(v2)); // AUTH-9: words, never raw JSON
        }

        var masked = v1.OptString("maskedPhone");
        if (masked.Length == 0)
        {
            masked = v1.OptString("maskedEmail");
        }

        return new LoginResult.TwoFactor(
            Channel: name == "Phone" ? "phone" : "email",
            MaskedTarget: masked,
            Submit: ticket => LoginWithVerifyAsync(ticket, ct));
    }

    private async Task<LoginResult> LoginWithVerifyAsync(string ticket, CancellationToken ct)
    {
        var auth = _auth ?? throw new XiaomiException("wrong login step");
        if (auth.Flag == null)
        {
            throw new XiaomiException("wrong login step");
        }

        var name = VerifyName();
        Log.I("login", $"submitting 2FA ticket (channel={name})");
        var cookies = new Dictionary<string, string>();
        if (auth.IdentitySession != null)
        {
            cookies["identity_session"] = auth.IdentitySession;
        }

        var qs = $"_flag={auth.Flag}&ticket={Urls.UrlEncode(ticket)}&trust=false&_json=true";
        var res = await _http.RequestAsync(
            $"{AccountBase}/identity/auth/verify{name}?{qs}",
            method: "POST",
            headers: CookieHeader(cookies),
            body: string.Empty,
            ct: ct).ConfigureAwait(false);

        var v1 = ReadLoginBody(res.Body);
        var location = v1.OptString("location");
        if (location.Length == 0)
        {
            // AUTH-9: the overwhelmingly likely cause is a wrong or expired code — say that.
            Log.W("login", $"2FA verify returned no location: code={v1.Opt("code")}");
            throw new XiaomiException(
                "That code wasn't accepted — check it and try again, or start over to get a new code.");
        }

        await FinishAuthAsync(location, ct).ConfigureAwait(false);
        await RecoverSsecurityIfMissingAsync(ct).ConfigureAwait(false);
        // Only clear auth state once the session is complete, so a partial failure can retry.
        var session = GetSession();
        _auth = null;
        Log.I("login", $"2FA login ok: userId={session.UserId}");
        return new LoginResult.Ok(session);
    }

    /// <summary>
    /// PROTO-5 safety net: if the redirect walk didn't surface ssecurity, re-hit serviceLogin with
    /// only userId+passToken — it returns ssecurity directly, paired with a NEW serviceToken from its
    /// own location chain (never keep the old one).
    /// </summary>
    private async Task RecoverSsecurityIfMissingAsync(CancellationToken ct)
    {
        if (_ssecurity != null)
        {
            return;
        }

        var uid = _userId;
        var pt = _passToken;
        if (uid == null || pt == null)
        {
            return;
        }

        var res = await _http.RequestAsync(
            $"{AccountBase}/pass/serviceLogin?_json=true&sid={Sid}",
            headers: CookieHeader(new Dictionary<string, string> { ["userId"] = uid, ["passToken"] = pt }),
            ct: ct).ConfigureAwait(false);

        var v = ReadLoginBody(res.Body);
        var ssecurity = v.OptString("ssecurity");
        if (ssecurity.Length > 0)
        {
            _ssecurity = ssecurity.Base64ToBytes();
        }

        var passToken = v.OptString("passToken");
        if (passToken.Length > 0)
        {
            _passToken = passToken;
        }

        var location = v.OptString("location");
        if (location.Length > 0)
        {
            _serviceToken = null;
            await FinishAuthAsync(location, ct).ConfigureAwait(false);
        }
    }

    /// <summary>PROTO-4: walk the redirect chain manually, harvesting cookies + Extension-Pragma.</summary>
    private async Task FinishAuthAsync(string location, CancellationToken ct)
    {
        var url = location;
        for (var hop = 0; hop < 10; hop++)
        {
            var res = await _http.RequestAsync(url, ct: ct).ConfigureAwait(false);
            CollectAuthArtifacts(res);
            Log.D(
                "login",
                $"finishAuth hop {hop}: status={res.Status} have userId={_userId != null} " +
                $"serviceToken={_serviceToken != null} ssecurity={_ssecurity != null}");

            var loc = res.Header("location");
            if (loc == null || res.Status < 300 || res.Status >= 400)
            {
                return;
            }

            url = Urls.ResolveUrl(url, loc);
        }

        throw new XiaomiException("finishAuth: too many redirects");
    }

    private void CollectAuthArtifacts(RawResponse res)
    {
        foreach (var sc in res.SetCookies())
        {
            var cookie = ParseSetCookie(sc);
            if (cookie == null)
            {
                continue;
            }

            switch (cookie.Value.Key)
            {
                case "userId":
                    _userId = cookie.Value.Value;
                    break;
                case "cUserId":
                    _cUserId = cookie.Value.Value;
                    break;
                case "serviceToken":
                    _serviceToken = cookie.Value.Value;
                    break;
                case "passToken":
                    _passToken = cookie.Value.Value;
                    break;
            }
        }

        var pragma = res.Header("extension-pragma");
        if (pragma == null)
        {
            return;
        }

        try
        {
            var ssecurity = new JsonObj(pragma).OptString("ssecurity");
            if (ssecurity.Length > 0)
            {
                _ssecurity = ssecurity.Base64ToBytes();
            }
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            // A hop whose Extension-Pragma is not the one we are looking for. The next hop may be.
        }
    }

    private async Task<object> DoRequestAsync(string apiPath, string parameters, CancellationToken ct)
    {
        var sec = _ssecurity;
        var uid = _userId;
        var st = _serviceToken;
        if (sec == null || uid == null || st == null)
        {
            throw new XiaomiException("request: not logged in");
        }

        var nonce = _nonceSource();
        var (form, signedNonce) = BuildSignedForm(apiPath, parameters, sec, nonce);

        // PROTO-9: exactly these cookies, and no others.
        var cookies = new Dictionary<string, string> { ["userId"] = uid, ["serviceToken"] = st };
        if (_cUserId != null)
        {
            cookies["cUserId"] = _cUserId;
        }

        Log.D("cloud", $"POST {apiPath}");
        var res = await _http.RequestAsync(
            GetApiBase(Region) + apiPath,
            method: "POST",
            headers: CookieHeader(cookies),
            body: form,
            ct: ct).ConfigureAwait(false);

        if (res.Status != 200)
        {
            var text = Encoding.UTF8.GetString(res.Body);
            Log.W("cloud", $"{apiPath} HTTP {res.Status}: {Truncate(text, 200)}");
            throw new XiaomiException($"xiaomi: {res.Status} on {apiPath} — {Truncate(text, 300)}");
        }

        var decoded = Crypto.Rc4(signedNonce, Encoding.UTF8.GetString(res.Body).Base64ToBytes());
        var json = new JsonObj(Encoding.UTF8.GetString(decoded));
        if (json.OptInt("code", -1) != 0)
        {
            Log.W("cloud", $"{apiPath} code={json.OptInt("code", -1)} message={json.OptString("message")}");
            var message = json.OptString("message");
            throw new XiaomiException($"xiaomi: {(message.Length > 0 ? message : json.ToString())}");
        }

        return json.Get("result");
    }

    private sealed class AuthState
    {
        public AuthState(string username, string password)
        {
            Username = username;
            Password = password;
        }

        public string Username { get; }

        public string Password { get; }

        public string? Ick { get; set; }

        public string? IdentitySession { get; set; }

        public string? Flag { get; set; }

        public string? CaptchaCode { get; set; }
    }
}
