using System.Globalization;

namespace BabyMonitor.Core.Monitor;

/// <summary>
/// The engine's small pure rules — reconnect backoff, what "the feed is alive" means, what the user
/// is told, and how a level is written down. Each is here rather than inline in the engine for the
/// same reason: a rule written in a loop is a rule nobody can test, and every one of these decides
/// something a parent is relying on.
/// </summary>
public static class Statuses
{
    public const string Idle = "idle";
    public const string Connecting = "connecting";
    public const string Live = "live";
    public const string Stopped = "stopped";
    public const string SessionExpired = "session-expired";
    public const string MonitorFailed = "monitor-failed"; // WATCH-11
    public const string UnsupportedCamera = "unsupported-camera"; // LIVE-12

    /// <summary>Status while waiting to reconnect — whole seconds, rounded up, counting down (LIVE-4).</summary>
    public static string ReconnectStatus(long remainingMs) =>
        $"reconnecting in {(remainingMs + 999) / 1000}s";

    /// <summary>
    /// LIVE-2/4: the live feed's one-line summary. Muted is stated in words — at a glance, and without
    /// having to know which way the speaker icon's convention goes.
    /// </summary>
    public static string StatusLine(string cameraName, string rawStatus, bool muted) =>
        $"{(cameraName.Length == 0 ? "Camera" : cameraName)} — {FriendlyStatus(rawStatus)}{(muted ? " · muted" : string.Empty)}";

    /// <summary>Map a raw engine status to user-facing copy (APP-3). Unknown statuses pass through.</summary>
    public static string FriendlyStatus(string raw)
    {
        if (raw == Idle)
        {
            return "Starting…";
        }

        if (raw == Connecting)
        {
            return "Connecting…";
        }

        if (raw == Live)
        {
            return "Live";
        }

        if (raw == Stopped)
        {
            return "Stopped";
        }

        if (raw == SessionExpired)
        {
            return "Session expired — open the app to sign in"; // BG-8
        }

        if (raw == MonitorFailed)
        {
            return "Monitoring stopped working — press Resume to restart"; // WATCH-11
        }

        if (raw == UnsupportedCamera)
        {
            return "This camera model isn't supported"; // LIVE-12
        }

        if (raw.StartsWith("reconnecting", StringComparison.Ordinal))
        {
            return char.ToUpperInvariant(raw[0]) + raw[1..];
        }

        if (raw.StartsWith("error:", StringComparison.Ordinal))
        {
            return "Connection lost — retrying";
        }

        return raw;
    }
}

/// <summary>LIVE-5: reconnect waits grow from sub-second to a capped tens-of-seconds.</summary>
public static class Backoff
{
    public static readonly IReadOnlyList<long> ReconnectBackoffMs = new long[] { 500, 1000, 2000, 5000, 10_000, 15_000 };

    public static long ReconnectDelayMs(int attempt) =>
        ReconnectBackoffMs[Math.Clamp(attempt, 0, ReconnectBackoffMs.Count - 1)];
}

/// <summary>
/// WATCH-2 / WATCH-7: what "the feed is alive" actually means. Pure so the decision the whole monitor
/// rests on — is silence real, or is the app lying? — is testable.
///
/// The engine reports a status and stamps every decodable *audio* frame — audio is what monitoring
/// means; video alone is not a live feed. A connection can be *up* (TCP happy, keepalives flowing,
/// video even rendering) while the audio has stopped: camera unplugged, a router black-holing traffic,
/// a dead audio stream. That looks identical to a quiet nursery unless we check the clock.
/// </summary>
public static class FeedLiveness
{
    /// <summary>No audio within this window means the feed is not delivering, whatever the socket thinks.</summary>
    public const long FeedStaleMs = 3_000;

    /// <summary>A live connection silent this long is dead in all but name: drop it and reconnect (WATCH-7).</summary>
    public const long StallMs = 8_000;

    /// <summary>Is the feed genuinely delivering audio right now? Anything but a fresh frame is "no".</summary>
    public static bool FeedAlive(string status, long lastAudioAtMs, long nowMs) =>
        status == Statuses.Live && lastAudioAtMs > 0 && nowMs - lastAudioAtMs < FeedStaleMs;

    /// <summary>
    /// Is the connection claiming to be live while delivering no audio? Such a connection never recovers
    /// on its own (the read just blocks), so the engine must force it closed (WATCH-7).
    /// </summary>
    public static bool FeedStalled(string status, long lastAudioAtMs, long nowMs) =>
        status == Statuses.Live && lastAudioAtMs > 0 && nowMs - lastAudioAtMs > StallMs;
}

/// <summary>
/// LIVE-8: live video must never fall progressively behind. When the consumer can't keep up (slow
/// decoder, render stall), we stop feeding frames and resume at the next keyframe — decoding a partial
/// GOP would only show artifacts.
/// </summary>
public sealed class VideoCatchup
{
    /// <summary>≈1 s of 40 ms Opus packets; older ones get dropped.</summary>
    public const int AudioMaxBacklogPackets = 25;

    private readonly int _maxBacklogFrames;
    private bool _skipping;

    public VideoCatchup(int maxBacklogFrames = 30) => _maxBacklogFrames = maxBacklogFrames;

    /// <summary>
    /// Decide whether to feed this frame to the decoder. <paramref name="backlogFrames"/> is how many
    /// frames are still queued behind it. Once the backlog exceeds the limit, frames are dropped until
    /// the next keyframe, which re-enters cleanly.
    /// </summary>
    public bool Admit(bool isKeyframe, int backlogFrames)
    {
        if (backlogFrames > _maxBacklogFrames)
        {
            _skipping = true;
        }

        if (!_skipping)
        {
            return true;
        }

        if (!isKeyframe)
        {
            return false;
        }

        _skipping = false;
        return true;
    }
}

public static class Format
{
    /// <summary>
    /// One decimal place, locale-independent. The room-level log line (LIVE-6) is read off a machine to
    /// reconstruct a night — it must look the same everywhere, and a locale that writes "0,0" would make
    /// it un-greppable.
    /// </summary>
    public static string OneDecimal(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Inf" : "-Inf";
        }

        var scaled = (long)Math.Round(Math.Abs(value) * 10, MidpointRounding.AwayFromZero);
        var sign = value < 0 && scaled != 0 ? "-" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{scaled / 10}.{scaled % 10}");
    }
}
