using BabyMonitor.Core.Data;
using BabyMonitor.Core.Logging;

namespace BabyMonitor.Core.Monitor;

/// <summary>
/// One observable value. The Kotlin core uses a StateFlow; this is the same idea with the same rule —
/// setting it to what it already is notifies nobody, so the UI is not woken twenty times a second by
/// a level that did not move.
///
/// **Two things a StateFlow gives you for free, and a naive event does not.**
///
/// 1. **A subscriber is never left holding a value older than the one in the box.** `Status` is
///    written from four threads — the reader (`live`), the watchdog (`the camera stopped sending
///    audio`), the reconnect loop, and the failure announcer. Commit the value under the lock but
///    raise the event outside it, and two racing writers can commit in one order and notify in the
///    other: the box says the feed is dead and the tray was last told `live`. That is DESK-1's exact
///    failure — a status area that looks the same whether the monitor is live or dead — and it is
///    reachable on every stall. So a notification carries a version, a stale one is dropped, and the
///    last thing every subscriber hears is always what the box actually holds.
/// 2. **A subscriber cannot take the monitor down with it.** Handlers run synchronously on whichever
///    monitor thread did the write. A UI handler that throws would unwind into `Push`, fault the
///    audio task and tear down the connection — twenty times a second. The monitor does not die of a
///    bug in a view.
/// </summary>
public sealed class Observable<T>
{
    private readonly object _lock = new();
    private readonly object _notifyLock = new();
    private T _value;
    private long _version;
    private long _notifiedVersion;

    public Observable(T initial) => _value = initial;

    /// <summary>
    /// Fired after the value changes, newest-wins and in order (see <see cref="Notify"/>). A handler
    /// runs while this observable's notify-lock is held, so a handler **must not synchronously set a
    /// *different* Observable** — two observables whose handlers each write the other can wedge
    /// (A→B, B→A). Reading any observable, or setting *this* one, is fine. Marshal cross-observable
    /// writes onto the UI queue instead (the shell already does).
    /// </summary>
    public event Action<T>? Changed;

    public T Value
    {
        get
        {
            lock (_lock)
            {
                return _value;
            }
        }

        set
        {
            lock (_lock)
            {
                if (EqualityComparer<T>.Default.Equals(_value, value))
                {
                    return;
                }

                _value = value;
                _version++;
            }

            Notify();
        }
    }

    /// <summary>
    /// Deliver the newest value, once, in order. Whoever gets here first does the delivering and keeps
    /// going until there is nothing newer to deliver; a writer that arrives while that is happening
    /// leaves its value to be picked up by the loop rather than racing it onto the wire.
    /// </summary>
    private void Notify()
    {
        lock (_notifyLock)
        {
            while (true)
            {
                T value;
                long version;
                lock (_lock)
                {
                    value = _value;
                    version = _version;
                }

                if (version == _notifiedVersion)
                {
                    return;
                }

                _notifiedVersion = version;

                // One at a time, and each one guarded. A multicast delegate stops dead at the first
                // handler that throws — so a view that blew up would not only fail to draw itself, it
                // would silence every observer registered behind it. The tray is one of those.
                foreach (var observer in Changed?.GetInvocationList() ?? [])
                {
                    try
                    {
                        ((Action<T>)observer)(value);
                    }
                    catch (Exception e)
                    {
                        // A view threw. That stays the view's problem: this is a monitor thread, and
                        // the watch does not end because a label could not be set.
                        Log.E("ui", $"an observer threw while handling a change: {e.Message}", e);
                    }
                }
            }
        }
    }
}

/// <summary>
/// Shared state between the monitor (which owns itself — BG-5) and whatever UI happens to be attached.
/// The UI is a thin observer; the monitor is the owner. On Windows the monitor lives in the
/// tray-resident process, exactly as it does on the Mac.
///
/// There is deliberately no video surface here. Where the picture goes is the one thing that is purely
/// a platform's business, and holding it in shared state is how a UI type ends up dragged into the
/// engine.
/// </summary>
public static class MonitorHub
{
    public static readonly Observable<bool> Running = new(false);
    public static readonly Observable<string> Status = new(Statuses.Idle);

    /// <summary>dB above the ambient floor, 0..LevelMeter.LevelMax (LIVE-6).</summary>
    public static readonly Observable<double> Level = new(0);

    public static readonly Observable<string> CameraName = new(string.Empty);

    /// <summary>Settings mirror (LIVE-2/3, ALRM-2/7, WATCH-1): the UI persists, then updates this.</summary>
    public static readonly Observable<Settings> Settings = new(new Settings());

    /// <summary>The currently-ringing, unacknowledged alarm (ALRM-4 / WATCH-3), if any.</summary>
    public static readonly Observable<AlarmKind?> ActiveAlarm = new(null);

    /// <summary>
    /// ALRM-15: the camera (did) whose acknowledged crying alarm still awaits the "was the baby crying?"
    /// answer — null when there is nothing to ask. Only ever the most recent alarm.
    /// </summary>
    public static readonly Observable<string?> PendingCryFeedback = new(null);

    /// <summary>ALRM-16/17: learned steps for the current camera, mirrored by the engine for the UI.</summary>
    public static readonly Observable<int> CalibrationSteps = new(0);

    /// <summary>BG-8: the session died and no retry can fix it — the UI must send the user to sign-in.</summary>
    public static readonly Observable<bool> SessionExpired = new(false);

    private static long _lastAudioAtMs;

    /// <summary>
    /// Monotonic timestamp of the last *decodable audio* frame — feeds the watchdog's liveness check
    /// (WATCH-2). Audio is what monitoring means: video alone, or audio in a codec we cannot play, is
    /// not a live feed (WATCH-7). Wall clock would let an overnight DST/NTP correction hide an outage,
    /// so it must never be used here.
    /// </summary>
    public static long LastAudioAtMs
    {
        get => Interlocked.Read(ref _lastAudioAtMs);
        set => Interlocked.Exchange(ref _lastAudioAtMs, value);
    }

    /// <summary>Set by the engine; routes acknowledge presses (window button, tray menu).</summary>
    public static Action? OnAcknowledge { get; set; }

    /// <summary>Set by the engine; applies a "was the baby crying?" answer to a camera (ALRM-16).</summary>
    public static Action<string, bool>? OnCryFeedback { get; set; }

    /// <summary>Set by the engine; forgets the current camera's learned tuning (ALRM-17).</summary>
    public static Action? OnCalibrationReset { get; set; }

    public static void ApplySettings(Settings s) => Settings.Value = s;

    public static void Acknowledge() => OnAcknowledge?.Invoke();

    /// <summary>ALRM-15/16: answer the pending question. A yes/no with no question pending does nothing.</summary>
    public static void SubmitCryFeedback(bool wasCry)
    {
        var did = PendingCryFeedback.Value;
        if (did == null)
        {
            return;
        }

        PendingCryFeedback.Value = null;
        OnCryFeedback?.Invoke(did, wasCry);
    }

    /// <summary>ALRM-15: the question is optional — dismissing it learns nothing and asks nothing again.</summary>
    public static void DismissCryFeedback() => PendingCryFeedback.Value = null;

    public static void ResetCalibration() => OnCalibrationReset?.Invoke();
}
