using System.Net;
using System.Text;

namespace BabyMonitor.Core.Net;

/// <summary>
/// Deliberately dumb HTTP client: no cookie jar, no automatic redirects, no header rewriting.
///
/// The Mi gateway rejects signed requests carrying any cookie beyond the ones we set (PROTO-9), and
/// ssecurity hides in redirect-hop headers (PROTO-4) — both need this control. .NET's HttpClient
/// would happily do both by default, and both failures look exactly like an expired session, so the
/// handler below switches them off explicitly.
/// </summary>
public sealed record RawResponse(
    int Status,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body)
{
    public string? Header(string name) => Headers
        .FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
        .Value;

    /// <summary>
    /// Every Set-Cookie, separately. .NET keeps them apart (Apple's stack merges them, which is why
    /// the Mac's client had to reach for a cookie parser) — the Mi auth chain sends several per hop
    /// and a merged one loses the session tokens.
    /// </summary>
    public IReadOnlyList<string> SetCookies() => Headers
        .Where(h => string.Equals(h.Key, "set-cookie", StringComparison.OrdinalIgnoreCase))
        .Select(h => h.Value)
        .ToList();
}

public interface IMiHttp
{
    Task<RawResponse> RequestAsync(
        string url,
        string method = "GET",
        IReadOnlyDictionary<string, string>? headers = null,
        string? body = null,
        CancellationToken ct = default);
}

public sealed class SystemMiHttp : IMiHttp, IDisposable
{
    private const int DefaultTimeoutMs = 20_000;

    private static readonly Lazy<SystemMiHttp> Instance = new(() => new SystemMiHttp());

    private readonly HttpClient _client;

    public SystemMiHttp(int timeoutMs = DefaultTimeoutMs)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, // PROTO-4: we walk the chain ourselves, harvesting as we go
            UseCookies = false, // PROTO-9: exactly the cookies we set, and no others
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Android-7.1.1-1.0.0-ONEPLUS A3010-136-A7CBC1CD53B7-app-cmp-2020");
    }

    public static SystemMiHttp Shared => Instance.Value;

    public async Task<RawResponse> RequestAsync(
        string url,
        string method = "GET",
        IReadOnlyDictionary<string, string>? headers = null,
        string? body = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded")
                {
                    CharSet = "UTF-8",
                };
        }

        try
        {
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            var collected = new List<KeyValuePair<string, string>>();
            foreach (var (name, values) in response.Headers)
            {
                collected.AddRange(values.Select(v => new KeyValuePair<string, string>(name, v)));
            }

            foreach (var (name, values) in response.Content.Headers)
            {
                collected.AddRange(values.Select(v => new KeyValuePair<string, string>(name, v)));
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return new RawResponse((int)response.StatusCode, url, collected, bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // **A timeout is not a cancellation, and the difference is a night of monitoring.**
            //
            // HttpClient reports its own timeout as a TaskCanceledException — which IS an
            // OperationCanceledException. The monitor treats one of those as "the user stopped
            // monitoring" and unwinds the reconnect loop for good: no retry, no error, no alarm,
            // nothing in the log but silence. One slow answer from Mi Cloud at 2am would have ended
            // the night's monitoring, and the app would have gone on looking like it was watching.
            //
            // On the JVM the same event surfaces as SocketTimeoutException — an IOException — and is
            // simply retried. So it is made to look like one here. (The sockets already do this; the
            // HTTP client was the one place it was missed.)
            throw new IOException($"http: {method} {url} timed out");
        }
    }

    public void Dispose() => _client.Dispose();
}
