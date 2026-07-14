namespace BabyMonitor.Core.Monitor;

/// <summary>
/// LIVE-6: loudness relative to an adaptive room baseline. A low quantile of recent loudness is the
/// floor; the reported level is a fast-attack / slower-release envelope of (signal - floor).
///
/// Two robustness choices, both measured on a real camera in a quiet room:
///
///  - The floor is a LOW quantile of recent windows, not the median, and that is load-bearing: during
///    real crying (bursts with breaths between them) the quiet breaths are what the quantile picks,
///    so the floor stays anchored to the ROOM and an ongoing cry is never absorbed into "usual" — it
///    can still re-alarm after an acknowledgment (ALRM-5).
///  - The level judges the MEDIAN of the last few windows, because a quiet room's per-window loudness
///    jitters across ±5 dB (sensor self-noise plus opus coding noise near silence). One hot window is
///    jitter; a cry holds every window hot. Without this, the fast-attack envelope rides the tops of
///    the jitter and a silent nursery reads a permanently bouncing 1–5 dB "louder than usual" — which
///    both looks broken and quietly eats the alarm's loudness margin.
/// </summary>
public sealed class LevelMeter
{
    /// <summary>dB above floor shown/used by the alarm scale.</summary>
    public const double LevelMax = 24.0;

    /// <summary>Below this, a reading is the last shreds of ambient flutter, not activity.</summary>
    private const double LevelDisplaySquelchDb = 2.0;

    private const int FloorWindowSamples = 120;
    private const double FloorQuantile = 0.3;
    private const double FloorInitDb = -72.0;
    private const double FloorWarmupRiseDbPerSec = 14.0;
    private const double FloorRiseDbPerSec = 0.45;
    private const double FloorFallDbPerSec = 18.0;
    private const double LevelAttackTauMs = 30.0;
    private const double LevelReleaseTauMs = 190.0;

    // Sized to the measured gap between a quiet room's median and the low-quantile floor — absorbs
    // the ambient spread so a steady room reads 0, without hiding a real event.
    private const double LevelDeadbandDb = 2.0;
    private const int LevelMedianWindows = 5;
    private const double SignalPeakBlend = 0.18;
    private const double SilenceDb = -80.0;

    private readonly double[] _recentSignalDb = CreateFilled(LevelMedianWindows, SilenceDb);
    private readonly double[] _floorHistoryDb = CreateFilled(FloorWindowSamples, FloorInitDb);

    private double _noiseFloorDb = FloorInitDb;
    private double _levelEnvelopeDb;
    private long _lastSampleAt;
    private int _recentSignalCount;
    private int _recentSignalIndex;
    private int _floorHistoryCount;
    private int _floorHistoryIndex;

    /// <summary>The current absolute noise floor (dBFS) — for the room-level log line only.</summary>
    public double FloorDb => _noiseFloorDb;

    /// <summary>
    /// LIVE-6: what the UI shows for a level. Display only — the detector and the room-level log keep
    /// the unrounded value, so the calmer display costs no alarm sensitivity and hides no diagnostics.
    /// </summary>
    public static double DisplayLevelDb(double levelDb) => levelDb < LevelDisplaySquelchDb ? 0.0 : levelDb;

    /// <summary>
    /// Push one measurement (rms and peak amplitude in 0..1) and get the level in dB above the
    /// adaptive room floor. 0 = ambient; positive = louder than the room.
    /// </summary>
    public double Process(double rms, double peak, long nowMs)
    {
        var dtMs = _lastSampleAt == 0L
            ? 100.0
            : Math.Min(1000.0, Math.Max(16.0, nowMs - _lastSampleAt));
        _lastSampleAt = nowMs;

        var blended = (rms * (1 - SignalPeakBlend)) + (peak * SignalPeakBlend);
        var signalDb = AmplitudeToDb(blended);

        // The floor tracks the raw windows (its quantile is already robust); the level judges the
        // median of the last few, so single-window jitter never reads as "the room got louder".
        _floorHistoryDb[_floorHistoryIndex] = signalDb;
        _floorHistoryIndex = (_floorHistoryIndex + 1) % FloorWindowSamples;
        if (_floorHistoryCount < FloorWindowSamples)
        {
            _floorHistoryCount++;
        }

        _recentSignalDb[_recentSignalIndex] = signalDb;
        _recentSignalIndex = (_recentSignalIndex + 1) % LevelMedianWindows;
        if (_recentSignalCount < LevelMedianWindows)
        {
            _recentSignalCount++;
        }

        var recent = _recentSignalDb[.._recentSignalCount];
        Array.Sort(recent);
        var filteredDb = recent[_recentSignalCount / 2];

        var target = FloorTargetDb();
        var riseRate = _floorHistoryCount < 20 ? FloorWarmupRiseDbPerSec : FloorRiseDbPerSec;
        var maxRise = riseRate * dtMs / 1000;
        var maxFall = FloorFallDbPerSec * dtMs / 1000;
        _noiseFloorDb = target > _noiseFloorDb
            ? Math.Min(target, _noiseFloorDb + maxRise)
            : Math.Max(target, _noiseFloorDb - maxFall);

        var targetLevelDb = Math.Max(0.0, filteredDb - _noiseFloorDb - LevelDeadbandDb);
        var tau = targetLevelDb > _levelEnvelopeDb ? LevelAttackTauMs : LevelReleaseTauMs;
        var alpha = 1 - Math.Exp(-dtMs / tau);
        _levelEnvelopeDb += (targetLevelDb - _levelEnvelopeDb) * alpha;
        return Math.Max(0.0, _levelEnvelopeDb);
    }

    private static double[] CreateFilled(int length, double value)
    {
        var array = new double[length];
        Array.Fill(array, value);
        return array;
    }

    private static double AmplitudeToDb(double value) => value > 0 ? 20 * Math.Log10(value) : SilenceDb;

    private double FloorTargetDb()
    {
        if (_floorHistoryCount == 0)
        {
            return FloorInitDb;
        }

        var ordered = _floorHistoryDb[.._floorHistoryCount];
        Array.Sort(ordered);
        var idx = Math.Min(ordered.Length - 1, Math.Max(0, (int)Math.Floor((ordered.Length - 1) * FloorQuantile)));
        return ordered[idx];
    }
}
