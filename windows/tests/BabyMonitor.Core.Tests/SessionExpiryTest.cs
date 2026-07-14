using System.Text;
using BabyMonitor.Core.Net;
using BabyMonitor.Core.Xiaomi;
using Xunit;

namespace BabyMonitor.Core.Tests;

// BG-8: declaring "your session expired" DELETES the stored session and forces a new sign-in. Getting
// that wrong at 3am is worse than any other bug in the app: monitoring stops and the parent must find
// their Mi password to get it back. So expiry may only be declared when Xiaomi itself refused the
// long-lived token — never when we simply could not get a straight answer.

internal sealed class ScriptedHttp : IMiHttp
{
    private readonly Func<string, RawResponse> _handler;

    public ScriptedHttp(Func<string, RawResponse> handler) => _handler = handler;

    public Task<RawResponse> RequestAsync(
        string url,
        string method = "GET",
        IReadOnlyDictionary<string, string>? headers = null,
        string? body = null,
        CancellationToken ct = default) => Task.FromResult(_handler(url));
}

public class SessionExpiryTest
{
    private static Session Session() => new(
        UserId: "100001",
        CUserId: "CU1",
        PassToken: "PT1",
        ServiceToken: "ST1",
        Ssecurity: new byte[16],
        Region: "de");

    private static RawResponse ExpiredApi() => new(
        401,
        string.Empty,
        Array.Empty<KeyValuePair<string, string>>(),
        Encoding.UTF8.GetBytes("Unauthorized"));

    private static Task RefreshWith(IMiHttp http) => new MiCloud(http, session: Session()).DeviceListAsync();

    [Fact(DisplayName = "AUTH-8+BG-8 Xiaomi refusing the long-lived token is a real expiry")]
    public async Task RefusedTokenIsAnExpiry()
    {
        // The account server answers properly and hands back no location: the token is dead.
        var http = new ScriptedHttp(url => url.Contains("/pass/serviceLogin?", StringComparison.Ordinal)
            ? new RawResponse(
                200,
                string.Empty,
                Array.Empty<KeyValuePair<string, string>>(),
                Encoding.UTF8.GetBytes("&&&START&&&{\"code\":70016}"))
            : ExpiredApi());

        await Assert.ThrowsAsync<AuthExpiredException>(() => RefreshWith(http));
    }

    [Fact(DisplayName = "AUTH-8+BG-8 a captive portal or maintenance page must never sign the user out")]
    public async Task CaptivePortalIsNotAnExpiry()
    {
        // Hotel Wi-Fi (or a Xiaomi outage) answers HTML instead of a login body. The token may be
        // perfectly good — treating this as expiry would throw away a working session.
        var http = new ScriptedHttp(url => url.Contains("/pass/serviceLogin?", StringComparison.Ordinal)
            ? new RawResponse(
                200,
                string.Empty,
                Array.Empty<KeyValuePair<string, string>>(),
                Encoding.UTF8.GetBytes("<html>Sign in to WiFi</html>"))
            : ExpiredApi());

        var err = await Assert.ThrowsAnyAsync<Exception>(() => RefreshWith(http));
        Assert.IsNotType<AuthExpiredException>(err);
    }

    [Fact(DisplayName = "AUTH-8+BG-8 a network failure during refresh must never sign the user out")]
    public async Task NetworkFailureIsNotAnExpiry()
    {
        var http = new ScriptedHttp(url =>
        {
            if (url.Contains("/pass/serviceLogin?", StringComparison.Ordinal))
            {
                // What a real transport failure looks like: the socket layer's own error, not
                // Xiaomi's. Wi-Fi dropped, DNS failed, captive portal.
                throw new SocketClosedException("http: could not resolve account.xiaomi.com");
            }

            return ExpiredApi();
        });

        var err = await Assert.ThrowsAnyAsync<Exception>(() => RefreshWith(http));
        Assert.IsNotType<AuthExpiredException>(err);
    }
}
