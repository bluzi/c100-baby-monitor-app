using System.Diagnostics;
using System.Security.Cryptography;

namespace BabyMonitor.Core.Platform;

/// <summary>
/// The clocks and the randomness. Small, but two of them are load-bearing.
/// </summary>
public static class Clock
{
    private static readonly Stopwatch Monotonic = Stopwatch.StartNew();

    /// <summary>Cryptographically secure random bytes — nonces, NaCl private keys.</summary>
    public static byte[] SecureRandomBytes(int n) => RandomNumberGenerator.GetBytes(n);

    /// <summary>
    /// Monotonic milliseconds since this process started.
    ///
    /// Everything the monitor times — outage length, alarm sustain, cooldown — must be immune to the
    /// wall clock jumping (NTP resync, DST). A backwards jump at 3am would otherwise hide an outage
    /// and mute the detector. Never use the wall clock for a duration.
    ///
    /// A sleeping PC advances nothing at all (the same gap a Mac has), so the shell detects sleep
    /// explicitly and reports the outage on wake — see WIN-11.
    /// </summary>
    public static long ElapsedRealtimeMs() => Monotonic.ElapsedMilliseconds;

    /// <summary>Wall-clock epoch millis. Only for things that are genuinely about the wall clock.</summary>
    public static long WallClockMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Minutes since local midnight (0..1439). Used only by the alarm schedule (ALRM-7) and the
    /// watchdog's arming (WATCH-9), because "only at night" is a statement about the wall clock.
    /// </summary>
    public static int WallClockMinutesOfDay()
    {
        var now = DateTime.Now;
        return (now.Hour * 60) + now.Minute;
    }
}
