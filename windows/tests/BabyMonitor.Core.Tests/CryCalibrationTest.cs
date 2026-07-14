using BabyMonitor.Core.Data;
using BabyMonitor.Core.Dsp;
using BabyMonitor.Core.Monitor;
using Xunit;

namespace BabyMonitor.Core.Tests;

// ALRM-2 (one sensitivity dial) and ALRM-15/16/17 (learning from the parent's answers). The mapping
// and the learning are pure logic; detector integration runs the REAL analysis.

public class CryCalibrationTest
{
    private const int Window = 2048;
    private const long WindowMs = Window * 1000L / Sounds.Sr;

    /// <summary>Feed a real synthesised cry to a detector at this bar; did it ever alarm?</summary>
    private static bool CryFires(double thresholdDb, double levelDb, double seconds = 4.0)
    {
        var detector = new BabyNoiseDetector { Enabled = true, ThresholdDb = thresholdDb };
        long now = 0;
        var fired = false;
        var windows = (int)(seconds * Sounds.Sr / Window);
        var pcm = Sounds.BabyCry(Window * windows);
        for (var w = 0; w < windows; w++)
        {
            now += WindowMs;
            var metrics = Analysis.AnalyzeWindow(pcm[(w * Window)..((w + 1) * Window)], Sounds.Sr);
            if (detector.OnWindow(levelDb, metrics, WindowMs, now))
            {
                fired = true;
            }
        }

        return fired;
    }

    // --- ALRM-2: the single sensitivity dial ------------------------------------------------

    [Fact(DisplayName = "ALRM-2 sensitivity runs 1 to 10 and defaults to the middle")]
    public void SensitivityScale()
    {
        Assert.Equal(1, Settings.SensitivityMin);
        Assert.Equal(10, Settings.SensitivityMax);
        Assert.Equal(5, Settings.SensitivityDefault);
        Assert.Equal(Settings.SensitivityDefault, new Settings().AlarmSensitivity);
    }

    [Fact(DisplayName = "ALRM-2 higher sensitivity means quieter sound can trigger")]
    public void HigherSensitivityNeedsLessLoudness()
    {
        for (var s = Settings.SensitivityMin; s < Settings.SensitivityMax; s++)
        {
            Assert.True(
                CryCalibration.SensitivityThresholdDb(s + 1) < CryCalibration.SensitivityThresholdDb(s),
                $"sensitivity {s + 1} must demand less loudness than {s}");
        }
    }

    [Fact(DisplayName = "ALRM-2 a quiet cry alarms at high sensitivity but not at low")]
    public void QuietCryOnlyAtHighSensitivity()
    {
        const double quietCryDb = 6.0;
        Assert.True(CryFires(CryCalibration.SensitivityThresholdDb(Settings.SensitivityMax), quietCryDb));
        Assert.False(CryFires(CryCalibration.SensitivityThresholdDb(Settings.SensitivityDefault), quietCryDb));
        Assert.False(CryFires(CryCalibration.SensitivityThresholdDb(Settings.SensitivityMin), quietCryDb));
    }

    [Fact(DisplayName = "ALRM-2 a sensitivity outside the scale is treated as its nearest end")]
    public void SensitivityIsClamped()
    {
        // Stored settings can be old or hand-edited; the mapping must never explode or go wild.
        Assert.Equal(
            CryCalibration.SensitivityThresholdDb(Settings.SensitivityMin),
            CryCalibration.SensitivityThresholdDb(-3),
            9);
        Assert.Equal(
            CryCalibration.SensitivityThresholdDb(Settings.SensitivityMax),
            CryCalibration.SensitivityThresholdDb(99),
            9);
    }

    // --- ALRM-15: which alarms ask for an answer ---------------------------------------------

    [Fact(DisplayName = "ALRM-15 only the crying alarm asks whether it was a false alarm")]
    public void OnlyCryingAsks()
    {
        Assert.True(CryCalibration.AsksForCryFeedback(AlarmKind.BabyNoise));
        Assert.False(CryCalibration.AsksForCryFeedback(AlarmKind.FeedDown));
    }

    [Fact(DisplayName = "ALRM-15 the answer goes to the camera that alarmed — once — and only while one is pending")]
    public void TheAnswerGoesToTheRightCamera()
    {
        var applied = new List<(string Did, bool WasCry)>();
        MonitorHub.OnCryFeedback = (did, wasCry) => applied.Add((did, wasCry));
        try
        {
            MonitorHub.SubmitCryFeedback(true); // nothing pending: a stray click learns nothing
            Assert.Empty(applied);

            MonitorHub.PendingCryFeedback.Value = "cam-1";
            MonitorHub.PendingCryFeedback.Value = "cam-2"; // a newer alarm replaces the question
            MonitorHub.SubmitCryFeedback(false);
            Assert.Equal(new[] { ("cam-2", false) }, applied);
            Assert.Null(MonitorHub.PendingCryFeedback.Value); // asked once, answered once

            MonitorHub.PendingCryFeedback.Value = "cam-1";
            MonitorHub.DismissCryFeedback(); // optional: dismissing learns nothing (ALRM-15)
            Assert.Null(MonitorHub.PendingCryFeedback.Value);
            Assert.Single(applied);
        }
        finally
        {
            MonitorHub.OnCryFeedback = null;
            MonitorHub.PendingCryFeedback.Value = null;
        }
    }

    // --- ALRM-16: bounded learning ------------------------------------------------------------

    [Fact(DisplayName = "ALRM-16 a false-alarm answer makes triggering one step harder")]
    public void FalseAlarmRaisesTheBar()
    {
        Assert.Equal(1, CryCalibration.AfterFalseAlarm(0));
        var before = CryCalibration.EffectiveThresholdDb(Settings.SensitivityDefault, 0);
        var after = CryCalibration.EffectiveThresholdDb(
            Settings.SensitivityDefault,
            CryCalibration.AfterFalseAlarm(0));
        Assert.True(after > before, "a false alarm must raise the bar");

        // Integration: a cry just over the slider's bar fired before the answer, not after.
        var borderline = before + 1.0;
        Assert.True(CryFires(before, borderline));
        Assert.False(CryFires(after, borderline));
    }

    [Fact(DisplayName = "ALRM-16 a confirmed cry undoes one step")]
    public void ConfirmedCryUndoesAStep()
    {
        Assert.Equal(0, CryCalibration.AfterRealCry(CryCalibration.AfterFalseAlarm(0)));
        Assert.Equal(1, CryCalibration.AfterRealCry(2));
    }

    [Fact(DisplayName = "ALRM-16 learning is bounded and never goes below the slider setting")]
    public void LearningIsBounded()
    {
        // However many false alarms are reported, the steps stop at the cap...
        var steps = 0;
        for (var i = 0; i < 20; i++)
        {
            steps = CryCalibration.AfterFalseAlarm(steps);
        }

        Assert.Equal(CryCalibration.MaxSteps, steps);

        // ...and however many cries are confirmed, tuning never drops below the slider's own bar.
        for (var i = 0; i < 20; i++)
        {
            steps = CryCalibration.AfterRealCry(steps);
        }

        Assert.Equal(0, steps);
        Assert.Equal(
            CryCalibration.SensitivityThresholdDb(Settings.SensitivityDefault),
            CryCalibration.EffectiveThresholdDb(Settings.SensitivityDefault, 0),
            9);
    }

    [Fact(DisplayName = "ALRM-16 corrupt stored steps are treated as their nearest bound")]
    public void CorruptStepsAreClamped()
    {
        Assert.Equal(
            CryCalibration.EffectiveThresholdDb(Settings.SensitivityDefault, 0),
            CryCalibration.EffectiveThresholdDb(Settings.SensitivityDefault, -7),
            9);
        Assert.Equal(
            CryCalibration.EffectiveThresholdDb(Settings.SensitivityDefault, CryCalibration.MaxSteps),
            CryCalibration.EffectiveThresholdDb(Settings.SensitivityDefault, 99),
            9);
    }

    [Fact(DisplayName = "ALRM-16 fully tuned-down detection still hears a loud cry — it never learns itself deaf")]
    public void ItNeverLearnsItselfDeaf()
    {
        var maxTuned = CryCalibration.EffectiveThresholdDb(Settings.SensitivityDefault, CryCalibration.MaxSteps);
        Assert.True(CryFires(maxTuned, levelDb: maxTuned + 2.0));

        // And the trigger point stays on the level scale, where it can be seen and tuned.
        for (var s = Settings.SensitivityMin; s <= Settings.SensitivityMax; s++)
        {
            Assert.True(CryCalibration.EffectiveThresholdDb(s, CryCalibration.MaxSteps) <= LevelMeter.LevelMax);
        }
    }
}
