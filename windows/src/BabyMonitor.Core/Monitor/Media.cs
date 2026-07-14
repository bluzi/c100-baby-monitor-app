namespace BabyMonitor.Core.Monitor;

// The engine's platform edge. Everything above this is the monitor; everything below it is a
// speaker, a screen and a buzzer. The Windows shell implements these and gets the whole monitor —
// protocol, reconnect, watchdog, cry detection, alarm schedule — unchanged.
//
// The contracts here are not incidental. Each one encodes something the monitor depends on being
// true, and an implementation that breaks it breaks the monitor silently.

public enum AlarmKind
{
    BabyNoise,
    FeedDown,
}

/// <summary>
/// Opus in, speaker out, with an analysis tap.
///
/// LIVE-3, and it is load-bearing: **mute silences the speaker only**. Decoding and the analysis
/// callback must keep running while muted, because the level meter and the crying alarm feed off
/// them. An implementation that mutes by pausing the decoder has quietly disabled the alarm — the app
/// would look like it was monitoring, and it would not be.
///
/// <see cref="Start"/> may throw: the engine treats that as a dead connection, reconnects and
/// rebuilds. A write that fails must throw too, rather than play silence — a parent must never
/// mistake a broken audio path for a quiet room.
/// </summary>
public interface IAudioOutput
{
    bool Muted { get; set; }

    void Start();

    void Push(byte[] packet, long ptsMs);

    void Release();
}

/// <summary>
/// H.265 Annex-B access units in, picture out.
///
/// LIVE-7: best-effort by design. <see cref="Push"/> must NEVER throw — video trouble must never take
/// audio monitoring down with it. Swallow, log, and recover at the next keyframe. On Windows this is
/// the interface behind which "this PC has no H.265 decoder" (WIN-20) lives, and it is exactly why
/// that is a message rather than a dead monitor.
/// </summary>
public interface IVideoOutput
{
    void Push(byte[] annexB, long ptsMs);

    void Release();
}

/// <summary>
/// The last link between a crying baby and a sleeping parent. Implementations must be paranoid:
/// never throw, and cut through whatever the platform does to "quiet" audio.
///
/// <see cref="Ring"/> returns false when another alarm is already sounding. The caller must then
/// retry later rather than treat this alarm as delivered (WATCH-6) — an unheard alarm is not an alarm.
/// </summary>
public interface IRinger
{
    bool Ring(AlarmKind kind, string cameraName);

    void Acknowledge();
}

/// <summary>The platform's media stack, handed to the engine at construction.</summary>
public interface IMediaFactory
{
    /// <summary>The callback receives every decoded window, muted or not — that is what LIVE-3 means.</summary>
    IAudioOutput Audio(Action<short[], int> onPcmWindow);

    IVideoOutput Video();
}
