namespace BabyMonitor.Core.Monitor;

/// <summary>
/// WATCH-1/2/3/9: alarm when the feed has been dead longer than the grace period, once per outage.
/// Pure — fed by a periodic tick with an injected clock.
/// </summary>
public sealed class StreamWatchdog
{
    private long? _downSinceMs;
    private bool _firedThisOutage;
    private bool _wasArmed;

    public StreamWatchdog(bool enabled = false, long graceMs = 30_000)
    {
        Enabled = enabled;
        GraceMs = graceMs;
    }

    public bool Enabled { get; set; }

    public long GraceMs { get; set; }

    /// <summary>
    /// WATCH-9: the watchdog guards the crying alarm, so it is armed only while the crying alarm could
    /// itself ring — its own toggle, the crying-alarm toggle, and the crying alarm's active hours all
    /// agree. The engine feeds this into <see cref="Enabled"/> every tick; an outage outliving an
    /// unarmed stretch then fires as soon as arming returns.
    /// </summary>
    public static bool Armed(bool watchdogEnabled, bool alarmEnabled, AlarmSchedule schedule, int minutesOfDay) =>
        watchdogEnabled && alarmEnabled && schedule.IsActive(minutesOfDay);

    /// <summary>
    /// Report the feed state. Returns true exactly when the alarm should start: the feed has been
    /// continuously dead for the grace period and this outage hasn't fired yet. Recovery re-arms
    /// (WATCH-3).
    /// </summary>
    public bool OnTick(bool feedAlive, long nowMs)
    {
        // WATCH-9 wins over WATCH-3's one-alarm-per-outage: a new armed window (the crying alarm turned
        // on, or its hours beginning) is a new obligation, even for an outage already alarmed and
        // acknowledged in an earlier window.
        if (Enabled && !_wasArmed)
        {
            _firedThisOutage = false;
        }

        _wasArmed = Enabled;

        if (feedAlive)
        {
            _downSinceMs = null;
            _firedThisOutage = false;
            return false;
        }

        _downSinceMs ??= nowMs;
        if (!Enabled || _firedThisOutage)
        {
            return false;
        }

        if (nowMs - _downSinceMs.Value < GraceMs)
        {
            return false;
        }

        _firedThisOutage = true;
        return true;
    }

    /// <summary>
    /// WATCH-6: the alarm we asked for never sounded (another alarm was already ringing). Take the fire
    /// back so the next tick retries — an unheard alarm must not count as an alarm.
    /// </summary>
    public void Unfire() => _firedThisOutage = false;

    /// <summary>Monitoring stopped by the user — a dead feed is expected; forget the outage.</summary>
    public void Reset()
    {
        _downSinceMs = null;
        _firedThisOutage = false;
    }
}
