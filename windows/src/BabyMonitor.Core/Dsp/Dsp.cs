namespace BabyMonitor.Core.Dsp;

/// <summary>
/// The DSP behind the crying alarm (ALRM-3/13).
///
/// The whole difficulty is telling a baby crying in the room from the things that sound loud but are
/// not a baby: parents talking next door, a TV through a wall, traffic, a fan, a door slamming.
/// Loudness alone cannot do it — at a high sensitivity, muffled conversation is louder than a
/// sleeping baby. Three physical facts separate them:
///
///   1. PITCH. An infant cry has a fundamental around 300–600 Hz. An adult voice is 85–180 Hz (male)
///      or 165–255 Hz (female) — an octave or more below. Pitch alone rejects adult speech, whichever
///      room it comes from.
///   2. TONALITY. A cry is strongly periodic — a clear, sustained pitch. Rumble, fans, white noise
///      and bangs have no stable pitch at all.
///   3. BRIGHTNESS. Walls and doors are low-pass filters: speech from the next room arrives with its
///      high frequencies stripped away. A cry *in the room* keeps strong energy above 1 kHz. This is
///      what makes "loud voice through the door" fundamentally different from "baby here".
///
/// Any one of these can be fooled; together they are hard to fool by accident.
/// </summary>
public static class Analysis
{
    /// <summary>Cry pitch range (ALRM-3). Comfortably above an adult voice, comfortably below a whistle.</summary>
    public const double CryPitchMinHz = 250.0;
    public const double CryPitchMaxHz = 700.0;

    // Pitch search covers adult voices too, so an adult is *found* at their own pitch and then
    // rejected for being too low — rather than being mistaken for a quiet baby.
    private const double PitchSearchMinHz = 80.0;
    private const double PitchSearchMaxHz = 900.0;
    private const int PitchDecimation = 4; // 48 kHz → 12 kHz: plenty for a 900 Hz search, 16× cheaper

    /// <summary>In-place iterative radix-2 FFT. re/im length must be a power of two.</summary>
    public static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        if (n != im.Length || (n & (n - 1)) != 0)
        {
            throw new ArgumentException("fft: size must be a power of two");
        }

        var j = 0;
        for (var i = 0; i < n - 1; i++)
        {
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }

            var k = n >> 1;
            while (k <= j)
            {
                j -= k;
                k >>= 1;
            }

            j += k;
        }

        var len = 2;
        while (len <= n)
        {
            var ang = -2 * Math.PI / len;
            var wRe = Math.Cos(ang);
            var wIm = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                var curRe = 1.0;
                var curIm = 0.0;
                for (var k = 0; k < len / 2; k++)
                {
                    var aRe = re[i + k];
                    var aIm = im[i + k];
                    var bRe = (re[i + k + (len / 2)] * curRe) - (im[i + k + (len / 2)] * curIm);
                    var bIm = (re[i + k + (len / 2)] * curIm) + (im[i + k + (len / 2)] * curRe);
                    re[i + k] = aRe + bRe;
                    im[i + k] = aIm + bIm;
                    re[i + k + (len / 2)] = aRe - bRe;
                    im[i + k + (len / 2)] = aIm - bIm;
                    var nRe = (curRe * wRe) - (curIm * wIm);
                    curIm = (curRe * wIm) + (curIm * wRe);
                    curRe = nRe;
                }
            }

            len <<= 1;
        }
    }

    /// <summary>
    /// Fundamental pitch by normalised autocorrelation, with the sub-harmonic guard that matters
    /// here: a periodic signal also correlates at 2×, 3×… its period, so a naive peak-pick reports a
    /// baby at *half* her real pitch — i.e. right in the adult range — and the cry gets thrown away.
    /// We therefore prefer the shortest lag that is nearly as good as the best one.
    /// </summary>
    public static (double PitchHz, double Correlation) EstimatePitch(double[] x, int sampleRate)
    {
        var minLag = Math.Max(2, (int)(sampleRate / PitchSearchMaxHz));
        var maxLag = (int)(sampleRate / PitchSearchMinHz);
        if (x.Length < 2 * maxLag)
        {
            return (0.0, 0.0);
        }

        var energy = 0.0;
        foreach (var v in x)
        {
            energy += v * v;
        }

        if (energy <= 1e-9)
        {
            return (0.0, 0.0);
        }

        var n = x.Length - maxLag;
        var r = new double[maxLag + 1];
        var best = 0.0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            var num = 0.0;
            var e0 = 0.0;
            var e1 = 0.0;
            for (var i = 0; i < n; i++)
            {
                var a = x[i];
                var b = x[i + lag];
                num += a * b;
                e0 += a * a;
                e1 += b * b;
            }

            var denom = Math.Sqrt(e0 * e1);
            var corr = denom > 1e-12 ? num / denom : 0.0;
            r[lag] = corr;
            if (corr > best)
            {
                best = corr;
            }
        }

        if (best <= 0.0)
        {
            return (0.0, 0.0);
        }

        // Octave fix: a periodic signal correlates at every multiple of its period, so the highest
        // peak may sit at 2× or 3× the true one — which would report a crying baby at half her pitch,
        // squarely in the adult range, and throw the cry away. Take the EARLIEST strong *peak*, not
        // merely the earliest lag that is strong: around a real peak the correlation is smooth, so a
        // plain threshold-crossing would land short of it and read the pitch too high.
        var chosen = 0;
        var chosenCorr = 0.0;
        for (var lag = minLag + 1; lag < maxLag; lag++)
        {
            var isPeak = r[lag] > r[lag - 1] && r[lag] >= r[lag + 1];
            if (isPeak && r[lag] >= 0.85 * best)
            {
                chosen = lag;
                chosenCorr = r[lag];
                break;
            }
        }

        if (chosen == 0)
        {
            // No clear peak: fall back to the strongest lag.
            for (var lag = minLag; lag <= maxLag; lag++)
            {
                if (r[lag] == best)
                {
                    chosen = lag;
                    chosenCorr = best;
                    break;
                }
            }
        }

        if (chosen == 0)
        {
            return (0.0, 0.0);
        }

        // Whole-sample lags quantise pitch coarsely (tens of Hz up at cry pitches), which would blur
        // the very boundary the alarm decides on. Interpolate the correlation peak for the real period.
        var period = (double)chosen;
        if (chosen > minLag && chosen < maxLag)
        {
            var a = r[chosen - 1];
            var b = r[chosen];
            var c = r[chosen + 1];
            var denom = a - (2 * b) + c;
            if (Math.Abs(denom) > 1e-12)
            {
                var delta = 0.5 * (a - c) / denom;
                if (delta is > -1.0 and < 1.0)
                {
                    period = chosen + delta;
                }
            }
        }

        return (sampleRate / period, Math.Clamp(chosenCorr, 0.0, 1.0));
    }

    /// <summary>Cheap decimation (box average). Anti-aliasing enough for a pitch search below 900 Hz.</summary>
    public static double[] Decimate(short[] pcm, int factor)
    {
        if (factor <= 1)
        {
            var direct = new double[pcm.Length];
            for (var i = 0; i < pcm.Length; i++)
            {
                direct[i] = pcm[i] / 32768.0;
            }

            return direct;
        }

        var out_ = new double[pcm.Length / factor];
        for (var i = 0; i < out_.Length; i++)
        {
            var sum = 0.0;
            for (var k = 0; k < factor; k++)
            {
                sum += pcm[(i * factor) + k] / 32768.0;
            }

            out_[i] = sum / factor;
        }

        return out_;
    }

    /// <summary>Analyze one PCM window: loudness, pitch, tonality and spectral shape.</summary>
    public static WindowMetrics AnalyzeWindow(short[] pcm, int sampleRate)
    {
        var n = HighestPowerOfTwo(pcm.Length);
        if (n < 2)
        {
            return new WindowMetrics(0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
        }

        if (n > 4096)
        {
            n = 4096;
        }

        var sumSq = 0.0;
        var peak = 0.0;
        foreach (var s in pcm)
        {
            var v = s / 32768.0;
            sumSq += v * v;
            var a = Math.Abs(v);
            if (a > peak)
            {
                peak = a;
            }
        }

        var rms = Math.Sqrt(sumSq / pcm.Length);

        var re = new double[n];
        var im = new double[n];
        for (var i = 0; i < n; i++)
        {
            // Hann window keeps leakage from smearing rumble into the bands we care about.
            var w = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
            re[i] = pcm[i] / 32768.0 * w;
        }

        Fft(re, im);

        var binHz = (double)sampleRate / n;
        var total = 0.0;
        var low = 0.0; // < 300 Hz: rumble, and the muffled remains of speech through a wall
        var voice = 0.0; // 300–5000 Hz: where a cry lives
        var high = 0.0; // 1000–5000 Hz: the part a wall takes away
        for (var bin = 1; bin < n / 2; bin++)
        {
            var energy = (re[bin] * re[bin]) + (im[bin] * im[bin]);
            total += energy;
            var hz = bin * binHz;
            if (hz < 300.0)
            {
                low += energy;
            }
            else if (hz <= 5000.0)
            {
                voice += energy;
                if (hz >= 1000.0)
                {
                    high += energy;
                }
            }
        }

        var (pitchHz, tonality) = EstimatePitch(Decimate(pcm, PitchDecimation), sampleRate / PitchDecimation);
        return new WindowMetrics(
            Rms: rms,
            Peak: peak,
            PitchHz: pitchHz,
            Tonality: tonality,
            Brightness: voice > 0 ? high / voice : 0.0,
            LowRatio: total > 0 ? low / total : 0.0);
    }

    private static int HighestPowerOfTwo(int value)
    {
        if (value <= 0)
        {
            return 0;
        }

        var n = 1;
        while (n <= value / 2)
        {
            n <<= 1;
        }

        return n;
    }
}

/// <summary>What one analysis window tells us about the sound in the room.</summary>
/// <param name="Rms">Loudness, 0..1.</param>
/// <param name="Peak">Peak amplitude, 0..1.</param>
/// <param name="PitchHz">Fundamental pitch in Hz, or 0 when the sound has no clear pitch at all.</param>
/// <param name="Tonality">How strongly periodic the sound is, 0..1. A cry is tonal; a fan or a bang is not.</param>
/// <param name="Brightness">Fraction of voice-band energy above 1 kHz. Muffled = dark; in-room = bright.</param>
/// <param name="LowRatio">Fraction of all energy below 300 Hz — rumble, and speech through a wall.</param>
public sealed record WindowMetrics(
    double Rms,
    double Peak,
    double PitchHz,
    double Tonality,
    double Brightness,
    double LowRatio);
