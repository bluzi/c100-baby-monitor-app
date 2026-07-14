using BabyMonitor.Core.Data;

namespace BabyMonitor.Core.Monitor;

/// <summary>
/// ALRM-11: the alarm sounds, synthesised rather than shipped as audio files — so they are pure,
/// testable, and identical on every device. Each one is a single loopable cycle: play it end to end,
/// on repeat, and you have the alarm.
///
/// The set runs from calm to urgent on purpose. A parent choosing "soft chime" for the baby wants to
/// be woken gently; one choosing "siren" has slept through gentler things. The default sounds for the
/// two alarms differ, so they are told apart by ear out of the box (WATCH-2).
/// </summary>
public static class AlarmTones
{
    public const int SampleRate = 22_050;

    /// <summary>Never start silent: a first cycle nobody hears is a first cycle wasted.</summary>
    private const double StartGain = 0.35;

    /// <summary>How each sound is described to the user, calmest first.</summary>
    public static string Label(string sound) => sound switch
    {
        Settings.SoundSoftChime => "Soft chime",
        Settings.SoundRisingChime => "Rising chime",
        Settings.SoundLowPulse => "Low pulse",
        Settings.SoundUrgentBeep => "Urgent beep",
        Settings.SoundSiren => "Siren",
        _ => sound,
    };

    public static string Description(string sound) => sound switch
    {
        Settings.SoundSoftChime => "Gentle — wakes a light sleeper",
        Settings.SoundRisingChime => "Calm but insistent",
        Settings.SoundLowPulse => "Low and wrong-sounding — hard to mistake for a chime",
        Settings.SoundUrgentBeep => "Sharp and hard to ignore",
        Settings.SoundSiren => "Loudest — for heavy sleepers",
        _ => string.Empty,
    };

    /// <summary>
    /// One loopable cycle of <paramref name="sound"/> as 16-bit mono PCM. Every cycle ends with a gap:
    /// an alarm that never pauses becomes a texture the brain filters out — the repetition is what wakes
    /// people.
    /// </summary>
    public static short[] Pcm(string sound, int sampleRate = SampleRate)
    {
        switch (sound)
        {
            case Settings.SoundSoftChime:
            {
                var out_ = new short[(int)(2.4 * sampleRate)];
                Note(out_, 0.0, 1.1, 880.0, 0.55, sampleRate, partials: 3);
                return out_;
            }

            case Settings.SoundLowPulse:
            {
                var out_ = new short[(int)(2.0 * sampleRate)];
                Note(out_, 0.0, 0.5, 220.0, 0.85, sampleRate, partials: 4);
                Note(out_, 0.55, 0.5, 185.0, 0.85, sampleRate, partials: 4); // falls: reads as "wrong"
                return out_;
            }

            case Settings.SoundUrgentBeep:
            {
                var out_ = new short[(int)(1.6 * sampleRate)];
                for (var k = 0; k < 4; k++)
                {
                    Note(out_, k * 0.18, 0.12, 1000.0, 0.9, sampleRate, partials: 2);
                }

                return out_;
            }

            case Settings.SoundSiren:
            {
                var out_ = new short[(int)(1.8 * sampleRate)];
                Sweep(out_, 0.0, 0.6, 600.0, 1300.0, 0.95, sampleRate);
                Sweep(out_, 0.6, 0.6, 600.0, 1300.0, 0.95, sampleRate);
                return out_;
            }

            default:
            {
                // SOUND_RISING_CHIME (default): three ascending notes — calm, but it climbs at you.
                var out_ = new short[(int)(2.2 * sampleRate)];
                Note(out_, 0.0, 0.45, 660.0, 0.7, sampleRate, partials: 3);
                Note(out_, 0.30, 0.45, 880.0, 0.7, sampleRate, partials: 3);
                Note(out_, 0.60, 0.75, 1100.0, 0.75, sampleRate, partials: 3);
                return out_;
            }
        }
    }

    /// <summary>
    /// ALRM-14: how loud the alarm should be <paramref name="elapsedMs"/> into ringing, as a fraction of
    /// the user's chosen volume. It starts gentle and reaches full volume within a few seconds — enough
    /// to wake without a jolt, never so gentle that it fails to wake.
    /// </summary>
    public static double RampGain(long elapsedMs, long rampMs = 5_000)
    {
        if (elapsedMs >= rampMs)
        {
            return 1.0;
        }

        var p = (double)Math.Max(0, elapsedMs) / rampMs;
        return StartGain + ((1.0 - StartGain) * p);
    }

    private static void Note(
        short[] out_,
        double startSec,
        double durationSec,
        double freqHz,
        double amplitude,
        int sampleRate,
        int partials = 2)
    {
        var start = (int)(startSec * sampleRate);
        var n = (int)(durationSec * sampleRate);
        for (var i = 0; i < n; i++)
        {
            var idx = start + i;
            if (idx < 0 || idx >= out_.Length)
            {
                continue;
            }

            var t = (double)i / sampleRate;
            // Soft attack, exponential decay — a struck bell, not a click.
            var attack = Math.Min(1.0, t / 0.02);
            var decay = Math.Exp(-t / (durationSec * 0.45));
            var v = 0.0;
            for (var h = 1; h <= partials; h++)
            {
                v += Math.Sin(2 * Math.PI * freqHz * h * t) / h;
            }

            var sample = v * attack * decay * amplitude;
            var mixed = out_[idx] + (int)(sample * 32767);
            out_[idx] = (short)Math.Clamp(mixed, -32768, 32767);
        }
    }

    private static void Sweep(
        short[] out_,
        double startSec,
        double durationSec,
        double fromHz,
        double toHz,
        double amplitude,
        int sampleRate)
    {
        var start = (int)(startSec * sampleRate);
        var n = (int)(durationSec * sampleRate);
        var phase = 0.0;
        for (var i = 0; i < n; i++)
        {
            var idx = start + i;
            if (idx < 0 || idx >= out_.Length)
            {
                continue;
            }

            var p = (double)i / n;
            var hz = fromHz + ((toHz - fromHz) * p);
            phase += 2 * Math.PI * hz / sampleRate;
            var envelope = Math.Min(1.0, Math.Min(p / 0.05, (1 - p) / 0.05)); // no clicks at the ends
            var sample = Math.Sin(phase) * envelope * amplitude;
            var mixed = out_[idx] + (int)(sample * 32767);
            out_[idx] = (short)Math.Clamp(mixed, -32768, 32767);
        }
    }
}
