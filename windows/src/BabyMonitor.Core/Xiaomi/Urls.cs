using System.Text;
using System.Text.RegularExpressions;

namespace BabyMonitor.Core.Xiaomi;

/// <summary>
/// URL helpers, ported rather than taken from the framework.
///
/// <see cref="UrlEncode"/> must stay byte-for-byte what Java's URLEncoder produces: the signed
/// request form (PROTO-7) carries base64, and base64's '+' and '=' MUST arrive percent-encoded or
/// the gateway rejects the signature. .NET's Uri.EscapeDataString has a different unreserved set and
/// leaves a space as %20 rather than '+', so it is the wrong tool here — quietly, and only in
/// production. The interop vectors compare the whole encoded form against the reference
/// implementation, so a drift fails the build rather than the camera.
/// </summary>
public static class Urls
{
    private const string Unreserved = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-*_";
    private const string UpperHex = "0123456789ABCDEF";

    private static readonly Regex Scheme = new("^[a-zA-Z][a-zA-Z0-9+.\\-]*:", RegexOptions.Compiled);

    /// <summary>application/x-www-form-urlencoded, UTF-8 — space becomes '+', as Java's URLEncoder does.</summary>
    public static string UrlEncode(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (b < 0x80 && Unreserved.Contains(c, StringComparison.Ordinal))
            {
                sb.Append(c);
            }
            else if (c == ' ')
            {
                sb.Append('+');
            }
            else
            {
                sb.Append('%').Append(UpperHex[b >> 4]).Append(UpperHex[b & 0x0f]);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// PROTO-4: resolve a redirect's Location against the URL it came from. Xiaomi's auth chain mixes
    /// absolute URLs with absolute paths, and a mis-resolved hop loses the ssecurity that only ever
    /// appears in one hop's headers.
    /// </summary>
    public static string ResolveUrl(string base_, string location)
    {
        if (location.Length == 0)
        {
            return base_;
        }

        if (Scheme.IsMatch(location))
        {
            return location;
        }

        var schemeEnd = base_.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            throw new ArgumentException($"url: base has no scheme: {base_}", nameof(base_));
        }

        var scheme = base_[..schemeEnd];
        var afterScheme = base_[(schemeEnd + 3)..];
        var authorityEnd = afterScheme.IndexOfAny(new[] { '/', '?', '#' });
        var authority = authorityEnd < 0 ? afterScheme : afterScheme[..authorityEnd];
        var path = authorityEnd < 0 ? string.Empty : afterScheme[authorityEnd..];

        if (location.StartsWith("//", StringComparison.Ordinal))
        {
            return $"{scheme}:{location}";
        }

        if (location.StartsWith('/'))
        {
            return $"{scheme}://{authority}{location}";
        }

        // Relative to the base's directory.
        var basePath = path.Split('?')[0].Split('#')[0];
        var lastSlash = basePath.LastIndexOf('/');
        var dir = (lastSlash < 0 ? string.Empty : basePath[..lastSlash]) + "/";
        return $"{scheme}://{authority}{dir}{location}";
    }
}
