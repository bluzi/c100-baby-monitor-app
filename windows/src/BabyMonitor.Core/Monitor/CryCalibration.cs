using BabyMonitor.Core.Data;

namespace BabyMonitor.Core.Monitor;

/// <summary>
/// ALRM-2 / ALRM-15/16/17: the single sensitivity dial and the per-camera learning behind it.
///
/// Sensitivity (1..10, middle by default) is the ONLY detection control the user has. It maps to how
/// loud a sound must be (dB above the room's own baseline) before the cry gates in
/// <see cref="BabyNoiseDetector"/> are even consulted. The character gates themselves (pitch,
/// tonality, brightness) never move: whatever the dial or the learning says, a fan or next-door talk
/// can never become "the baby" (ALRM-13) — and, just as deliberately, learning can never redefine the
/// baby's own sound away. All either can change is the loudness bar.
///
/// Learning is asymmetric by nature: parents tell us about false alarms, but a MISSED cry is silent —
/// nobody files a report at 3am for the alarm that didn't ring. Unbounded "stricter" learning would
/// therefore slowly deafen the monitor. Hence hard bounds: at most <see cref="MaxSteps"/> steps above
/// the slider, never below it, and always visible and resettable in settings (ALRM-17).
/// </summary>
public static class CryCalibration
{
    /// <summary>One learning step, in dB — also the loudness distance between two neighbouring slider stops.</summary>
    private const double StepDb = 2.0;

    /// <summary>ALRM-16: learning stops here. At worst the baby must be ~6 dB louder — never inaudible.</summary>
    public const int MaxSteps = 3;

    /// <summary>ALRM-2: the loudness bar for a sensitivity setting, in dB above the room's baseline.</summary>
    public static double SensitivityThresholdDb(int sensitivity) =>
        LevelMeter.LevelMax -
        (StepDb * Math.Clamp(sensitivity, Settings.SensitivityMin, Settings.SensitivityMax));

    /// <summary>
    /// ALRM-16: the bar actually in force — the slider's bar plus the learned steps, clamped to the top
    /// of the level scale so the trigger point always stays where it can be seen and tuned.
    /// </summary>
    public static double EffectiveThresholdDb(int sensitivity, int falseAlarmSteps) =>
        Math.Min(
            SensitivityThresholdDb(sensitivity) + (StepDb * Math.Clamp(falseAlarmSteps, 0, MaxSteps)),
            LevelMeter.LevelMax);

    /// <summary>ALRM-16: "no, false alarm" — one step harder to trigger, up to the cap.</summary>
    public static int AfterFalseAlarm(int steps) => Math.Clamp(steps + 1, 0, MaxSteps);

    /// <summary>ALRM-16: "yes, real cry" — undo one step, never below the slider's own setting.</summary>
    public static int AfterRealCry(int steps) => Math.Clamp(steps - 1, 0, MaxSteps);

    /// <summary>ALRM-15: only the crying alarm asks for an answer — a feed-drop alarm is not a detection.</summary>
    public static bool AsksForCryFeedback(AlarmKind kind) => kind == AlarmKind.BabyNoise;
}
