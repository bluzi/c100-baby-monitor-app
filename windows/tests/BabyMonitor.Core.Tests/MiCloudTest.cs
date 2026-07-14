using System.Text;
using BabyMonitor.Core.Json;
using BabyMonitor.Core.Net;
using BabyMonitor.Core.Xiaomi;
using Xunit;

namespace BabyMonitor.Core.Tests;

internal sealed class FakeMiHttp : IMiHttp
{
    public sealed record Req(string Url, string Method, IReadOnlyDictionary<string, string> Headers, string? Body);

    public List<Req> Log { get; } = new();

    public Func<Req, RawResponse> Handler { get; set; } =
        r => throw new InvalidOperationException($"no handler for {r.Url}");

    public Task<RawResponse> RequestAsync(
        string url,
        string method = "GET",
        IReadOnlyDictionary<string, string>? headers = null,
        string? body = null,
        CancellationToken ct = default)
    {
        var req = new Req(url, method, headers ?? new Dictionary<string, string>(), body);
        Log.Add(req);
        return Task.FromResult(Handler(req));
    }
}

internal static class Http
{
    internal const string SsecurityB64 = "EBESExQVFhcYGRobHB0eHw==";

    internal static byte[] LoginBody(string json) => Encoding.UTF8.GetBytes("&&&START&&&" + json);

    internal static RawResponse Resp(
        int status = 200,
        byte[]? body = null,
        params (string Name, string Value)[] headers) =>
        new(
            status,
            string.Empty,
            headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList(),
            body ?? Array.Empty<byte>());

    internal static Dictionary<string, string> ParseForm(string body) => body
        .Split('&')
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(kv => UrlDecode(kv[0]), kv => UrlDecode(kv[1]));

    /// <summary>RC4-encrypt a result payload the way the gateway would, given the request's nonce.</summary>
    internal static byte[] EncryptResponse(FakeMiHttp.Req req, byte[] ssecurity, string plaintext)
    {
        var nonce = ParseForm(req.Body!)["_nonce"].Base64ToBytes();
        var signedNonce = Crypto.GenSignedNonce(ssecurity, nonce);
        return Encoding.UTF8.GetBytes(Crypto.Rc4(signedNonce, Encoding.UTF8.GetBytes(plaintext)).ToBase64());
    }

    private static string UrlDecode(string s)
    {
        var bytes = new List<byte>(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            switch (s[i])
            {
                case '+':
                    bytes.Add((byte)' ');
                    i++;
                    break;
                case '%':
                    bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                    i += 3;
                    break;
                default:
                    bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
                    i++;
                    break;
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

public class MiCloudTest
{
    private static byte[] ServiceLoginContext() => Http.LoginBody(
        """{"qs":"%3Fsid%3Dxiaomiio","_sign":"SIGN1","sid":"xiaomiio","callback":"https://sts.api.io.mi.com/sts"}""");

    /// <summary>Wire a happy-path credential login into the fake.</summary>
    private static void ScriptHappyLogin(FakeMiHttp http, Func<FakeMiHttp.Req, RawResponse?>? auth2Extra = null)
    {
        http.Handler = req =>
        {
            var url = req.Url;
            if (url.StartsWith("https://account.xiaomi.com/pass/serviceLogin?", StringComparison.Ordinal))
            {
                return Http.Resp(body: ServiceLoginContext());
            }

            if (url.StartsWith("https://account.xiaomi.com/pass/serviceLoginAuth2", StringComparison.Ordinal))
            {
                return auth2Extra?.Invoke(req) ?? Http.Resp(
                    body: Http.LoginBody(
                        $$"""{"ssecurity":"{{Http.SsecurityB64}}","passToken":"PT1","location":"https://sts.api.io.mi.com/hop1"}"""));
            }

            if (url == "https://sts.api.io.mi.com/hop1")
            {
                return Http.Resp(
                    status: 302,
                    body: null,
                    ("Location", "/hop2"),
                    ("Set-Cookie", "userId=100001; Path=/; HttpOnly"),
                    ("Set-Cookie", "cUserId=CU1; Path=/"));
            }

            if (url == "https://sts.api.io.mi.com/hop2")
            {
                return Http.Resp(body: null, headers: ("Set-Cookie", "serviceToken=ST1; Path=/"));
            }

            throw new InvalidOperationException($"unexpected url: {url}");
        };
    }

    private static Session Session(string ssecurityB64 = Http.SsecurityB64) => new(
        UserId: "U1",
        CUserId: "CU1",
        PassToken: "PT1",
        ServiceToken: "STOLD",
        Ssecurity: ssecurityB64.Base64ToBytes(),
        Region: "de");

    // ---- login --------------------------------------------------------------

    [Fact(DisplayName = "AUTH-2 valid credentials complete login and yield a full session")]
    public async Task ValidCredentialsYieldASession()
    {
        var http = new FakeMiHttp();
        ScriptHappyLogin(http);

        var result = await new MiCloud(http, region: "de").LoginAsync("parent@example.com", "hunter2");

        var ok = Assert.IsType<LoginResult.Ok>(result);
        Assert.Equal("100001", ok.Session.UserId);
        Assert.Equal("CU1", ok.Session.CUserId);
        Assert.Equal("ST1", ok.Session.ServiceToken);
        Assert.Equal("PT1", ok.Session.PassToken);
        Assert.Equal(Http.SsecurityB64, ok.Session.Ssecurity.ToBase64());
        Assert.Equal("de", ok.Session.Region);
    }

    [Fact(DisplayName = "PROTO-1+3 the auth2 form carries the hashed password and serviceLogin context")]
    public async Task Auth2FormCarriesTheHashedPassword()
    {
        var http = new FakeMiHttp();
        ScriptHappyLogin(http);
        await new MiCloud(http, region: "de").LoginAsync("parent@example.com", "hunter2");

        var auth2 = http.Log.First(r => r.Url.Contains("serviceLoginAuth2", StringComparison.Ordinal));
        var form = Http.ParseForm(auth2.Body!);
        Assert.Equal("2AB96390C7DBE3439DE74D0C9B0B1767", form["hash"]); // vector for "hunter2"
        Assert.Equal("parent@example.com", form["user"]);
        Assert.Equal("xiaomiio", form["sid"]);
        Assert.Equal("SIGN1", form["_sign"]);
        Assert.Equal("true", form["_json"]);
        Assert.Contains("deviceId=", auth2.Headers["Cookie"], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "PROTO-4 the redirect chain is walked manually collecting cookies hop by hop")]
    public async Task RedirectChainIsWalked()
    {
        var http = new FakeMiHttp();
        ScriptHappyLogin(http);
        await new MiCloud(http, region: "de").LoginAsync("parent@example.com", "hunter2");

        var urls = http.Log.Select(r => r.Url).ToList();
        Assert.Contains("https://sts.api.io.mi.com/hop1", urls);
        Assert.Contains("https://sts.api.io.mi.com/hop2", urls); // relative Location resolved
    }

    [Fact(DisplayName = "PROTO-2 a login response without the START prefix is an error")]
    public async Task MissingStartPrefixIsAnError()
    {
        var http = new FakeMiHttp
        {
            Handler = _ => Http.Resp(body: Encoding.UTF8.GetBytes("<html>maintenance</html>")),
        };

        var err = await Assert.ThrowsAsync<XiaomiException>(() => new MiCloud(http).LoginAsync("u", "p"));
        Assert.Contains("unexpected login response", err.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "AUTH-9 a rejected login surfaces a readable error")]
    public async Task RejectedLoginIsReadable()
    {
        var http = new FakeMiHttp
        {
            Handler = req => req.Url.Contains("serviceLogin?", StringComparison.Ordinal)
                ? Http.Resp(body: ServiceLoginContext())
                : Http.Resp(body: Http.LoginBody("""{"code":70016,"desc":"wrong password"}""")),
        };

        var err = await Assert.ThrowsAsync<XiaomiException>(() => new MiCloud(http).LoginAsync("u", "wrong"));
        // AUTH-9 says *readable*: the wrong-credentials code maps to plain words, and no raw response
        // JSON ever reaches the user.
        Assert.Equal("Wrong account or password.", err.Message);
    }

    [Fact(DisplayName = "AUTH-9 an unrecognised login failure is readable — never raw JSON")]
    public async Task UnknownLoginFailureIsReadable()
    {
        var http = new FakeMiHttp
        {
            Handler = req => req.Url.Contains("serviceLogin?", StringComparison.Ordinal)
                ? Http.Resp(body: ServiceLoginContext())
                : Http.Resp(body: Http.LoginBody(
                    """{"code":9999,"desc":"internal error","weird":{"nested":"blob"}}""")),
        };

        var err = await Assert.ThrowsAsync<XiaomiException>(() => new MiCloud(http).LoginAsync("u", "p"));
        Assert.Equal("Sign-in failed: internal error (code 9999)", err.Message);
        Assert.DoesNotContain("{", err.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "AUTH-3 a captcha demand surfaces the image and the resubmission carries the code")]
    public async Task CaptchaFlow()
    {
        var http = new FakeMiHttp();
        var captchaBytes = new byte[] { 0x11, 0x22, 0x33 };
        var auth2Calls = 0;

        ScriptHappyLogin(http, req =>
        {
            auth2Calls++;
            if (auth2Calls == 1)
            {
                return Http.Resp(body: Http.LoginBody("""{"captchaURL":"/pass/captcha?x=1"}"""));
            }

            // Resubmission must carry the code + ick cookie.
            Assert.Equal("abcd", Http.ParseForm(req.Body!)["captCode"]);
            Assert.Contains("ick=ICK1", req.Headers["Cookie"], StringComparison.Ordinal);
            return null; // fall through to the happy-path auth2 response
        });

        var baseHandler = http.Handler;
        http.Handler = req => req.Url == "https://account.xiaomi.com/pass/captcha?x=1"
            ? Http.Resp(
                body: captchaBytes,
                headers: new[] { ("Content-Type", "image/jpeg"), ("Set-Cookie", "ick=ICK1; Path=/") })
            : baseHandler(req);

        var result = await new MiCloud(http, region: "de").LoginAsync("parent@example.com", "hunter2");
        var captcha = Assert.IsType<LoginResult.Captcha>(result);
        Assert.Equal("image/jpeg", captcha.ContentType);
        Assert.Equal(captchaBytes, captcha.Image);

        var after = await captcha.Submit("abcd");
        Assert.IsType<LoginResult.Ok>(after);
    }

    [Fact(DisplayName = "AUTH-4+PROTO-6 two-factor reveals the masked target and completes with the ticket")]
    public async Task TwoFactorFlow()
    {
        var http = new FakeMiHttp
        {
            Handler = req => req.Url switch
            {
                var u when u.StartsWith("https://account.xiaomi.com/pass/serviceLogin?", StringComparison.Ordinal) =>
                    Http.Resp(body: ServiceLoginContext()),
                var u when u.StartsWith("https://account.xiaomi.com/pass/serviceLoginAuth2", StringComparison.Ordinal) =>
                    Http.Resp(body: Http.LoginBody(
                        """{"notificationUrl":"https://account.xiaomi.com/fe/service/identity/authStart?context=CTX"}""")),
                "https://account.xiaomi.com/identity/list?context=CTX" => Http.Resp(
                    body: Http.LoginBody("""{"code":0,"flag":4}"""),
                    headers: ("Set-Cookie", "identity_session=IS1; Path=/")),
                var u when u.StartsWith(
                    "https://account.xiaomi.com/identity/auth/verifyPhone?_flag=4&_json=true",
                    StringComparison.Ordinal) => VerifyPhone(req),
                "https://account.xiaomi.com/identity/auth/sendPhoneTicket" =>
                    Http.Resp(body: Http.LoginBody("""{"code":0}""")),
                var u when u.StartsWith(
                    "https://account.xiaomi.com/identity/auth/verifyPhone?_flag=4&ticket=123456",
                    StringComparison.Ordinal) => Http.Resp(body: Http.LoginBody(
                        """{"location":"https://sts.api.io.mi.com/2fa-hop"}""")),
                "https://sts.api.io.mi.com/2fa-hop" => Http.Resp(
                    body: null,
                    headers: new[]
                    {
                        ("Set-Cookie", "userId=100002; Path=/"),
                        ("Set-Cookie", "passToken=PT2FA; Path=/"),
                        ("Set-Cookie", "serviceToken=ST2FA; Path=/"),
                        ("Extension-Pragma", $$"""{"ssecurity":"{{Http.SsecurityB64}}"}"""),
                    }),
                var u => throw new InvalidOperationException($"unexpected url: {u}"),
            },
        };

        var result = await new MiCloud(http, region: "de").LoginAsync("parent@example.com", "hunter2");
        var twoFactor = Assert.IsType<LoginResult.TwoFactor>(result);
        Assert.Equal("phone", twoFactor.Channel);
        Assert.Equal("+972*****99", twoFactor.MaskedTarget);

        var after = Assert.IsType<LoginResult.Ok>(await twoFactor.Submit("123456"));
        Assert.Equal("100002", after.Session.UserId);
        Assert.Equal("ST2FA", after.Session.ServiceToken);
        // PROTO-4: ssecurity arrived via Extension-Pragma on the redirect hop.
        Assert.Equal(Http.SsecurityB64, after.Session.Ssecurity.ToBase64());

        static RawResponse VerifyPhone(FakeMiHttp.Req req)
        {
            Assert.Contains("identity_session=IS1", req.Headers["Cookie"], StringComparison.Ordinal);
            return Http.Resp(body: Http.LoginBody("""{"code":0,"maskedPhone":"+972*****99"}"""));
        }
    }

    [Fact(DisplayName = "AUTH-9 a rejected two-factor code reads as words — never raw JSON")]
    public async Task RejectedTwoFactorCodeIsReadable()
    {
        var http = new FakeMiHttp
        {
            Handler = req => req.Url switch
            {
                var u when u.StartsWith("https://account.xiaomi.com/pass/serviceLogin?", StringComparison.Ordinal) =>
                    Http.Resp(body: ServiceLoginContext()),
                var u when u.StartsWith("https://account.xiaomi.com/pass/serviceLoginAuth2", StringComparison.Ordinal) =>
                    Http.Resp(body: Http.LoginBody(
                        """{"notificationUrl":"https://account.xiaomi.com/fe/service/identity/authStart?context=CTX"}""")),
                "https://account.xiaomi.com/identity/list?context=CTX" => Http.Resp(
                    body: Http.LoginBody("""{"code":0,"flag":4}"""),
                    headers: ("Set-Cookie", "identity_session=IS1; Path=/")),
                var u when u.StartsWith(
                    "https://account.xiaomi.com/identity/auth/verifyPhone?_flag=4&_json=true",
                    StringComparison.Ordinal) =>
                    Http.Resp(body: Http.LoginBody("""{"code":0,"maskedPhone":"+972*****99"}""")),
                "https://account.xiaomi.com/identity/auth/sendPhoneTicket" =>
                    Http.Resp(body: Http.LoginBody("""{"code":0}""")),
                var u when u.StartsWith(
                    "https://account.xiaomi.com/identity/auth/verifyPhone?_flag=4&ticket=000000",
                    StringComparison.Ordinal) =>
                    Http.Resp(body: Http.LoginBody("""{"code":70014,"desc":"invalid ticket"}""")),
                var u => throw new InvalidOperationException($"unexpected url: {u}"),
            },
        };

        var twoFactor = Assert.IsType<LoginResult.TwoFactor>(
            await new MiCloud(http, region: "de").LoginAsync("parent@example.com", "hunter2"));

        var err = await Assert.ThrowsAsync<XiaomiException>(() => twoFactor.Submit("000000"));
        Assert.Equal(
            "That code wasn't accepted — check it and try again, or start over to get a new code.",
            err.Message);
        Assert.DoesNotContain("{", err.Message, StringComparison.Ordinal);
    }

    // ---- signed requests ------------------------------------------------------

    [Fact(DisplayName = "PROTO-9+11 device list uses exactly the session cookies and maps localip to ip")]
    public async Task DeviceListUsesExactlyTheSessionCookies()
    {
        var http = new FakeMiHttp();
        var ssecurity = Http.SsecurityB64.Base64ToBytes();
        http.Handler = req =>
        {
            Assert.Equal("https://de.api.io.mi.com/app/v2/home/device_list_page", req.Url);
            Assert.Equal("userId=U1; serviceToken=STOLD; cUserId=CU1", req.Headers["Cookie"]);
            const string result = """
                {"code":0,"result":{"list":[
                    {"did":"d1","name":"Nursery","model":"chuangmi.camera.077ac1","mac":"AA:BB","localip":"192.168.1.50"},
                    {"did":"d2","name":"Lamp","model":"yeelink.light.lamp4","mac":"CC:DD","localip":"192.168.1.60"}
                ]}}
                """;
            return Http.Resp(body: Http.EncryptResponse(req, ssecurity, result));
        };

        var devices = await new MiCloud(http, session: Session()).DeviceListAsync();
        Assert.Equal(2, devices.Count);
        Assert.Equal(new Device("d1", "Nursery", "chuangmi.camera.077ac1", "AA:BB", "192.168.1.50"), devices[0]);
        // CAM-1: the picker filter keeps only cameras.
        Assert.Equal(new[] { "d1" }, devices.Where(d => Mi.IsCamera(d.Model)).Select(d => d.Did));
    }

    [Fact(DisplayName = "AUTH-7+PROTO-10 an expired session refreshes once via passToken and re-persists")]
    public async Task ExpiredSessionRefreshesOnce()
    {
        var http = new FakeMiHttp();
        const string newSsecurityB64 = "ICEiIyQlJicoKSorLC0uLw=="; // 202122...2f
        var apiCalls = 0;
        http.Handler = req =>
        {
            var url = req.Url;
            if (url.EndsWith("/v2/home/device_list_page", StringComparison.Ordinal))
            {
                apiCalls++;
                if (apiCalls == 1)
                {
                    return Http.Resp(status: 401, body: Encoding.UTF8.GetBytes("Unauthorized"));
                }

                // PROTO-5: the retry must pair the NEW serviceToken with the NEW ssecurity.
                Assert.Contains("serviceToken=STNEW", req.Headers["Cookie"], StringComparison.Ordinal);
                return Http.Resp(body: Http.EncryptResponse(
                    req,
                    newSsecurityB64.Base64ToBytes(),
                    """{"code":0,"result":{"list":[]}}"""));
            }

            if (url.StartsWith("https://account.xiaomi.com/pass/serviceLogin?", StringComparison.Ordinal))
            {
                Assert.Equal("userId=U1; passToken=PT1", req.Headers["Cookie"]);
                return Http.Resp(body: Http.LoginBody(
                    $$"""{"ssecurity":"{{newSsecurityB64}}","passToken":"PT2","location":"https://sts.api.io.mi.com/refresh-hop"}"""));
            }

            if (url == "https://sts.api.io.mi.com/refresh-hop")
            {
                return Http.Resp(body: null, headers: ("Set-Cookie", "serviceToken=STNEW; Path=/"));
            }

            throw new InvalidOperationException($"unexpected url: {url}");
        };

        Session? refreshed = null;
        var cloud = new MiCloud(http, session: Session())
        {
            OnSessionRefreshed = s =>
            {
                refreshed = s;
                return Task.CompletedTask;
            },
        };

        var devices = await cloud.DeviceListAsync();
        Assert.Empty(devices);
        Assert.Equal(2, apiCalls);
        Assert.NotNull(refreshed); // AUTH-7: refreshed session handed back for persistence
        Assert.Equal("STNEW", refreshed!.ServiceToken);
        Assert.Equal("PT2", refreshed.PassToken);
        Assert.Equal(newSsecurityB64, refreshed.Ssecurity.ToBase64());
    }

    [Fact(DisplayName = "PROTO-5+AUTH-8 a refresh whose chain dies part-way stays retryable and mixes no tokens")]
    public async Task HalfFinishedRefreshStaysRetryable()
    {
        // The serviceLogin answer hands out a NEW ssecurity, but the hop that should deliver the
        // matching NEW serviceToken dies. Continuing with new-ssecurity + old-serviceToken would poison
        // the stored session (every signed request rejected until the next refresh).
        var http = new FakeMiHttp();
        const string newSsecurityB64 = "ICEiIyQlJicoKSorLC0uLw==";
        var apiCalls = 0;
        http.Handler = req =>
        {
            var url = req.Url;
            if (url.EndsWith("/v2/home/device_list_page", StringComparison.Ordinal))
            {
                apiCalls++;
                return Http.Resp(status: 401, body: Encoding.UTF8.GetBytes("Unauthorized"));
            }

            if (url.StartsWith("https://account.xiaomi.com/pass/serviceLogin?", StringComparison.Ordinal))
            {
                return Http.Resp(body: Http.LoginBody(
                    $$"""{"ssecurity":"{{newSsecurityB64}}","passToken":"PT2","location":"https://sts.api.io.mi.com/refresh-hop"}"""));
            }

            if (url == "https://sts.api.io.mi.com/refresh-hop")
            {
                return Http.Resp(status: 500); // chain dies here
            }

            throw new InvalidOperationException($"unexpected url: {url}");
        };

        Session? refreshed = null;
        var cloud = new MiCloud(http, session: Session())
        {
            OnSessionRefreshed = s =>
            {
                refreshed = s;
                return Task.CompletedTask;
            },
        };

        var err = await Assert.ThrowsAnyAsync<Exception>(() => cloud.DeviceListAsync());
        Assert.IsNotType<AuthExpiredException>(err); // a half-finished refresh must stay retryable
        Assert.Null(refreshed); // a mixed session must never be persisted
        Assert.Equal(1, apiCalls); // the request must not be retried with mixed tokens
    }

    [Fact(DisplayName = "APP-3 non-auth server failures surface path and status")]
    public async Task ServerFailuresSurfacePathAndStatus()
    {
        var http = new FakeMiHttp
        {
            Handler = _ => Http.Resp(status: 500, body: Encoding.UTF8.GetBytes("boom")),
        };
        var cloud = new MiCloud(http, session: Session());

        var err = await Assert.ThrowsAsync<XiaomiException>(
            () => cloud.RequestAsync("/v2/home/device_list_page", "{}"));
        Assert.Contains("500", err.Message, StringComparison.Ordinal);
        Assert.Contains("/v2/home/device_list_page", err.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "PROTO-24 miotGetProp reads a property value")]
    public async Task MiotGetProp()
    {
        var http = new FakeMiHttp();
        var ssecurity = Http.SsecurityB64.Base64ToBytes();
        http.Handler = req =>
        {
            Assert.Equal("https://de.api.io.mi.com/app/miotspec/prop/get", req.Url);
            var form = Http.ParseForm(req.Body!);
            var signedNonce = Crypto.GenSignedNonce(ssecurity, form["_nonce"].Base64ToBytes());
            var data = new JsonObj(Encoding.UTF8.GetString(
                Crypto.Rc4(signedNonce, form["data"].Base64ToBytes())));
            var p = data.OptJsonArray("params")!.GetJsonObject(0);
            Assert.Equal("cam-did-1", p.GetString("did"));
            Assert.Equal(2, p.GetInt("siid"));
            Assert.Equal(3, p.GetInt("piid"));
            const string result =
                """{"code":0,"result":[{"did":"cam-did-1","siid":2,"piid":3,"code":0,"value":2}]}""";
            return Http.Resp(body: Http.EncryptResponse(req, ssecurity, result));
        };

        var v = await new MiCloud(http, session: Session()).MiotGetPropAsync("cam-did-1", 2, 3);
        Assert.Equal(2L, v);
        Assert.Equal(NightVisionMode.Auto, Mi.NightVisionFromValue(2));
    }

    [Fact(DisplayName = "PROTO-24 miotSetProp writes the value")]
    public async Task MiotSetProp()
    {
        var http = new FakeMiHttp();
        var ssecurity = Http.SsecurityB64.Base64ToBytes();
        var lastValue = -1;
        http.Handler = req =>
        {
            Assert.Equal("https://de.api.io.mi.com/app/miotspec/prop/set", req.Url);
            var form = Http.ParseForm(req.Body!);
            var signedNonce = Crypto.GenSignedNonce(ssecurity, form["_nonce"].Base64ToBytes());
            var data = new JsonObj(Encoding.UTF8.GetString(
                Crypto.Rc4(signedNonce, form["data"].Base64ToBytes())));
            lastValue = data.OptJsonArray("params")!.GetJsonObject(0).GetInt("value");
            return Http.Resp(body: Http.EncryptResponse(req, ssecurity, """{"code":0,"result":[{"code":0}]}"""));
        };

        // ON = value 0 on the wire (PROTO-24).
        await new MiCloud(http, session: Session())
            .MiotSetPropAsync("cam-did-1", 2, 3, (int)NightVisionMode.On);
        Assert.Equal(0, lastValue);
    }

    [Fact(DisplayName = "PROTO-24 miotSetProp surfaces an item-level error code")]
    public async Task MiotSetPropSurfacesItemErrors()
    {
        var http = new FakeMiHttp();
        var ssecurity = Http.SsecurityB64.Base64ToBytes();
        http.Handler = req => Http.Resp(
            body: Http.EncryptResponse(req, ssecurity, """{"code":0,"result":[{"code":-704002}]}"""));

        var err = await Assert.ThrowsAsync<XiaomiException>(
            () => new MiCloud(http, session: Session()).MiotSetPropAsync("cam-did-1", 2, 3, 1));
        Assert.Contains("-704002", err.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "PROTO-12 miss_get_vendor sends our public key and maps the vendor response")]
    public async Task MissGetVendor()
    {
        var http = new FakeMiHttp();
        var ssecurity = Http.SsecurityB64.Base64ToBytes();
        var appKey = Enumerable.Repeat((byte)7, 32).ToArray();
        http.Handler = req =>
        {
            Assert.Equal("https://de.api.io.mi.com/app/v2/device/miss_get_vendor", req.Url);
            var form = Http.ParseForm(req.Body!);
            var signedNonce = Crypto.GenSignedNonce(ssecurity, form["_nonce"].Base64ToBytes());
            var data = new JsonObj(Encoding.UTF8.GetString(
                Crypto.Rc4(signedNonce, form["data"].Base64ToBytes())));
            Assert.Equal(appKey.ToHex(), data.GetString("app_pubkey"));
            Assert.Equal("cam-did-1", data.GetString("did"));
            Assert.Equal("TUTK_CS2_MTP", data.GetString("support_vendors"));

            const string result = """
                {"code":0,"result":{
                    "vendor":{"vendor":4,"vendor_params":{"p2p_id":"ABC-123"}},
                    "public_key":"deadbeef","sign":"SIGN-TOKEN"
                }}
                """;
            return Http.Resp(body: Http.EncryptResponse(req, ssecurity, result));
        };

        var vendor = await new MiCloud(http, session: Session()).MissGetVendorAsync("cam-did-1", appKey);
        Assert.Equal(4, vendor.Vendor);
        Assert.Equal("cs2", Mi.VendorName(vendor.Vendor));
        Assert.Equal("ABC-123", vendor.Uid);
        Assert.Equal("deadbeef", vendor.DevicePublicHex);
        Assert.Equal("SIGN-TOKEN", vendor.Sign);
    }
}
