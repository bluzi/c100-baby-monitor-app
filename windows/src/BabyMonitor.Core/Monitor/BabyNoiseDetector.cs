using BabyMonitor.Core.Data;
using BabyMonitor.Core.Dsp;

namespace BabyMonitor.Core.Monitor;

/// <summary>
/// ALRM-1/2/3/5/13: decides when a sound is the baby crying. Pure logic — audio analysis feeds it
/// windows of metrics; it answers "alarm now?". Clock injected via nowMs. There is exactly one
/// algorithm (ALRM-2): the only tunable is the loudness threshold — set from the sensitivity dial plus
/// the learned per-camera steps (CryCalibration) — and nothing else.
///
/// Ring/acknowledge lifecycle (ALRM-4/5): when a trigger starts the ringer, the engine sets
/// <see cref="Suppressed"/>; on acknowledgment it clears it and calls <see cref="Snooze"/> with
/// now + cooldown.
/// </summary>
public sealed class BabyNoiseDetector
{
    /// <summary>A cry is a clear, sustained pitch. Fans, white noise, rumble and bangs never reach this.</summary>
    private const double MinTonality = 0.30;

    /// <summary>A cry in the room keeps harmonics above 1 kHz. Through a wall or door, they are gone.</summary>
    private const double MinBrightness = 0.12;

    /// <summary>Rumble — and the muffled remains of next-door voices — pile energy below 300 Hz. A cry doesn't.</summary>
    private const double MaxLowRatio = 0.55;

    private readonly Queue<Window> _recent = new();
    private readonly long _sustainMs;
    private readonly double _minCoverage;

    private long _snoozeUntilMs = long.MinValue;

    public BabyNoiseDetector(long sustainMs = 2000, double minCoverage = 0.6)
    {
        _sustainMs = sustainMs;
        _minCoverage = minCoverage;
        ThresholdDb = CryCalibration.EffectiveThresholdDb(Settings.SensitivityDefault, 0);
    }

    public bool Enabled { get; set; }

    public double ThresholdDb { get; set; }

    /// <summary>True while an alarm is sounding unacknowledged — nothing new may trigger (ALRM-5).</summary>
    public bool Suppressed { get; set; }

    /// <summary>
    /// Is this window the sound of a baby crying *in this room* (ALRM-3)?
    ///
    /// Every clause rejects one real thing (ALRM-13):
    ///  - loud enough    → the room's own quiet
    ///  - pitch in range → adults, in ANY room: their fundamental is an octave or more below a baby's
    ///  - tonal          → fans, white noise, rumble, door slams (no stable pitch at all)
    ///  - bright         → speech and TV through a wall (a wall is a low-pass filter)
    ///  - not bass-heavy → traffic rumble, and again the muffled remains of next-door voices
    ///
    /// Loudness alone cannot do this: at a high sensitivity, conversation next door is louder than the
    /// baby. Pitch alone cannot either — a fan has no pitch but neither does a slam. Together they can.
    /// Only the loudness clause is tunable (sensitivity + learning); the character gates never move.
    /// </summary>
    public static bool IsCryLike(double levelDb, double thresholdDb, WindowMetrics m) =>
        levelDb >= thresholdDb &&
        m.PitchHz >= Analysis.CryPitchMinHz &&
        m.PitchHz <= Analysis.CryPitchMaxHz &&
        m.Tonality >= MinTonality &&
        m.Brightness >= MinBrightness &&
        m.LowRatio <= MaxLowRatio;

    /// <summary>Quiet period after an acknowledgment (ALRM-5).</summary>
    public void Snooze(long untilMs) => _snoozeUntilMs = untilMs;

    /// <summary>Feed one analysis window. Returns true when the alarm should start ringing now.</summary>
    public bool OnWindow(double levelDb, WindowMetrics metrics, long windowMs, long nowMs)
    {
        var cryLike = IsCryLike(levelDb, ThresholdDb, metrics);
        _recent.Enqueue(new Window(nowMs, windowMs, cryLike));
        while (_recent.Count > 0 && _recent.Peek().AtMs < nowMs - _sustainMs)
        {
            _recent.Dequeue();
        }

        if (!Enabled || Suppressed || nowMs < _snoozeUntilMs)
        {
            return false;
        }

        var fired = CryTrigger(nowMs);
        if (fired)
        {
            _recent.Clear();
        }

        return fired;
    }

    /// <summary>ALRM-3: cry-like sound held across the sustain span. A door slam is loud but over in 50 ms.</summary>
    private bool CryTrigger(long nowMs)
    {
        if (_recent.Count == 0)
        {
            return false;
        }

        var first = _recent.Peek();
        var span = nowMs - first.AtMs + first.DurationMs;
        if (span < _sustainMs)
        {
            return false; // judge only once a full span has been observed
        }

        var cryMs = _recent.Where(w => w.IsCry).Sum(w => w.DurationMs);
        return cryMs >= _minCoverage * _sustainMs;
    }

    private sealed record Window(long AtMs, long DurationMs, bool IsCry);
}
