using BabyMonitor.Core.Data;

namespace BabyMonitor.Core.Monitor;

/// <summary>
/// One observable value. The Kotlin core uses a StateFlow; this is the same idea with the same rule —
/// setting it to what it already is notifies nobody, so the UI is not woken twenty times a second by
/// a level that did not move.
/// </summary>
public sealed class Observable<T>
{
    private readonly object _lock = new();
    private T _value;

    public Observable(T initial) => _value = initial;

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
            }

            Changed?.Invoke(value);
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
