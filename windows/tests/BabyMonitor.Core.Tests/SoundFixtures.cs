namespace BabyMonitor.Core.Tests;

/// <summary>
/// Synthetic stand-ins for the sounds a nursery actually hears at night (ALRM-3/13). They are not
/// recordings, but they carry the physical properties the detector reasons about: an infant's high
/// fundamental and bright harmonics; an adult's low fundamental; the high-frequency roll-off that a
/// wall or a door imposes; the pitchlessness of fans, rumble and bangs.
/// </summary>
internal static class Sounds
{
    internal const int Sr = 48_000;

    /// <summary>A baby crying in the room: high fundamental, strong bright harmonics, fully present.</summary>
    internal static short[] BabyCry(int samples, double f0 = 450.0, double amplitude = 0.30) =>
        HarmonicVoice(samples, f0, harmonics: 10, amplitude: amplitude, harmonicGain: (h, _) => 1.0 / h);

    /// <summary>An adult talking IN the room: same fullness, but an octave-plus lower — not a baby.</summary>
    internal static short[] AdultVoiceInRoom(int samples, double f0 = 120.0, double amplitude = 0.30) =>
        HarmonicVoice(samples, f0, harmonics: 30, amplitude: amplitude, harmonicGain: (h, _) => 1.0 / h);

    /// <summary>
    /// Adults talking in the NEXT ROOM — the false positive that matters. Loud (the user has the
    /// threshold at maximum), but a wall has stripped the highs: everything above ~700 Hz is gone.
    /// </summary>
    internal static short[] MuffledSpeechThroughWall(int samples, double f0 = 130.0, double amplitude = 0.45) =>
        HarmonicVoice(samples, f0, harmonics: 30, amplitude: amplitude, harmonicGain: (h, hz) =>
        {
            // A wall/door is a low-pass filter: ~12 dB per octave above a few hundred Hz.
            var rolloff = 1.0 / (1.0 + (hz / 500.0 * (hz / 500.0)));
            return rolloff / h;
        });

    /// <summary>
    /// A TV through the wall: like muffled speech, but with a pitch that wanders — still dark, still
    /// low. The other classic 3am false alarm.
    /// </summary>
    internal static short[] MutedTelevisionThroughWall(int samples, double amplitude = 0.40)
    {
        var rnd = new Random(11);
        var out_ = new short[samples];
        var phase = 0.0;
        for (var i = 0; i < samples; i++)
        {
            var f0 = 110.0 + (60.0 * Math.Sin(2 * Math.PI * 0.7 * i / Sr)); // speech-like pitch movement
            phase += 2 * Math.PI * f0 / Sr;
            var v = 0.0;
            for (var h = 1; h <= 20; h++)
            {
                var hz = f0 * h;
                var rolloff = 1.0 / (1.0 + (hz / 450.0 * (hz / 450.0)));
                v += rolloff / h * Math.Sin(phase * h);
            }

            v += 0.05 * ((rnd.NextDouble() * 2) - 1);
            out_[i] = Clip(v * amplitude);
        }

        return out_;
    }

    /// <summary>Traffic / appliance rumble: loud, but almost all of it below 200 Hz and with no pitch.</summary>
    internal static short[] Rumble(int samples, double amplitude = 0.45)
    {
        var rnd = new Random(3);
        var out_ = new short[samples];
        var lp = 0.0;
        for (var i = 0; i < samples; i++)
        {
            var white = (rnd.NextDouble() * 2) - 1;
            lp += 0.02 * (white - lp); // heavy low-pass → rumble
            out_[i] = Clip(lp * 6 * amplitude);
        }

        return out_;
    }

    /// <summary>A fan / white-noise machine: broadband, bright even — but utterly pitchless.</summary>
    internal static short[] WhiteNoise(int samples, double amplitude = 0.35)
    {
        var rnd = new Random(5);
        var out_ = new short[samples];
        for (var i = 0; i < samples; i++)
        {
            out_[i] = Clip(((rnd.NextDouble() * 2) - 1) * amplitude);
        }

        return out_;
    }

    /// <summary>A door slam: very loud, very bright, very short — and gone.</summary>
    internal static short[] DoorSlam(int samples, double amplitude = 0.9)
    {
        var rnd = new Random(9);
        var out_ = new short[samples];
        for (var i = 0; i < samples; i++)
        {
            var env = Math.Exp(-i / (Sr * 0.05)); // ~50 ms decay
            out_[i] = Clip(((rnd.NextDouble() * 2) - 1) * env * amplitude);
        }

        return out_;
    }

    internal static short[] Silence(int samples) => new short[samples];

    internal static short[] Sine(double freqHz, int sampleRate, int n, double amplitude = 0.5)
    {
        var out_ = new short[n];
        for (var i = 0; i < n; i++)
        {
            out_[i] = (short)(amplitude * 32767 * Math.Sin(2 * Math.PI * freqHz * i / sampleRate));
        }

        return out_;
    }

    private static short[] HarmonicVoice(
        int samples,
        double f0,
        int harmonics,
        double amplitude,
        Func<int, double, double> harmonicGain,
        int seed = 7)
    {
        var rnd = new Random(seed);
        var out_ = new short[samples];
        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / Sr;
            var v = 0.0;
            for (var h = 1; h <= harmonics; h++)
            {
                var hz = f0 * h;
                if (hz >= Sr / 2)
                {
                    break;
                }

                v += harmonicGain(h, hz) * Math.Sin(2 * Math.PI * hz * t);
            }

            v += 0.01 * ((rnd.NextDouble() * 2) - 1); // a little breath noise; real sound is never pure
            out_[i] = Clip(v * amplitude);
        }

        return out_;
    }

    private static short Clip(double v) => (short)Math.Clamp((int)(v * 32767), -32768, 32767);
}
