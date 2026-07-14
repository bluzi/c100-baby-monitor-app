using BabyMonitor.Core.Data;
using BabyMonitor.Core.Monitor;
using Xunit;

namespace BabyMonitor.Core.Tests;

// LIVE-6: level is dB above an adaptive ambient baseline — room tone reads quiet, louder events read
// immediately.

public class LevelMeterTest
{
    private static (double Level, long At) Feed(
        LevelMeter meter,
        double rms,
        double seconds,
        long startMs,
        int hz = 10)
    {
        var last = 0.0;
        var t = startMs;
        var steps = (int)(seconds * hz);
        for (var i = 0; i < steps; i++)
        {
            t += 1000 / hz;
            last = meter.Process(rms, rms * 1.5, t);
        }

        return (last, t);
    }

    /// <summary>
    /// A steady background the way a real camera delivers one. Measured on a real C100 in a quiet
    /// room: per-window rms wanders across ±5 dB (sensor self-noise plus opus coding noise near
    /// silence), peaks ride ~10 dB above rms, and slow swells (AC cycling) add a couple more.
    /// Perfectly constant noise is far too clean to catch the flutter the meter must flatten.
    /// </summary>
    private static (List<double> Levels, long At) Flutter(
        LevelMeter meter,
        double baseRms,
        double seconds,
        long startMs,
        Random rnd,
        int hz = 10)
    {
        var levels = new List<double>();
        var t = startMs;
        var steps = (int)(seconds * hz);
        for (var i = 0; i < steps; i++)
        {
            t += 1000 / hz;
            var swellDb = 1.5 * Math.Sin(2 * Math.PI * t / 8000.0); // AC cycling over ~8 s
            // Sum of two uniforms ≈ the measured window-to-window spread (±5 dB, mostly ±2).
            var jitterDb = ((rnd.NextDouble() * 5.0) - 2.5) + ((rnd.NextDouble() * 5.0) - 2.5);
            var rms = baseRms * Math.Pow(10.0, (swellDb + jitterDb) / 20.0);
            var crest = 2.8 + (rnd.NextDouble() * 0.8); // peaks ~10 dB over rms, wandering a little
            levels.Add(meter.Process(rms, rms * crest, t));
        }

        return (levels, t);
    }

    [Fact(DisplayName = "LIVE-6 residual flutter displays as a quiet room — real levels display unchanged")]
    public void DisplaySquelch()
    {
        Assert.Equal(0.0, LevelMeter.DisplayLevelDb(0.0), 9);
        Assert.Equal(0.0, LevelMeter.DisplayLevelDb(1.9), 9); // ambient remainder is not "activity"
        Assert.Equal(2.0, LevelMeter.DisplayLevelDb(2.0), 9);
        Assert.Equal(14.7, LevelMeter.DisplayLevelDb(14.7), 9);
    }

    [Fact(DisplayName = "LIVE-6 silence sits at zero")]
    public void SilenceIsZero()
    {
        var (level, _) = Feed(new LevelMeter(), 0.0, seconds: 5.0, startMs: 1_000);
        Assert.Equal(0.0, level, 1);
    }

    [Fact(DisplayName = "LIVE-6 a loud onset over a quiet room reads immediately")]
    public void LoudOnsetReadsImmediately()
    {
        var meter = new LevelMeter();
        var (_, t1) = Feed(meter, 0.001, seconds: 20.0, startMs: 1_000); // quiet room
        var (level, _) = Feed(meter, 0.3, seconds: 0.5, startMs: t1); // crying starts
        Assert.True(level > 10.0, $"expected a strong level, got {level}");
    }

    [Fact(DisplayName = "LIVE-6 constant background noise adapts back toward quiet")]
    public void BackgroundAdapts()
    {
        var meter = new LevelMeter();
        // White-noise machine switches on and stays on.
        var (initial, t1) = Feed(meter, 0.05, seconds: 3.0, startMs: 1_000);
        var (settled, _) = Feed(meter, 0.05, seconds: 120.0, startMs: t1);
        Assert.True(initial >= 0.0, $"initially audible ({initial})");
        Assert.True(settled < 2.0, $"should settle near the floor, got {settled}");
    }

    [Fact(DisplayName = "LIVE-6 an event louder than the settled background still stands out")]
    public void EventsOverBackgroundStandOut()
    {
        var meter = new LevelMeter();
        var (_, t1) = Feed(meter, 0.02, seconds: 60.0, startMs: 1_000); // fan noise, settled
        var (level, _) = Feed(meter, 0.4, seconds: 0.5, startMs: t1); // cry over the fan
        Assert.True(level > 8.0, $"cry should stand out over settled fan, got {level}");
    }

    [Fact(DisplayName = "LIVE-6 a fluttering background reads flat — and never loud enough to alarm")]
    public void FlutterReadsFlat()
    {
        var meter = new LevelMeter();
        var rnd = new Random(42);
        var (_, t1) = Flutter(meter, 0.05, seconds: 60.0, startMs: 1_000, rnd: rnd); // settle in
        var (levels, _) = Flutter(meter, 0.05, seconds: 120.0, startMs: t1, rnd: rnd);
        levels.Sort();
        var median = levels[levels.Count / 2];
        Assert.True(median <= 0.5, $"a steady room should read ~0, got median {median}");

        // The reliability half: ambient flutter alone must never satisfy the alarm's loudness bar,
        // even at the most sensitive setting — quiet rooms don't take defences away.
        var bar = CryCalibration.SensitivityThresholdDb(Settings.SensitivityMax);
        Assert.True(levels[^1] < bar, $"flutter reached the {bar} dB bar (max was {levels[^1]})");
    }

    [Fact(DisplayName = "LIVE-6 a cry onset over a fluttering background still reads immediately")]
    public void CryOverFlutter()
    {
        var meter = new LevelMeter();
        var rnd = new Random(7);
        var (_, t1) = Flutter(meter, 0.05, seconds: 60.0, startMs: 1_000, rnd: rnd);
        var (level, _) = Feed(meter, 0.5, seconds: 0.5, startMs: t1);
        Assert.True(level > 10.0, $"a cry over flutter should stand out, got {level}");
    }

    [Fact(DisplayName = "LIVE-6 ongoing crying with breaths between bursts is never absorbed into the baseline")]
    public void OngoingCryingIsNeverAbsorbed()
    {
        // The breaths anchor the floor to the ROOM. If the baseline ever tracked the typical recent
        // level instead of the quiet dips, minutes of crying would fade to "usual" and a still-crying
        // baby could fail to re-alarm after an acknowledgment (ALRM-5).
        var meter = new LevelMeter();
        var (_, t0) = Feed(meter, 0.02, seconds: 60.0, startMs: 1_000); // fan, settled
        var t = t0;
        var lastBurst = 0.0;
        for (var i = 0; i < 112; i++)
        {
            // ~3 minutes of 1 s cry bursts with 0.6 s breaths.
            var (burst, t1) = Feed(meter, 0.4, seconds: 1.0, startMs: t);
            var (_, t2) = Feed(meter, 0.02, seconds: 0.6, startMs: t1);
            lastBurst = burst;
            t = t2;
        }

        Assert.True(lastBurst > 8.0, $"after minutes of crying, bursts must still stand out, got {lastBurst}");
    }
}
