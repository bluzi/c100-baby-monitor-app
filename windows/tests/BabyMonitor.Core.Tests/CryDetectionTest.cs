using BabyMonitor.Core.Data;
using BabyMonitor.Core.Dsp;
using BabyMonitor.Core.Monitor;
using Xunit;

namespace BabyMonitor.Core.Tests;

// ALRM-3 / ALRM-13. These run the REAL analysis over synthesised versions of what a nursery hears at
// night, at the MOST SENSITIVE setting — where false alarms actually happen.

public class CryDetectionTest
{
    private const int Window = 2048;
    private const long WindowMs = Window * 1000L / Sounds.Sr;

    /// <summary>The most sensitive setting the user can pick — where a lesser detector cries wolf.</summary>
    private static readonly double MostSensitiveDb =
        CryCalibration.SensitivityThresholdDb(Settings.SensitivityMax);

    private static BabyNoiseDetector Detector() =>
        new() { Enabled = true, ThresholdDb = MostSensitiveDb };

    /// <summary>Feed <paramref name="seconds"/> of one sound to a detector; did it ever alarm?</summary>
    private static bool RunSound(
        BabyNoiseDetector detector,
        Func<int, short[]> sound,
        double seconds,
        double levelDb = 20.0)
    {
        long now = 0;
        var fired = false;
        var windows = (int)(seconds * Sounds.Sr / Window);
        var pcm = sound(Window * windows);
        for (var w = 0; w < windows; w++)
        {
            var chunk = pcm[(w * Window)..((w + 1) * Window)];
            now += WindowMs;
            var metrics = Analysis.AnalyzeWindow(chunk, Sounds.Sr);
            if (detector.OnWindow(levelDb, metrics, WindowMs, now))
            {
                fired = true;
            }
        }

        return fired;
    }

    [Fact(DisplayName = "ALRM-3 a baby crying in the room triggers the alarm")]
    public void BabyCryTriggers()
    {
        Assert.True(RunSound(Detector(), n => Sounds.BabyCry(n), seconds: 4.0));
    }

    [Fact(DisplayName = "ALRM-3 crying triggers across the whole range of infant pitches")]
    public void EveryInfantPitchTriggers()
    {
        foreach (var f0 in new[] { 300.0, 400.0, 500.0, 600.0 })
        {
            var fired = RunSound(Detector(), n => Sounds.BabyCry(n, f0), seconds: 4.0);
            Assert.True(fired, $"a cry at {f0}Hz must trigger the alarm");
        }
    }

    // --- ALRM-13: the things that must NEVER wake a parent -----------------------------------

    [Fact(DisplayName = "ALRM-13 adults talking in the next room never trigger — even at maximum sensitivity")]
    public void MuffledSpeechNeverTriggers()
    {
        // The exact complaint: loud, voice-like, but muffled by a wall — and an octave too low.
        Assert.False(RunSound(
            Detector(),
            n => Sounds.MuffledSpeechThroughWall(n),
            seconds: 8.0,
            levelDb: 24.0));
    }

    [Fact(DisplayName = "ALRM-13 a television through the wall never triggers")]
    public void TelevisionNeverTriggers()
    {
        Assert.False(RunSound(
            Detector(),
            n => Sounds.MutedTelevisionThroughWall(n),
            seconds: 8.0,
            levelDb: 24.0));
    }

    [Fact(DisplayName = "ALRM-13 an adult talking in the room is not a baby")]
    public void AdultVoiceNeverTriggers()
    {
        Assert.False(RunSound(Detector(), n => Sounds.AdultVoiceInRoom(n), seconds: 8.0, levelDb: 24.0));
    }

    [Fact(DisplayName = "ALRM-13 traffic and appliance rumble never trigger")]
    public void RumbleNeverTriggers()
    {
        Assert.False(RunSound(Detector(), n => Sounds.Rumble(n), seconds: 8.0, levelDb: 24.0));
    }

    [Fact(DisplayName = "ALRM-13 a fan or white-noise machine never triggers")]
    public void WhiteNoiseNeverTriggers()
    {
        Assert.False(RunSound(Detector(), n => Sounds.WhiteNoise(n), seconds: 8.0, levelDb: 24.0));
    }

    [Fact(DisplayName = "ALRM-13 a door slam never triggers")]
    public void DoorSlamNeverTriggers()
    {
        Assert.False(RunSound(Detector(), n => Sounds.DoorSlam(n), seconds: 4.0, levelDb: 24.0));
    }

    [Fact(DisplayName = "ALRM-3 silence never triggers")]
    public void SilenceNeverTriggers()
    {
        Assert.False(RunSound(Detector(), n => Sounds.Silence(n), seconds: 4.0, levelDb: 0.0));
    }

    // --- the toggle and the ring lifecycle ----------------------------------------------------

    [Fact(DisplayName = "ALRM-3 with the toggle off nothing triggers — however hard the baby cries")]
    public void ToggleOffNeverTriggers()
    {
        var off = new BabyNoiseDetector { Enabled = false, ThresholdDb = MostSensitiveDb };
        Assert.False(RunSound(off, n => Sounds.BabyCry(n), seconds: 6.0));
    }

    [Fact(DisplayName = "ALRM-5 a ringing alarm suppresses new triggers until acknowledged")]
    public void RingingSuppresses()
    {
        var det = Detector();
        Assert.True(RunSound(det, n => Sounds.BabyCry(n), seconds: 4.0));
        det.Suppressed = true; // the engine sets this while the alarm rings
        Assert.False(RunSound(det, n => Sounds.BabyCry(n), seconds: 4.0));
    }

    [Fact(DisplayName = "ALRM-5 after acknowledgment the alarm stays quiet for the cooldown — then may trigger again")]
    public void CooldownThenReArm()
    {
        var det = Detector();
        long now = 0;

        // Like RunSound, but on one continuous clock so the snooze deadline means something.
        bool CryFor(double seconds)
        {
            var fired = false;
            var windows = (int)(seconds * Sounds.Sr / Window);
            var pcm = Sounds.BabyCry(Window * windows);
            for (var w = 0; w < windows; w++)
            {
                now += WindowMs;
                var metrics = Analysis.AnalyzeWindow(pcm[(w * Window)..((w + 1) * Window)], Sounds.Sr);
                if (det.OnWindow(20.0, metrics, WindowMs, now))
                {
                    fired = true;
                }
            }

            return fired;
        }

        Assert.True(CryFor(4.0));
        det.Snooze(now + 30_000); // what the engine does on acknowledgment
        Assert.False(CryFor(25.0)); // crying inside the cooldown must not re-alarm
        Assert.True(CryFor(10.0)); // crying after the cooldown must alarm again
    }
}
